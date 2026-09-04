using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace YuPinTuiSong;

/// <summary>
/// 极简 HTTP 流媒体服务（TcpListener 手写协议，无需 URLACL/管理员权限）。
/// 路由：/ 播放页 · /stream 音频流 · /status 状态。
/// 鉴权：?token=xxx（也接受 X-Token 头 / Authorization: Bearer xxx）。
/// </summary>
public sealed class StreamServer
{
    readonly Config _cfg;
    readonly AudioHub _hub;
    readonly CancellationToken _ct;
    TcpListener? _listener;

    public StreamServer(Config cfg, AudioHub hub, CancellationToken ct)
    {
        _cfg = cfg;
        _hub = hub;
        _ct = ct;
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

        var (method, target, headers) = req.Value;
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
            mime = _hub.Mime,
            source = _hub.SourceInfo,
            startedAt = _hub.StartedAt.ToString("yyyy-MM-dd HH:mm:ss"),
            requireAuth = _cfg.RequireAuth,
        });
        await WriteAsync(stream, 200, "application/json", json);
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
<p style="color:#888;font-size:13px">延迟约 2~5 秒属正常 · 电脑端 Ctrl+C 停止 · 配置见 config.json</p>
</body></html>
""";
        string body = auth
            ? $"""
<p><audio controls autoplay style="width:100%" src="/stream?token={_cfg.Token}"></audio></p>
<p>如果浏览器不能播放，请安装 <b>VLC</b>，打开「网络串流」：<br>
<code>http://{host}/stream?token={_cfg.Token}</code></p>
"""
            : """
<p>&#10060; <b>缺少或错误的访问令牌（token）。</b></p>
<p>请使用电脑端打印的完整地址，格式形如：<br>
<code>http://电脑IP:8818/?token=xxxxxxxx</code></p>
""";
        return tpl.Replace("__BODY__", body);
    }

    // ------------------------------------------------------------ HTTP 基础

    async Task<(string Method, string Target, Dictionary<string, string> Headers)?> ReadRequestAsync(NetworkStream stream)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        var buf = new byte[16 * 1024];
        int len = 0;
        try
        {
            while (len < buf.Length)
            {
                int n = await stream.ReadAsync(buf.AsMemory(len, buf.Length - len), timeout.Token);
                if (n <= 0) return null;
                len += n;
                if (len >= 4 && Encoding.ASCII.GetString(buf, len - 4, 4) == "\r\n\r\n") break;
            }
        }
        catch (OperationCanceledException) { return null; }
        catch (IOException) { return null; }

        var head = Encoding.Latin1.GetString(buf, 0, len);
        int end = head.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        if (end < 0) return null;
        head = head[..end];

        var lines = head.Split("\r\n");
        var parts = lines[0].Split(' ');
        if (parts.Length < 2) return null;

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines.Skip(1))
        {
            int c = line.IndexOf(':');
            if (c > 0) headers[line[..c].Trim()] = line[(c + 1)..].Trim();
        }
        return (parts[0].ToUpperInvariant(), parts[1], headers);
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
            200 => "OK", 401 => "Unauthorized", 404 => "Not Found",
            405 => "Method Not Allowed", _ => "Error",
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
