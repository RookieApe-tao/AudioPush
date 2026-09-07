using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace YuPinTuiSong;

/// <summary>
/// 极简 HTTP 流媒体服务（TcpListener 手写协议，无需 URLACL/管理员权限）。
/// 路由：/ 播放页 · /stream MP3 兼容流 · /status 状态 · POST /rtc WebRTC 信令 · /ws WebSocket。
/// 鉴权：?token=xxx（也接受 X-Token 头 / Authorization: Bearer xxx）。
/// </summary>
public sealed class StreamServer
{
    readonly Config _cfg;
    readonly AudioHub _hub;
    readonly CancellationToken _ct;
    readonly WebRtcService? _webRtc;
    readonly WebSocketAudioService? _wsAudio;
    TcpListener? _listener;

    readonly record struct Request(
        string Method, string Target,
        Dictionary<string, string> Headers, byte[] Body);

    public StreamServer(Config cfg, AudioHub hub, CancellationToken ct,
        WebRtcService? webRtc = null, WebSocketAudioService? wsAudio = null)
    {
        _cfg = cfg;
        _hub = hub;
        _ct = ct;
        _webRtc = webRtc;
        _wsAudio = wsAudio;
    }

    public void Start()
    {
        _listener = new TcpListener(IPAddress.Any, _cfg.Port);
        _listener.Start();
        Log.Info($"HTTP 服务已监听 0.0.0.0:{_cfg.Port}");
        _ = Task.Run(AcceptLoop);
    }

