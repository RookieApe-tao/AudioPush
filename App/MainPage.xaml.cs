using System.Net.WebSockets;
using Android.Media;
using Concentus;
using Concentus.Enums;
using Concentus.Structs;
using ZXing.Net.Maui.Controls;

namespace Audio2PhoneApp;

public partial class MainPage : ContentPage
{
    const int SampleRate = 48000;
    const int Channels = 2;
    const int FrameSamples = SampleRate / 100;   // 10ms
    const int FrameBytesPcm = FrameSamples * Channels * 2;

    WebSocket? _ws;
    System.Net.Sockets.TcpClient? _tcp;
    CancellationTokenSource? _cts;
    AudioTrack? _audioTrack;
    IOpusDecoder? _decoder;
    Task? _receiveTask;
    volatile bool _connected;
    volatile bool _autoReconnect;
    volatile bool _connecting;
    bool _supervisorRunning;
    int _reconnectAttempts;
    long _lastDataTicks;
    long _connGen;                        // 连接代际：每次连接/断开 +1，旧连接的收尾不得影响新连接
    float _volume = 0.8f;
    readonly SemaphoreSlim _connectGate = new(1, 1);   // 同一时间只允许一次连接尝试

    const int StallTimeoutMs = 10_000;    // 应用层心跳：10 秒没收到任何音频帧 → 判定假死，自动重连
    const int ConnectTimeoutMs = 5_000;   // TCP 连接 / 握手超时（避免电脑关机时干等系统 SYN 超时）
    const int ReconnectDelayMaxMs = 5_000; // 自动重连退避上限；此后每 5 秒试一次，直到连上为止（不再有次数上限）

    public MainPage()
    {
        InitializeComponent();
        LoadSavedConfig();
    }

    void LoadSavedConfig()
    {
        HostEntry.Text = Preferences.Get("host", "");
        PortEntry.Text = Preferences.Get("port", "8818");
        TokenEntry.Text = Preferences.Get("token", "");
    }

    void SaveConfig()
    {
        Preferences.Set("host", HostEntry.Text ?? "");
        Preferences.Set("port", PortEntry.Text ?? "8818");
        Preferences.Set("token", TokenEntry.Text ?? "");
    }

    // ---- 线程安全 UI 帮助 ----
    //
    // 自动重连跑在后台线程，绝不允许直接触碰 UI 元素——
    // 旧版在这里从后台线程改 Label/Button 抛 CalledFromWrongThreadException，
    // 导致 _connecting 卡死：既不再自动重连，「连接」按钮也永远无效。
    // 所有 UI 操作必须经 RunOnUi。

    void RunOnUi(Action action)
    {
        try
        {
            if (MainThread.IsMainThread) action();
            else MainThread.BeginInvokeOnMainThread(action);
        }
        catch (Exception ex)
        {
            Android.Util.Log.Warn("A2P", $"RunOnUi: {ex.Message}");
        }
    }

    // ---- 扫码 ----

    async void OnScanClicked(object? sender, EventArgs e)
    {
        try
        {
            // 请求相机权限
            var status = await Permissions.RequestAsync<Permissions.Camera>();
            if (status != PermissionStatus.Granted)
            {
                StatusLabel.Text = "需要相机权限才能扫码";
                return;
            }

            // 跳转扫码页面
            await Navigation.PushAsync(new ScanPage(OnQrCodeScanned));
        }
        catch (Exception ex)
        {
            StatusLabel.Text = $"扫码启动失败: {ex.Message}";
            Android.Util.Log.Error("A2P", $"scan start: {ex}");
        }
    }

