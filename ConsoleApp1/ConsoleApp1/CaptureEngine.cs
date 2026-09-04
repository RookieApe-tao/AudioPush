using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using NAudio.Lame;
using NAudio.Wave;

namespace YuPinTuiSong;

/// <summary>
/// WASAPI loopback 采集（全系统声音）+ MP3 编码（不可用时退回 WAV）。
/// 设备变化 / 异常时自动重启采集，直到取消令牌触发。
/// </summary>
public sealed class CaptureEngine : IDisposable
{
    readonly Config _cfg;
    readonly AudioHub _hub;
    readonly CancellationToken _ct;
    readonly object _pcmGate = new();      // 序列化 lame.Write 与 Dispose
    Task? _loop;

    WasapiLoopbackCapture? _capture;
    LameMP3FileWriter? _lame;
    HubStream? _sink;
    DeviceEvents? _deviceEvents;
    string? _deviceId;
    TaskCompletionSource _stopTcs = NewTcs();

    public CaptureEngine(Config cfg, AudioHub hub, CancellationToken ct)
    {
        _cfg = cfg;
        _hub = hub;
        _ct = ct;
    }

    static TaskCompletionSource NewTcs() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    public void Start() => _loop ??= Task.Run(RunLoopAsync);

    /// <summary>等待采集循环退出。</summary>
    public bool WaitStop(TimeSpan timeout) => _loop is null || _loop.Wait(timeout);

    public void Dispose() => TeardownCapture();

    async Task RunLoopAsync()
    {
        var backoff = TimeSpan.FromSeconds(2);
        while (!_ct.IsCancellationRequested)
        {
            try
            {
                string info = StartCaptureOnce();
                Log.Info("采集已启动: " + info);
                backoff = TimeSpan.FromSeconds(2);
                await WaitForCaptureStopAsync();
                Log.Info(_ct.IsCancellationRequested ? "采集停止（收到退出信号）" : "采集停止，稍后自动重启…");
            }
            catch (Exception ex)
            {
                Log.Error("采集失败: " + ex.Message);
                Log.Info("提示: 若电脑当前没有任何输出设备，请安装虚拟声卡 VB-Cable（https://vb-audio.com/Cable/），");
                Log.Info("      并在系统声音设置中把「CABLE Input」设为默认输出设备；无需重启本程序，稍后自动恢复。");
            }
            finally { TeardownCapture(); }

            if (_ct.IsCancellationRequested) break;
            try { await Task.Delay(backoff, _ct); }
            catch (OperationCanceledException) { break; }
            if (backoff < TimeSpan.FromSeconds(30)) backoff = TimeSpan.FromTicks(backoff.Ticks * 2);
        }
        Log.Info("采集线程已退出");
    }

    string StartCaptureOnce()
    {
        var enumerator = new MMDeviceEnumerator();
        var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Console); // 无输出设备时抛异常
        _deviceId = device.ID;

        _capture = new WasapiLoopbackCapture(device);
        var fmt = _capture.WaveFormat;
        _capture.DataAvailable += OnDataAvailable;
        _capture.RecordingStopped += OnRecordingStopped;

        // 输出端：MP3；编码器不可用（缺原生 DLL / 采样率不支持）退回 WAV
        int outCh = Math.Min(fmt.Channels, 2);
        _hub.Mime = "audio/mpeg";
        _hub.Prelude = [];
        try
        {
            _sink = new HubStream(_hub);
            _lame = new LameMP3FileWriter(_sink, new WaveFormat(fmt.SampleRate, 16, outCh), _cfg.Mp3Bitrate);
        }
        catch (Exception ex)
        {
            Log.Warn($"MP3 编码器不可用（{ex.Message}），退回原始 WAV 模式（建议用 VLC 播放）");
            _lame = null;
            _sink = null;
            _hub.Mime = "audio/wav";
            _hub.Prelude = WavHeader(fmt.SampleRate, outCh);
        }

        _capture.StartRecording();

        _deviceEvents = new DeviceEvents(this);
        enumerator.RegisterEndpointNotificationCallback(_deviceEvents);