    async Task AcceptLoop()
    {
        var listener = _listener!;
        while (!_ct.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await listener.AcceptTcpClientAsync(_ct); }
            catch (OperationCanceledException) { break; }
            catch (Exception) { if (_ct.IsCancellationRequested) break; continue; }
            _ = Task.Run(() => SafeHandleAsync(client));
        }
        try { listener.Stop(); } catch { }
    }

    async Task SafeHandleAsync(TcpClient client)
    {
        try { await HandleAsync(client); }
        catch { /* 客户端中断等，一律忽略 */ }
        finally { try { client.Close(); } catch { } }
    }

    async Task HandleAsync(TcpClient client)
    {
        client.NoDelay = true;
        using var stream = client.GetStream();

        var req = await ReadRequestAsync(stream);
        if (req is null) return;

        var (method, target, headers, body) = req.Value;
        int q = target.IndexOf('?');
        var path = q < 0 ? target : target[..q];
        var query = ParseQuery(q < 0 ? "" : target[(q + 1)..]);
        bool auth = IsAuthorized(query, headers);

        switch (path)
        {
            case "/" or "/index.html":
                await ServeIndexAsync(stream, headers, auth);
                break;
            case "/stream":
                await ServeStreamAsync(stream, method, auth);
                break;
            case "/status":
                await ServeStatusAsync(stream, auth);
                break;
            case "/rtc":
                await ServeRtcAsync(stream, method, auth, body);
                break;
            case "/ws":
                await ServeWsAsync(client, stream, query, headers);
                break;
            default:
                await WriteAsync(stream, 404, "text/plain; charset=utf-8", "not found");
                break;
        }
    }

    // ------------------------------------------------------------ 路由

    async Task ServeIndexAsync(NetworkStream stream, Dictionary<string, string> headers, bool auth)
    {
        string host = headers.TryGetValue("host", out var h) && !string.IsNullOrEmpty(h)
            ? h
            : $"{Utils.GetLanIps().FirstOrDefault() ?? "127.0.0.1"}:{_cfg.Port}";
        await WriteAsync(stream, 200, "text/html; charset=utf-8", BuildIndexHtml(host, auth));
    }

    async Task ServeStreamAsync(NetworkStream stream, string method, bool auth)
    {
        if (method is not ("GET" or "HEAD"))
        {
            await WriteAsync(stream, 405, "text/plain; charset=utf-8", "method not allowed");
            return;
        }
        if (!auth)
        {
            await WriteAsync(stream, 401, "text/plain; charset=utf-8",
                "unauthorized: 请使用带 ?token=xxx 的完整地址（见电脑端控制台或 logs 日志）");
            return;
        }

        var sb = new StringBuilder();
        sb.Append("HTTP/1.1 200 OK\r\n");
        sb.Append("Content-Type: ").Append(_hub.Mime).Append("\r\n");
        sb.Append("Cache-Control: no-cache, no-store\r\n");
        sb.Append("Connection: close\r\n");
        sb.Append("Access-Control-Allow-Origin: *\r\n");
        sb.Append("\r\n");
        await WriteRawAsync(stream, sb.ToString());

        if (method == "HEAD") return;

        if (_hub.Prelude.Length > 0)
            await stream.WriteAsync(_hub.Prelude, _ct);

        var channel = _hub.Join();
        try
        {
            using var idle = CancellationTokenSource.CreateLinkedTokenSource(_ct);
            var reader = channel.Reader;
            while (true)
            {
                idle.CancelAfter(TimeSpan.FromSeconds(60));   // 60 秒无数据视为空闲，断开
                bool has;
                try { has = await reader.WaitToReadAsync(idle.Token); }
                catch (OperationCanceledException) when (!_ct.IsCancellationRequested)
                {
                    Log.Info("一个客户端 60 秒未收到数据，已断开");
                    break;
                }
                if (!has) break;
                while (reader.TryRead(out var chunk))
                    await stream.WriteAsync(chunk, _ct);
            }
        }
        catch { /* 客户端断开 */ }
        finally { _hub.Leave(channel); }
    }

    async Task ServeStatusAsync(NetworkStream stream, bool auth)
    {
        if (!auth)
        {
            await WriteAsync(stream, 401, "text/plain; charset=utf-8", "unauthorized");
            return;
        }
        var json = JsonSerializer.Serialize(new
        {
            clients = _hub.ClientCount,
            webrtcPeers = _webRtc?.PeerCount ?? 0,
            wsClients = _wsAudio?.ClientCount ?? 0,
            mime = _hub.Mime,
            source = _hub.SourceInfo,
            startedAt = _hub.StartedAt.ToString("yyyy-MM-dd HH:mm:ss"),
            requireAuth = _cfg.RequireAuth,
        });
        await WriteAsync(stream, 200, "application/json", json);
    }

    async Task ServeRtcAsync(NetworkStream stream, string method, bool auth, byte[] body)
    {
        if (method != "POST")
        {
            await WriteAsync(stream, 405, "text/plain; charset=utf-8", "POST only");
            return;
        }
        if (!auth)
        {
            await WriteAsync(stream, 401, "text/plain; charset=utf-8",
                "unauthorized: 请使用带 ?token=xxx 的完整地址");
            return;
        }
        if (_webRtc == null)
        {
            await WriteAsync(stream, 500, "text/plain; charset=utf-8", "webrtc unavailable");
            return;
        }

        // 请求体是 JSON 信封 {"type":"offer","sdp":"v=0..."}；也兼容裸 SDP
        string offerSdp;
        var bodyText = Encoding.UTF8.GetString(body).TrimStart();
        if (bodyText.StartsWith("{"))
        {
            using var doc = JsonDocument.Parse(bodyText);
            offerSdp = doc.RootElement.GetProperty("sdp").GetString() ?? "";
        }
        else
        {
            offerSdp = bodyText;
        }
        if (string.IsNullOrWhiteSpace(offerSdp))
        {
            await WriteAsync(stream, 400, "text/plain; charset=utf-8", "missing sdp");
            return;
        }
        var answer = await _webRtc.HandleOfferAsync(offerSdp);
        if (answer == null)
            await WriteAsync(stream, 500, "text/plain; charset=utf-8", "webrtc handshake failed");
        else
            await WriteAsync(stream, 200, "application/json", answer);
    }

    async Task ServeWsAsync(TcpClient client, NetworkStream stream,
        Dictionary<string, string> query, Dictionary<string, string> headers)
    {
        // 鉴权
        if (!IsAuthorized(query, headers))
        {
            await WriteAsync(stream, 401, "text/plain; charset=utf-8", "unauthorized");
            return;
        }
        if (_wsAudio == null)
        {
            await WriteAsync(stream, 500, "text/plain; charset=utf-8", "websocket unavailable");
            return;
        }
        if (!headers.TryGetValue("sec-websocket-key", out var wsKey))
        {
            await WriteAsync(stream, 400, "text/plain; charset=utf-8", "not a websocket request");
            return;
        }

        // WebSocket 握手：RFC 6455 标准
        var acceptKey = Convert.ToBase64String(
            System.Security.Cryptography.SHA1.HashData(
                System.Text.Encoding.ASCII.GetBytes(wsKey + "258EAFA5-E914-47DA-95CA-5AB1E0495B53")));

        // 构造响应并一次性写入+刷新
        var response = $"HTTP/1.1 101 Switching Protocols\r\nConnection: Upgrade\r\nUpgrade: websocket\r\nSec-WebSocket-Accept: {acceptKey}\r\n\r\n";
        var responseBytes = System.Text.Encoding.ASCII.GetBytes(response);
        await stream.WriteAsync(responseBytes);
        await stream.FlushAsync();

        Log.Debug($"WS 握手完成: key={wsKey} accept={acceptKey}");

        // 包装为 WebSocket 并交给服务处理
        var ws = WebSocket.CreateFromStream(stream, new WebSocketCreationOptions
        {
            IsServer = true,
            KeepAliveInterval = TimeSpan.FromSeconds(30),
        });
        await _wsAudio.AcceptClientAsync(ws);
    }

    // ------------------------------------------------------------ 鉴权

    bool IsAuthorized(Dictionary<string, string> query, Dictionary<string, string> headers)
    {
        if (!_cfg.RequireAuth) return true;
        string? given =
            query.TryGetValue("token", out var t1) ? t1 :
            headers.TryGetValue("x-token", out var t2) ? t2 :
            headers.TryGetValue("authorization", out var t3) &&
                t3.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? t3[7..].Trim() :
            null;
        if (string.IsNullOrEmpty(given)) return false;
        return FixedTimeEquals(given, _cfg.Token);
    }

    static bool FixedTimeEquals(string a, string b)
    {
        int diff = a.Length ^ b.Length;
        for (int i = 0; i < a.Length && i < b.Length; i++)
            diff |= a[i] ^ b[i];
        return diff == 0;
    }

    // ------------------------------------------------------------ 页面

    string BuildIndexHtml(string host, bool auth)
    {
        const string tpl = """
<!doctype html>
<html lang="zh"><head><meta charset="utf-8">
<meta name="viewport" content="width=device-width,initial-scale=1">
<title>电脑声音</title></head>
<body style="font-family:-apple-system,'Segoe UI',sans-serif;max-width:640px;margin:40px auto;padding:0 16px;color:#222">
<h2>&#128266; 电脑声音实时转发</h2>
__BODY__
<hr style="border:none;border-top:1px solid #ddd;margin:24px 0">
<p style="color:#888;font-size:13px">看视频请用「低延迟模式」 · 电脑端 Ctrl+C 停止 · 配置见 config.json</p>
</body></html>
""";
        string body = auth ? BuildAuthBody() : """
<p>&#10060; <b>缺少或错误的访问令牌（token）。</b></p>
<p>请使用电脑端打印的完整地址，格式形如：<br>
<code>http://电脑IP:8818/?token=xxxxxxxx</code></p>
""";
        return tpl.Replace("__BODY__", body).Replace("__TOKEN__", _cfg.Token);
    }

    string BuildAuthBody()
    {
        const string body = """
<p><button id="btnLL" style="padding:10px 16px;font-size:16px;border:1px solid #ccc;border-radius:8px;background:#f6f6f6">&#127911; 低延迟模式（约 0.1 秒，看视频对口型）</button> <span id="llState" style="color:#888"></span></p>
<p><audio id="llAudio" autoplay></audio></p>
<p><button id="btnWake" style="padding:6px 12px;font-size:13px;border:1px solid #ccc;border-radius:6px;background:#f6f6f6">&#128161; 保持亮屏（推荐长时间收听）</button> <span id="wakeState" style="color:#888;font-size:12px"></span></p>
<p style="color:#999;font-size:12px">息屏后浏览器会冻结低延迟连接，亮屏自动重连；开启「保持亮屏」可不间断播放。</p>
<hr style="border:none;border-top:1px solid #ddd;margin:24px 0">
<p>兼容模式 MP3（延迟 2~5 秒，适合听歌）：</p>
<p><audio controls style="width:100%" src="/stream?token=__TOKEN__"></audio></p>
<script>
const TOKEN = "__TOKEN__";
const llState = document.getElementById('llState');
const wakeState = document.getElementById('wakeState');
let pc = null;
let reconnectTimer = null;
let wakeLock = null;
let wantLL = false;
window.__rtc = { state: 'idle', bytes: 0 };

async function startLL() {
  if (pc) { try { pc.close(); } catch (e) {} pc = null; }
  try {
    llState.textContent = '连接中…';
    pc = new RTCPeerConnection();
    window.__pc = pc;   // 诊断用
    pc.addTransceiver('audio', { direction: 'recvonly' });
    pc.ontrack = e => {
      const el = document.getElementById('llAudio');
      el.srcObject = e.streams[0];
      el.play().catch(() => {});   // 息屏后策略可能拦截播放，需显式恢复
    };
    pc.onconnectionstatechange = () => {
      llState.textContent = 'WebRTC: ' + pc.connectionState;
      window.__rtc.state = pc.connectionState;
      if ((pc.connectionState === 'disconnected' || pc.connectionState === 'failed' || pc.connectionState === 'closed') && wantLL) {
        scheduleReconnect();
      }
    };
    const offer = await pc.createOffer();
    await pc.setLocalDescription(offer);
    await new Promise(res => {
      if (pc.iceGatheringState === 'complete') return res();
      const t = setTimeout(res, 1200);
      pc.addEventListener('icegatheringstatechange', () => {
        if (pc.iceGatheringState === 'complete') { clearTimeout(t); res(); }
      });
    });
    const resp = await fetch('/rtc?token=' + TOKEN, {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify({ type: 'offer', sdp: pc.localDescription.sdp })
    });
    if (!resp.ok) throw new Error('信令 HTTP ' + resp.status);
    const ansText = await resp.text();
    window.__ansRaw = ansText;
    const ans = JSON.parse(ansText);
    await pc.setRemoteDescription({ type: ans.type, sdp: String(ans.sdp) });
  } catch (err) {
    llState.textContent = '出错: ' + err.message;
    if (wantLL) scheduleReconnect();
  }
}

function scheduleReconnect(delay = 1500) {
  if (reconnectTimer) return;
  llState.textContent = '已断开，稍后自动重连…';
  reconnectTimer = setTimeout(() => {
    reconnectTimer = null;
    if (wantLL) startLL();
  }, delay);
}

// 息屏/切后台再回来时，立即检查并按需重连
document.addEventListener('visibilitychange', () => {
  if (!document.hidden && wantLL && (!pc || pc.connectionState !== 'connected' && pc.connectionState !== 'connecting')) {
    scheduleReconnect(200);
  }
});

// 保持亮屏（Wake Lock），避免息屏导致断连
async function requestWake() {
  try {
    if ('wakeLock' in navigator) {
      wakeLock = await navigator.wakeLock.request('screen');
      wakeState.textContent = '已开启亮屏保持';
      wakeLock.addEventListener('release', () => { wakeLock = null; wakeState.textContent = ''; });
    } else {
      wakeState.textContent = '浏览器不支持，可用系统设置常亮';
    }
  } catch (e) { wakeState.textContent = '开启失败: ' + e.message; }
}
// Wake Lock 在切后台时会释放，回来时重新申请
document.addEventListener('visibilitychange', () => {
  if (!document.hidden && wakeLock === null && document.getElementById('btnWake').dataset.on === '1') requestWake();
});
document.getElementById('btnWake').addEventListener('click', async () => {
  if (wakeLock) { try { await wakeLock.release(); } catch (e) {} wakeLock = null; wakeState.textContent = ''; document.getElementById('btnWake').dataset.on = '0'; return; }
  document.getElementById('btnWake').dataset.on = '1';
  requestWake();
});

document.getElementById('btnLL').addEventListener('click', () => {
  wantLL = !wantLL;
  if (wantLL) { startLL(); }
  else {
    if (reconnectTimer) { clearTimeout(reconnectTimer); reconnectTimer = null; }
    if (pc) { try { pc.close(); } catch (e) {} pc = null; }
    llState.textContent = '已停止';
  }
});

setInterval(async () => {
  if (pc && pc.connectionState === 'connected') {
    try {
      const stats = await pc.getStats();
      stats.forEach(s => {
        if (s.type === 'inbound-rtp' && s.kind === 'audio') window.__rtc.bytes = s.bytesReceived;
      });
    } catch (e) {}
  }
}, 1000);
</script>
""";
        return body;
    }

    // ------------------------------------------------------------ HTTP 基础

    async Task<Request?> ReadRequestAsync(NetworkStream stream)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));
        var buf = new byte[96 * 1024];
        int len = 0, headerEnd = -1, total = -1;
        try
        {
            while (len < buf.Length)
            {
                if (total >= 0 && len >= total) break;
                int n = await stream.ReadAsync(buf.AsMemory(len, buf.Length - len), timeout.Token);
                if (n <= 0) break;
                len += n;
                if (total < 0)
                {
                    headerEnd = IndexOfHeaderEnd(buf, len);
                    if (headerEnd >= 0)
                    {
                        var headers = ParseHeaders(buf, headerEnd);
                        int cl = headers != null &&
                                 headers.TryGetValue("content-length", out var v) &&
                                 int.TryParse(v, out var c) && c >= 0 ? c : 0;
                        if (cl > 64 * 1024) cl = 64 * 1024;
                        total = headerEnd + 4 + cl;
                    }
                }
            }
        }
        catch (OperationCanceledException) { return null; }
        catch (IOException) { return null; }

        if (headerEnd < 0) return null;
        var headText = Encoding.Latin1.GetString(buf, 0, headerEnd);
        var lines = headText.Split("\r\n");
        var parts = lines[0].Split(' ');
        if (parts.Length < 2) return null;

        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines.Skip(1))
        {
            int c = line.IndexOf(':');
            if (c > 0) dict[line[..c].Trim()] = line[(c + 1)..].Trim();
        }

        int bodyLen = Math.Max(0, Math.Min(len, total) - (headerEnd + 4));
        var body = new byte[bodyLen];
        Buffer.BlockCopy(buf, headerEnd + 4, body, 0, bodyLen);

        return new Request(parts[0].ToUpperInvariant(), parts[1], dict, body);
    }

    static Dictionary<string, string>? ParseHeaders(byte[] buf, int headerEnd)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var lines = Encoding.Latin1.GetString(buf, 0, headerEnd).Split("\r\n");
        foreach (var line in lines.Skip(1))
        {
            int c = line.IndexOf(':');
            if (c > 0) dict[line[..c].Trim()] = line[(c + 1)..].Trim();
        }
        return dict;
    }

    static int IndexOfHeaderEnd(byte[] buf, int len)
    {
        for (int i = 0; i + 3 < len; i++)
            if (buf[i] == 13 && buf[i + 1] == 10 && buf[i + 2] == 13 && buf[i + 3] == 10)
                return i;
        return -1;
    }

    static Dictionary<string, string> ParseQuery(string query)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = kv.IndexOf('=');
            var k = eq < 0 ? kv : kv[..eq];
            var v = eq < 0 ? "" : Uri.UnescapeDataString(kv[(eq + 1)..]);
            dict[k] = v;
        }
        return dict;
    }

    static async Task WriteAsync(NetworkStream s, int code, string mime, string body)
    {
        var bodyBytes = Encoding.UTF8.GetBytes(body);
        string reason = code switch
        {
            200 => "OK", 400 => "Bad Request", 401 => "Unauthorized", 404 => "Not Found",
            405 => "Method Not Allowed", 500 => "Internal Server Error", _ => "Error",
        };
        var sb = new StringBuilder();
        sb.Append("HTTP/1.1 ").Append(code).Append(' ').Append(reason).Append("\r\n");
        sb.Append("Content-Type: ").Append(mime).Append("\r\n");
        sb.Append("Content-Length: ").Append(bodyBytes.Length).Append("\r\n");
        sb.Append("Connection: close\r\n\r\n");
        await WriteRawAsync(s, sb.ToString());
        await s.WriteAsync(bodyBytes);
    }

    static async Task WriteRawAsync(NetworkStream s, string text)
        => await s.WriteAsync(Encoding.Latin1.GetBytes(text));
}