    void OnQrCodeScanned(string text)
    {
        // 解析 URL: http://192.168.0.32:8818/?token=6cr5haaz
        try
        {
            var uri = new Uri(text.Trim());
            var host = uri.Host;
            var port = uri.Port > 0 ? uri.Port.ToString() : "8818";

            // 从 query 提取 token
            var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
            var token = query["token"] ?? "";

            if (!string.IsNullOrEmpty(host) && !string.IsNullOrEmpty(token))
            {
                HostEntry.Text = host;
                PortEntry.Text = port;
                TokenEntry.Text = token;
                SaveConfig();
                StatusLabel.Text = $"已解析: {host}:{port}";
            }
            else
            {
                StatusLabel.Text = "二维码内容无法解析，请检查格式";
            }
        }
        catch
        {
            StatusLabel.Text = "二维码不是有效 URL，请检查";
        }
    }

    // ---- 连接 ----

    async void OnConnectClicked(object? sender, EventArgs e)
    {
        if (_connecting) return;   // 防连点（自动重连进行中按钮已被禁用）

        if (_connected)
        {
            _autoReconnect = false;   // 手动断开，不自动重连
            Disconnect();
            return;
        }

        _autoReconnect = true;        // 手动连接后启用自动重连
        _reconnectAttempts = 0;
        StartSupervisor();            // 启动监督循环（只启动一次）
        await ConnectAsync();
    }