        _hub.SourceInfo = $"{device.FriendlyName} @ {fmt.SampleRate}Hz {fmt.Channels}ch";
        return $"{_hub.SourceInfo} → {_hub.Mime}{(_lame != null ? $" {_cfg.Mp3Bitrate}kbps" : "")}";
    }

    void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (e.BytesRecorded <= 0) return;
        var fmt = _capture?.WaveFormat;
        if (fmt == null) return;
        try
        {
            byte[] pcm16 = ConvertToPcm16(e.Buffer, e.BytesRecorded, fmt);
            if (fmt.Channels > 2) pcm16 = KeepFrontStereo(pcm16, fmt.Channels);
            lock (_pcmGate)
            {
                if (_lame != null) _lame.Write(pcm16, 0, pcm16.Length);   // MP3 经 HubStream 进广播
                else _hub.Publish(pcm16);                                  // WAV 直发
            }
        }
        catch (Exception ex)
        {
            Log.Error("音频处理异常: " + ex.Message);
            try { _capture?.StopRecording(); } catch { }
        }
    }

    void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception != null) Log.Error("录音停止: " + e.Exception.Message);
        _stopTcs.TrySetResult();
    }

    async Task WaitForCaptureStopAsync()
    {
        _stopTcs = NewTcs();
        var stopTask = _stopTcs.Task;
        var cancelTask = Task.Delay(Timeout.Infinite, _ct);
        var done = await Task.WhenAny(stopTask, cancelTask).ConfigureAwait(false);
        if (done == cancelTask)
        {
            try { _capture?.StopRecording(); } catch { }
            try { await stopTask; } catch { }
        }
    }

    void TeardownCapture()
    {
        if (_deviceEvents != null)
        {
            try { new MMDeviceEnumerator().UnregisterEndpointNotificationCallback(_deviceEvents); } catch { }
            _deviceEvents = null;
        }
        lock (_pcmGate)
        {
            try { _lame?.Dispose(); } catch { }
            _lame = null;
        }
        try { _capture?.StopRecording(); } catch { }
        try { _capture?.Dispose(); } catch { }
        _capture = null;
        _sink = null;
        _deviceId = null;
    }

    void RequestRestart(string reason)
    {
        if (_ct.IsCancellationRequested) return;
        Log.Info("检测到" + reason + "，重启采集…");
        try { _capture?.StopRecording(); } catch { }
    }

    void RequestRestartIfDevice(string deviceId, string reason)
    {
        if (!string.Equals(deviceId, _deviceId, StringComparison.OrdinalIgnoreCase)) return;
        RequestRestart(reason);
    }

    static byte[] ConvertToPcm16(byte[] buffer, int bytes, WaveFormat fmt)
    {
        if (fmt.Encoding == WaveFormatEncoding.Pcm && fmt.BitsPerSample == 16)
            return buffer.AsSpan(0, bytes).ToArray();

        if (fmt.Encoding == WaveFormatEncoding.IeeeFloat && fmt.BitsPerSample == 32)
        {
            int n = bytes / 4;
            var dst = new byte[n * 2];
            for (int i = 0; i < n; i++)
            {
                float f = BitConverter.ToSingle(buffer, i * 4);
                if (f > 1f) f = 1f; else if (f < -1f) f = -1f;
                short v = (short)MathF.Round(f * 32767f);
                dst[i * 2] = (byte)v;
                dst[i * 2 + 1] = (byte)(v >> 8);
            }
            return dst;
        }
        throw new NotSupportedException($"不支持的采集格式: {fmt.Encoding} {fmt.BitsPerSample}bit");
    }

    static byte[] KeepFrontStereo(byte[] pcm16, int srcCh)
    {
        int frames = pcm16.Length / (srcCh * 2);
        var dst = new byte[frames * 4];
        for (int f = 0; f < frames; f++)
            Array.Copy(pcm16, f * srcCh * 2, dst, f * 4, 4);
        return dst;
    }

    static byte[] WavHeader(int rate, int ch, int bits = 16)
    {
        int byteRate = rate * ch * bits / 8;
        short blockAlign = (short)(ch * bits / 8);
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write("RIFF"u8); w.Write(0xFFFFFFFF); w.Write("WAVE"u8);
        w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)ch);
        w.Write(rate); w.Write(byteRate); w.Write(blockAlign); w.Write((short)bits);
        w.Write("data"u8); w.Write(0xFFFFFFFF);
        w.Flush();
        return ms.ToArray();
    }

    /// <summary>LameMP3FileWriter 的输出流：编码结果直接进广播中心。</summary>
    sealed class HubStream : Stream
    {
        readonly AudioHub _hub;
        public HubStream(AudioHub hub) => _hub = hub;

        public override bool CanRead => false;
        public override bool CanSeek => false;     // 不支持 Seek → LAME 跳过 Xing 头，流式安全
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count)
        {
            if (count > 0) _hub.Publish(buffer.AsSpan(offset, count));
        }
    }

    sealed class DeviceEvents : IMMNotificationClient
    {
        readonly CaptureEngine _owner;
        public DeviceEvents(CaptureEngine owner) => _owner = owner;

        public void OnDeviceStateChanged(string deviceId, DeviceState newState)
            => _owner.RequestRestartIfDevice(deviceId, $"设备状态变化({newState})");

        public void OnDeviceAdded(string deviceId) { }

        public void OnDeviceRemoved(string deviceId)
            => _owner.RequestRestartIfDevice(deviceId, "设备被移除");

        public void OnDefaultDeviceChanged(DataFlow dataFlow, Role role, string defaultDeviceId)
        {
            if (dataFlow == DataFlow.Render) _owner.RequestRestart("默认输出设备变更");
        }

        public void OnPropertyValueChanged(string deviceId, PropertyKey propertyKey) { }
    }
}
