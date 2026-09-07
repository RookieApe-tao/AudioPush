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
    bool _connected;
    bool _autoReconnect;
    long _lastDataTicks;
    const int StallTimeoutMs = 10_000;   // 10 秒没收到任何音频帧 → 判定假死，自动重连

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
        if (_connected)
        {
            _autoReconnect = false;   // 手动断开，不自动重连
            Disconnect();
            return;
        }

        _autoReconnect = true;        // 手动连接后启用自动重连
        await ConnectAsync();
    }

    /// <summary>建立 WebSocket 连接并开始播放。silent=true 时不弹错误框（自动重连用）。</summary>
    async Task ConnectAsync(bool silent = false)
    {
        var host = (HostEntry.Text ?? "").Trim();
        var port = (PortEntry.Text ?? "8818").Trim();
        var token = (TokenEntry.Text ?? "").Trim();

        if (string.IsNullOrEmpty(host))
        {
            StatusLabel.Text = "请输入电脑 IP 地址";
            return;
        }
        if (string.IsNullOrEmpty(token))
        {
            StatusLabel.Text = "请输入令牌 Token";
            return;
        }

        SaveConfig();
        SetConnecting(true);
        StatusLabel.Text = "连接中…";

        // Android 13+ 运行时申请通知权限（前台服务通知需要）
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

        try
        {
            _cts = new CancellationTokenSource();

            StatusLabel.Text = "正在连接…";
            Android.Util.Log.Info("A2P", $"Connecting to {host}:{port}");

            // 直接用 TCP 连接 + 手写 WebSocket 握手，绕过 ClientWebSocket 的严格 HTTP 验证
            int portNum;
            if (!int.TryParse(port, out portNum) || portNum < 1 || portNum > 65535)
            {
                StatusLabel.Text = "端口格式不正确";
                SetConnecting(false);
                return;
            }

            var tcp = new System.Net.Sockets.TcpClient();
            await tcp.ConnectAsync(host, portNum, _cts.Token);
            _tcp = tcp;   // 交给 Disconnect 统一释放
            var ns = tcp.GetStream();

            // 发送 WebSocket 升级请求
            var wsKey = Convert.ToBase64String(new byte[16]); // 简单随机 key
            var request = $"GET /ws?token={token} HTTP/1.1\r\nHost: {host}:{port}\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Key: {wsKey}\r\nSec-WebSocket-Version: 13\r\n\r\n";
            var reqBytes = System.Text.Encoding.ASCII.GetBytes(request);
            await ns.WriteAsync(reqBytes, _cts.Token);
            await ns.FlushAsync(_cts.Token);

            // 读取响应（等待 101）
            var respBuf = new byte[4096];
            int respLen = 0;
            using var handshakeTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var readCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token, handshakeTimeout.Token);
            while (respLen < respBuf.Length)
            {
                int n;
                try
                {
                    n = await ns.ReadAsync(respBuf.AsMemory(respLen, respBuf.Length - respLen), readCts.Token);
                }
                catch (OperationCanceledException) when (!readCts.Token.IsCancellationRequested || handshakeTimeout.Token.IsCancellationRequested)
                {
                    break;   // 握手响应超时，走下面的格式判断
                }
                if (n <= 0) break;
                respLen += n;
                if (respLen >= 4 && System.Text.Encoding.ASCII.GetString(respBuf, 0, respLen).Contains("\r\n\r\n"))
                    break;
            }
            var respText = System.Text.Encoding.ASCII.GetString(respBuf, 0, respLen);
            Android.Util.Log.Info("A2P", $"Response: {respText.Replace("\r\n", " | ").Substring(0, Math.Min(200, respText.Length))}");

            if (!respText.Contains("101"))
            {
                tcp.Close();
                _tcp = null;
                var reason = respText.Length > 0 ? respText.Split('\n')[0].Trim() : "无响应";
                StatusLabel.Text = $"握手失败: {reason}";
                await ShowAlertSafe("连接失败", $"服务端握手失败：{reason}\n请确认电脑端正在运行、Token 是否正确。");
                return;
            }

            // 从 TCP 流创建 WebSocket（跳过 ClientWebSocket 的 HTTP 验证）
            _ws = WebSocket.CreateFromStream(ns, new WebSocketCreationOptions
            {
                IsServer = false,
                KeepAliveInterval = TimeSpan.FromSeconds(15),
            });

            Android.Util.Log.Info("A2P", $"WebSocket created, state={_ws.State}");
            StatusLabel.Text = "握手完成";

            // 初始化音频播放器
            InitAudioPlayer();
            Android.Util.Log.Info("A2P", "AudioTrack initialized");
            StatusLabel.Text = "音频已初始化";

            // 开始接收
            _connected = true;
            ConnectBtn.Text = "断开";
            ConnectBtn.BackgroundColor = Colors.Red;
            StatusLabel.Text = "已连接 · 播放中";
            Android.Util.Log.Info("A2P", "Starting receive loop");

            // 启动前台服务保活（防止锁屏后系统冻结网络）
            StartForegroundService();

            // 断流检测基准 + 看门狗（10 秒没音频帧 → 自动断开重连）
            _lastDataTicks = Environment.TickCount64;
            _receiveTask = Task.Run(() => ReceiveLoopAsync(_cts.Token));
            _ = Task.Run(() => WatchdogLoopAsync(_cts.Token));
        }
        catch (OperationCanceledException)
        {
            StatusLabel.Text = "连接超时";
            if (!silent)
                await ShowAlertSafe("连接失败", $"连接超时：无法连上 {host}:{port}\n请确认电脑端正在运行、IP 正确且手机与电脑在同一网络。");
            Disconnect();
        }
        catch (Exception ex)
        {
            Android.Util.Log.Error("A2P", $"ERROR: {ex}");
            StatusLabel.Text = $"连接失败: {ex.GetType().Name}: {ex.Message}";
            if (!silent)
                await ShowAlertSafe("连接失败", $"{ex.Message}");
            Disconnect();
        }
        finally
        {
            SetConnecting(false);
        }
    }

    // ---- 断流看门狗：连接保持着但长时间没有音频帧 → 自动断开重连 ----

    async Task WatchdogLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(3000, ct);
                if (!_connected) continue;

                long idle = Environment.TickCount64 - Volatile.Read(ref _lastDataTicks);
                if (idle < StallTimeoutMs) continue;

                Android.Util.Log.Warn("A2P", $"No audio data for {idle}ms -> auto reconnect");
                MainThread.BeginInvokeOnMainThread(() =>
                    StatusLabel.Text = "长时间未收到音频，自动重连…");
                Disconnect();
                break;
            }
        }
        catch (OperationCanceledException) { }

        // 自动重连（静默模式，最多 30 次）
        var attempts = 0;
        while (_autoReconnect && !_connected && attempts < 30 && _cts?.IsCancellationRequested != true)
        {
            attempts++;
            MainThread.BeginInvokeOnMainThread(() =>
                StatusLabel.Text = $"自动重连…（第 {attempts} 次）");
            try { await Task.Delay(2000); } catch { return; }
            if (!_autoReconnect || _connected) return;
            try
            {
                await MainThread.InvokeOnMainThreadAsync(() => ConnectAsync(silent: true));
            }
            catch (Exception ex)
            {
                Android.Util.Log.Warn("A2P", $"reconnect attempt {attempts}: {ex.Message}");
            }
        }
        if (attempts >= 30 && !_connected)
        {
            _autoReconnect = false;
            MainThread.BeginInvokeOnMainThread(() =>
                StatusLabel.Text = "自动重连失败，请手动点「连接」");
        }
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

        _audioTrack.SetVolume((float)VolumeSlider.Value);
        _audioTrack.Play();

        // 初始化 Opus 解码器
        _decoder = OpusCodecFactory.CreateDecoder(SampleRate, Channels);
    }

    async Task ReceiveLoopAsync(CancellationToken ct)
    {
        var headerBuf = new byte[2];
        var opusBuf = new byte[1275];
        var pcmBuf = new short[FrameSamples * Channels];
        string? errorMessage = null;

        try
        {
            while (_ws?.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                // 读 2 字节帧头（Opus 数据长度，小端）
                await ReadExactAsync(_ws, headerBuf, ct);
                int opusLen = headerBuf[0] | (headerBuf[1] << 8);
                if (opusLen <= 0 || opusLen > opusBuf.Length) continue;

                // 读 Opus 数据
                await ReadExactAsync(_ws, opusBuf.AsMemory(0, opusLen), ct);

                // 解码：IOpusDecoder.Decode(ReadOnlySpan<byte>, Span<short>, frame_size, decode_fec)
                int samplesDecoded = _decoder!.Decode(
                    opusBuf.AsSpan(0, opusLen),
                    pcmBuf.AsSpan(0, FrameSamples * Channels),
                    FrameSamples,
                    false);
                if (samplesDecoded <= 0) continue;

                // 写入 AudioTrack
                int pcmLen = samplesDecoded * Channels * 2;
                var pcmBytes = new byte[pcmLen];
                Buffer.BlockCopy(pcmBuf, 0, pcmBytes, 0, pcmLen);
                _audioTrack?.Write(pcmBytes, 0, pcmLen);
                _lastDataTicks = Environment.TickCount64;   // 喂狗：收到音频帧
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException ex)
        {
            errorMessage = $"连接断开: {ex.Message}";
        }
        catch (Exception ex)
        {
            errorMessage = $"错误: {ex.Message}";
        }
        finally
        {
            var wasConnected = _connected;
            MainThread.BeginInvokeOnMainThread(async () =>
            {
                StatusLabel.Text = errorMessage ?? "已断开";
                if (errorMessage != null)
                    await ShowAlertSafe("连接已断开", errorMessage + "\n\n点击「连接」可重新连接。");
                if (wasConnected) Disconnect();
            });
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

    void Disconnect()
    {
        _connected = false;
        _cts?.Cancel();
        // Abort 立即释放，不阻塞 UI 线程（服务端能容忍硬断开）
        try { _ws?.Abort(); } catch { }
        _ws?.Dispose();
        _ws = null;
        try { _tcp?.Close(); } catch { }
        _tcp = null;
        try { _audioTrack?.Stop(); } catch { }
        try { _audioTrack?.Release(); } catch { }
        _audioTrack?.Dispose();
        _audioTrack = null;
        try { _decoder?.Dispose(); } catch { }
        _decoder = null;

        StopForegroundService();

        ConnectBtn.Text = "连接";
        ConnectBtn.BackgroundColor = Color.FromArgb("#4CAF50");
        StatusLabel.Text = "已断开";
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
        _audioTrack?.SetVolume((float)e.NewValue);
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        // 页面关闭时断开（但 ForegroundService 会继续播放）
    }
}