    /// <summary>
    /// 建立 WebSocket 连接并开始播放。silent=true 时不弹错误框、不禁用按钮（自动重连用）。
    /// 可在任意线程调用（含后台监督循环）；全部 UI 更新经 RunOnUi，
    /// 全部资源清理经 try/finally，任何异常都不可能把 _connecting 或按钮卡死。
    /// </summary>
    async Task ConnectAsync(bool silent = false)
    {
        var host = (HostEntry.Text ?? "").Trim();
        var port = (PortEntry.Text ?? "8818").Trim();
        var token = (TokenEntry.Text ?? "").Trim();

        if (string.IsNullOrEmpty(host))
        {
            RunOnUi(() => StatusLabel.Text = "请输入电脑 IP 地址");
            return;
        }
        if (string.IsNullOrEmpty(token))
        {
            RunOnUi(() => StatusLabel.Text = "请输入令牌 Token");
            return;
        }

        SaveConfig();

        await _connectGate.WaitAsync();   // 串行化连接尝试
        long gen = 0;
        System.Net.Sockets.TcpClient? tcp = null;
        WebSocket? ws = null;
        bool ok = false;
        try
        {
            // 新连接开始：作废旧代，创建本代专属 CTS（旧收尾只取消旧 CTS，互不干扰）
            gen = Interlocked.Increment(ref _connGen);
            var cts = new CancellationTokenSource();
            var oldCts = Interlocked.Exchange(ref _cts, cts);
            try { if (!ReferenceEquals(oldCts, cts)) oldCts?.Cancel(); } catch { }

            _connecting = true;
            RunOnUi(() =>
            {
                StatusLabel.Text = silent ? "自动重连中…" : "连接中…";
                if (!silent) SetConnecting(true);
            });

            // Android 13+ 运行时申请通知权限（前台服务通知需要；静默重连时跳过）
            if (!silent)
            {
                try
                {
                    var notifStatus = await Permissions.RequestAsync<Permissions.PostNotifications>();
                    Android.Util.Log.Info("A2P", $"Notification permission: {notifStatus}");
                }
                catch (Exception ex)
                {
                    Android.Util.Log.Warn("A2P", $"Notification permission request failed: {ex.Message}");
                }

                // 请求忽略电池优化（防 MIUI/Doze 冻结网络），只弹一次系统框
                RequestIgnoreBatteryOptimization();
            }

            Android.Util.Log.Info("A2P", $"[{gen}] Connecting to {host}:{port}");

            // 直接用 TCP 连接 + 手写 WebSocket 握手，绕过 ClientWebSocket 的严格 HTTP 验证
            if (!int.TryParse(port, out int portNum) || portNum < 1 || portNum > 65535)
            {
                RunOnUi(() => StatusLabel.Text = "端口格式不正确");
                return;
            }

            tcp = new System.Net.Sockets.TcpClient();
            _tcp = tcp;   // 立即登记到字段：Disconnect/作废路径都能关掉它
            using (var connectTimeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(ConnectTimeoutMs)))
            using (var link = CancellationTokenSource.CreateLinkedTokenSource(cts.Token, connectTimeout.Token))
            {
                await tcp.ConnectAsync(host, portNum, link.Token);
            }

            var ns = tcp.GetStream();

            // 发送 WebSocket 升级请求
            var wsKey = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16));
            var request = $"GET /ws?token={Uri.EscapeDataString(token)} HTTP/1.1\r\nHost: {host}:{port}\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Key: {wsKey}\r\nSec-WebSocket-Version: 13\r\n\r\n";
            var reqBytes = System.Text.Encoding.ASCII.GetBytes(request);
            await ns.WriteAsync(reqBytes, cts.Token);
            await ns.FlushAsync(cts.Token);

            // 读取响应（等待 101）
            var respBuf = new byte[4096];
            int respLen = 0;
            using (var handshakeTimeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(ConnectTimeoutMs)))
            using (var readCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token, handshakeTimeout.Token))
            {
                while (respLen < respBuf.Length)
                {
                    int n;
                    try
                    {
                        n = await ns.ReadAsync(respBuf.AsMemory(respLen, respBuf.Length - respLen), readCts.Token);
                    }
                    catch (OperationCanceledException) when (handshakeTimeout.IsCancellationRequested && !cts.IsCancellationRequested)
                    {
                        break;   // 握手响应超时，用已读到的内容判断
                    }
                    if (n <= 0) break;
                    respLen += n;
                    if (respLen >= 4 && System.Text.Encoding.ASCII.GetString(respBuf, 0, respLen).Contains("\r\n\r\n"))
                        break;
                }
            }

            var respText = System.Text.Encoding.ASCII.GetString(respBuf, 0, respLen);
            var firstLine = respText.Length > 0 ? respText.Split('\n')[0].Trim() : "无响应";
            Android.Util.Log.Info("A2P", $"[{gen}] Handshake: {firstLine}");

            if (!respText.Contains("101"))
            {
                RunOnUi(() => StatusLabel.Text = $"握手失败: {firstLine}");
                if (!silent)
                    await ShowAlertSafe("连接失败", $"服务端握手失败：{firstLine}\n请确认电脑端正在运行、Token 是否正确。");
                return;
            }

            // 从 TCP 流创建 WebSocket（跳过 ClientWebSocket 的 HTTP 验证）
            // KeepAliveInterval：协议层心跳，空闲 15 秒自动发 Ping（服务端自动回 Pong）
            ws = WebSocket.CreateFromStream(ns, new WebSocketCreationOptions
            {
                IsServer = false,
                KeepAliveInterval = TimeSpan.FromSeconds(15),
            });
            _ws = ws;

            Android.Util.Log.Info("A2P", $"[{gen}] WebSocket created, state={ws.State}");

            // 初始化音频播放器
            InitAudioPlayer();

            // 开始接收
            _connected = true;
            _lastDataTicks = Environment.TickCount64;   // 断流检测基准（监督循环负责断流检测与自动重连）
            StartForegroundService();
            _receiveTask = Task.Run(() => ReceiveLoopAsync(cts.Token, gen));

            RunOnUi(() =>
            {
                ConnectBtn.Text = "断开";
                ConnectBtn.BackgroundColor = Colors.Red;
                StatusLabel.Text = "已连接 · 播放中";
            });
            Android.Util.Log.Info("A2P", $"[{gen}] Connected");
            ok = true;
        }
        catch (Exception ex) when (ex is OperationCanceledException)
        {
            Android.Util.Log.Warn("A2P", $"[{gen}] connect cancelled/timeout");
            if (gen != 0 && gen == Volatile.Read(ref _connGen))
            {
                RunOnUi(() => StatusLabel.Text = "连接超时");
                if (!silent)
                    await ShowAlertSafe("连接失败", $"连接超时：无法连上 {host}:{port}\n请确认电脑端正在运行、IP 正确且手机与电脑在同一网络。");
                Disconnect();
            }
        }
        catch (Exception ex)
        {
            Android.Util.Log.Error("A2P", $"[{gen}] ERROR: {ex}");
            if (gen != 0 && gen == Volatile.Read(ref _connGen))
            {
                RunOnUi(() => StatusLabel.Text = $"连接失败: {ex.GetType().Name}: {ex.Message}");
                if (!silent)
                    await ShowAlertSafe("连接失败", $"{ex.Message}");
                Disconnect();
            }
        }
        finally
        {
            if (!ok)
            {
                // 尝试未成功：确保本次创建的资源全部释放（Disconnect 可能已释放过，重复释放安全）
                try { tcp?.Close(); } catch { }
                try { ws?.Abort(); } catch { }
                try { ws?.Dispose(); } catch { }
                if (tcp != null && ReferenceEquals(_tcp, tcp)) _tcp = null;
                if (ws != null && ReferenceEquals(_ws, ws)) _ws = null;
            }
            // 无条件复位：无论走哪条路径、被谁作废，「本次尝试已结束」这一事实必须落实，
            // 否则监督循环永远等待、连接按钮永远失效（旧版卡死 Bug 的根源）
            _connecting = false;
            if (!silent) RunOnUi(() => SetConnecting(false));
            _connectGate.Release();
        }
    }

    // ---- 监督循环：断流检测（应用层心跳）+ 自动重连（与连接级取消令牌解耦）----
    //
    // 只要 _autoReconnect 为 true 就一直活着：
    //   已连接 → 检查断流（10 秒无音频帧 → 判定假死，断开）
    //   已断开 → 静默重连：1s→2s→4s→5s 退避，无次数上限，服务端什么时候恢复就什么时候连上
    // 手动点「断开」会把 _autoReconnect 置 false，循环随即停止干预。

    async Task SupervisorLoopAsync()
    {
        while (true)
        {
            try
            {
                if (_autoReconnect)
                {
                    if (_connected)
                    {
                        _reconnectAttempts = 0;
                        long idle = Environment.TickCount64 - Volatile.Read(ref _lastDataTicks);
                        if (idle >= StallTimeoutMs)
                        {
                            Android.Util.Log.Warn("A2P", $"No audio data for {idle}ms -> reconnect (stall)");
                            RunOnUi(() => StatusLabel.Text = "长时间未收到音频，自动重连…");
                            Disconnect();
                        }
                    }
                    else if (!_connecting)
                    {
                        _reconnectAttempts++;
                        int delay = Math.Min(1000 << Math.Min(_reconnectAttempts - 1, 3), ReconnectDelayMaxMs);
                        RunOnUi(() => StatusLabel.Text = $"自动重连…（第 {_reconnectAttempts} 次）");
                        await ConnectAsync(silent: true);
                        if (!_connected)
                        {
                            try { await Task.Delay(delay); } catch { }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Android.Util.Log.Warn("A2P", $"supervisor: {ex.Message}");
            }

            try { await Task.Delay(500); } catch { }
        }
    }

    void StartSupervisor()
    {
        if (_supervisorRunning) return;
        _supervisorRunning = true;
        _ = Task.Run(SupervisorLoopAsync);
    }

    async Task ShowAlertSafe(string title, string message)
    {
        try
        {
            if (MainThread.IsMainThread)
                await DisplayAlert(title, message, "好");
            else
                await MainThread.InvokeOnMainThreadAsync(() => DisplayAlert(title, message, "好"));
        }
        catch { }
    }

    void InitAudioPlayer()
    {
        // 释放旧实例
        _audioTrack?.Release();
        _audioTrack?.Dispose();

        var minBuf = AudioTrack.GetMinBufferSize(SampleRate, ChannelOut.Stereo, Encoding.Pcm16bit);
        _audioTrack = new AudioTrack.Builder()
            .SetAudioAttributes(new AudioAttributes.Builder()
                .SetUsage(AudioUsageKind.Media)
                .SetContentType(AudioContentType.Music)
                .Build())
            .SetAudioFormat(new AudioFormat.Builder()
                .SetSampleRate(SampleRate)
                .SetChannelMask(ChannelOut.Stereo)
                .SetEncoding(Encoding.Pcm16bit)
                .Build())
            .SetBufferSizeInBytes(Math.Max(minBuf, FrameBytesPcm * 4))
            .SetTransferMode(AudioTrackMode.Stream)
            .Build();

        _audioTrack.SetVolume(_volume);
        _audioTrack.Play();

        // 初始化 Opus 解码器
        _decoder = OpusCodecFactory.CreateDecoder(SampleRate, Channels);
    }

    async Task ReceiveLoopAsync(CancellationToken ct, long gen)
    {
        var headerBuf = new byte[2];
        var opusBuf = new byte[1275];
        var pcmBuf = new short[FrameSamples * Channels];
        string? errorMessage = null;

        try
        {
            while (!ct.IsCancellationRequested)
            {
                var ws = _ws;
                if (ws == null || ws.State != WebSocketState.Open) break;

                // 读 2 字节帧头（Opus 数据长度，小端）
                await ReadExactAsync(ws, headerBuf, ct);
                int opusLen = headerBuf[0] | (headerBuf[1] << 8);
                if (opusLen <= 0 || opusLen > opusBuf.Length) continue;

                // 读 Opus 数据
                await ReadExactAsync(ws, opusBuf.AsMemory(0, opusLen), ct);

                // 解码：IOpusDecoder.Decode(ReadOnlySpan<byte>, Span<short>, frame_size, decode_fec)
                int samplesDecoded = _decoder?.Decode(
                    opusBuf.AsSpan(0, opusLen),
                    pcmBuf.AsSpan(0, FrameSamples * Channels),
                    FrameSamples,
                    false) ?? 0;
                if (samplesDecoded <= 0) continue;

                // 写入 AudioTrack
                int pcmLen = samplesDecoded * Channels * 2;
                var pcmBytes = new byte[pcmLen];
                Buffer.BlockCopy(pcmBuf, 0, pcmBytes, 0, pcmLen);
                _audioTrack?.Write(pcmBytes, 0, pcmLen);
                Volatile.Write(ref _lastDataTicks, Environment.TickCount64);   // 喂狗：收到音频帧
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException ex)
        {
            errorMessage = $"连接断开: {ex.Message}";
            Android.Util.Log.Warn("A2P", $"[{gen}] receive: {ex.Message}");
        }
        catch (Exception ex)
        {
            errorMessage = $"错误: {ex.Message}";
            Android.Util.Log.Warn("A2P", $"[{gen}] receive error: {ex}");
        }
        finally
        {
            Android.Util.Log.Info("A2P", $"[{gen}] receive loop exit");
            // 只有仍是「当前代」时才由本循环收尾；
            // 否则说明已被新连接/手动断开取代——绝不能去杀新连接的资源
            if (gen == Volatile.Read(ref _connGen))
            {
                var msg = errorMessage;
                RunOnUi(() =>
                {
                    if (msg != null) StatusLabel.Text = msg;
                    if (_connected) Disconnect();
                });
            }
        }
    }

    static async Task ReadExactAsync(WebSocket ws, byte[] buf, CancellationToken ct)
    {
        int offset = 0;
        while (offset < buf.Length)
        {
            var result = await ws.ReceiveAsync(buf.AsMemory(offset, buf.Length - offset), ct);
            if (result.MessageType == WebSocketMessageType.Close)
                throw new WebSocketException("服务器关闭连接");
            offset += result.Count;
        }
    }

    static async Task ReadExactAsync(WebSocket ws, Memory<byte> buf, CancellationToken ct)
    {
        int offset = 0;
        while (offset < buf.Length)
        {
            var result = await ws.ReceiveAsync(buf.Slice(offset), ct);
            if (result.MessageType == WebSocketMessageType.Close)
                throw new WebSocketException("服务器关闭连接");
            offset += result.Count;
        }
    }

    /// <summary>
    /// 断开并清理全部资源。可在任意线程调用（UI/后台监督循环/连接失败路径）；
    /// 非 UI 部分就地清理，UI 更新经 RunOnUi。代际 +1 使任何在途收尾失效。
    /// </summary>
    void Disconnect()
    {
        Interlocked.Increment(ref _connGen);   // 作废当前代
        _connected = false;

        var cts = Interlocked.Exchange(ref _cts, null);
        try { cts?.Cancel(); } catch { }

        var ws = Interlocked.Exchange(ref _ws, null);
        try { ws?.Abort(); } catch { }
        try { ws?.Dispose(); } catch { }

        var tcp = Interlocked.Exchange(ref _tcp, null);
        try { tcp?.Close(); } catch { }

        var at = Interlocked.Exchange(ref _audioTrack, null);
        try { at?.Stop(); } catch { }
        try { at?.Release(); } catch { }
        try { at?.Dispose(); } catch { }

        var dec = Interlocked.Exchange(ref _decoder, null);
        try { dec?.Dispose(); } catch { }

        StopForegroundService();

        RunOnUi(() =>
        {
            ConnectBtn.Text = "连接";
            ConnectBtn.BackgroundColor = Color.FromArgb("#4CAF50");
            StatusLabel.Text = "已断开";
        });
        Android.Util.Log.Info("A2P", "Disconnected");
    }

    // ---- 前台服务保活 ----

    void StartForegroundService()
    {
        try
        {
            var intent = new Android.Content.Intent(Android.App.Application.Context, typeof(AudioStreamForegroundService));
            if (Android.OS.Build.VERSION.SdkInt >= Android.OS.BuildVersionCodes.O)
                Android.App.Application.Context.StartForegroundService(intent);
            else
                Android.App.Application.Context.StartService(intent);
        }
        catch (Exception ex)
        {
            Android.Util.Log.Warn("A2P", $"StartForegroundService failed: {ex.Message}");
        }
    }

    void RequestIgnoreBatteryOptimization()
    {
        try
        {
            var pkg = Android.App.Application.Context.PackageName;
            var pm = (Android.OS.PowerManager?)Android.App.Application.Context
                .GetSystemService(Android.Content.Context.PowerService);
            if (pkg == null || pm == null || pm.IsIgnoringBatteryOptimizations(pkg)) return;

            var intent = new Android.Content.Intent(
                Android.Provider.Settings.ActionRequestIgnoreBatteryOptimizations,
                Android.Net.Uri.Parse("package:" + pkg));
            Platform.CurrentActivity?.StartActivity(intent);
            Android.Util.Log.Info("A2P", "Requested ignore battery optimizations");
        }
        catch (Exception ex)
        {
            Android.Util.Log.Warn("A2P", $"RequestIgnoreBatteryOptimization failed: {ex.Message}");
        }
    }

    void StopForegroundService()
    {
        try
        {
            var intent = new Android.Content.Intent(Android.App.Application.Context, typeof(AudioStreamForegroundService));
            Android.App.Application.Context.StopService(intent);
        }
        catch { }
    }

    void SetConnecting(bool connecting)
    {
        ConnectBtn.IsEnabled = !connecting;
    }

    void OnVolumeChanged(object? sender, ValueChangedEventArgs e)
    {
        _volume = (float)e.NewValue;
        _audioTrack?.SetVolume(_volume);
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        // 页面关闭时断开（但 ForegroundService 会继续播放）
    }
}
