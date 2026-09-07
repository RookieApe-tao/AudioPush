using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Threading.Channels;
using Concentus;
using Concentus.Enums;
using Concentus.Structs;

namespace YuPinTuiSong;

/// <summary>
/// WebSocket 低延迟通道：客户端通过 ws://host:port/ws?token=xxx 连接，
/// 服务端持续推送 Opus 帧（10ms 一帧，帧头 2 字节小端长度 + Opus 数据）。
/// 息屏不断（App 端 ForegroundService 保活），延迟约 50~100ms。
/// </summary>
public sealed class WebSocketAudioService : IDisposable
{
    const int OutRate = 48000;
    const int Channels = 2;
    const int FrameSamples = OutRate / 100;   // 10ms
    const int OpusBitrate = 128_000;

    readonly CancellationToken _ct;
    readonly object _gate = new();
    readonly ConcurrentDictionary<WebSocket, ClientState> _clients = new();

    LinearResampler? _resampler;
    int _inRate;
    short[] _src = [];
    short[] _dst = [];
    readonly short[] _acc = new short[FrameSamples * Channels];
    int _accCount;

    public int ClientCount => _clients.Count;

    sealed class ClientState
    {
        public required WebSocket Socket;
        public required IOpusEncoder Encoder;
        public required Channel<byte[]> Frames;
        public required Task Sender;
    }

    public WebSocketAudioService(CancellationToken ct) => _ct = ct;

    /// <summary>StreamServer 调用：接受一个 WebSocket 连接并保持到断开。</summary>
    public async Task AcceptClientAsync(WebSocket ws)
    {
        var encoder = OpusCodecFactory.CreateEncoder(OutRate, Channels, OpusApplication.OPUS_APPLICATION_AUDIO);
        encoder.Bitrate = OpusBitrate;
        encoder.Complexity = 5;

        var channel = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(64)
        {
            SingleReader = true,
            FullMode = BoundedChannelFullMode.DropOldest,
        });

        var state = new ClientState
        {
            Socket = ws,
            Encoder = encoder,
            Frames = channel,
            Sender = Task.CompletedTask,
        };

        _clients[ws] = state;
        state.Sender = Task.Run(() => SendLoopAsync(state));
        Log.Info($"WebSocket 客户端接入（当前 {ClientCount} 个）");

        try
        {
            // 保持连接直到客户端断开（纯接收通道，客户端不发数据）
            var buf = new byte[1024];
            while (ws.State == WebSocketState.Open && !_ct.IsCancellationRequested)
            {
                var result = await ws.ReceiveAsync(buf, _ct);
                Log.Debug($"WebSocket ReceiveAsync: type={result.MessageType} count={result.Count} close={result.CloseStatus}");
                if (result.MessageType == WebSocketMessageType.Close) break;
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"WebSocket 接收异常: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            _clients.TryRemove(ws, out _);
            channel.Writer.TryComplete();
            try { await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None); } catch { }
            Log.Info($"WebSocket 客户端断开（剩余 {ClientCount} 个）");
        }
    }

    // ---- PCM 分接入口（来自 CaptureEngine）----

    public void OnPcmCaptured(int rate, byte[] pcm16)
    {
        if (rate <= 0 || pcm16.Length == 0 || _clients.IsEmpty) return;
        lock (_gate)
        {
            try
            {
                int frames = pcm16.Length / (Channels * 2);
                if (frames <= 0) return;

                if (_src.Length < frames * Channels) _src = new short[frames * Channels];
                Buffer.BlockCopy(pcm16, 0, _src, 0, frames * Channels * 2);

                int outFrames;
                if (rate == OutRate)
                {
                    outFrames = frames;
                    if (_dst.Length < outFrames * Channels) _dst = new short[outFrames * Channels];
                    Array.Copy(_src, _dst, outFrames * Channels);
                }
                else
                {
                    if (_resampler == null || _inRate != rate)
                    {
                        _resampler = new LinearResampler(rate, OutRate, Channels);
                        _inRate = rate;
                    }
                    int need = (int)((long)frames * OutRate / rate) + 16;
                    if (_dst.Length < need * Channels) _dst = new short[need * Channels];
                    outFrames = _resampler.Process(_src, frames, _dst);
                }

                int srcPos = 0;
                while (srcPos < outFrames)
                {
                    int copy = Math.Min(outFrames - srcPos, FrameSamples - _accCount);
                    Array.Copy(_dst, srcPos * Channels, _acc, _accCount * Channels, copy * Channels);
                    _accCount += copy;
                    srcPos += copy;
                    if (_accCount == FrameSamples)
                    {
                        BroadcastFrame(_acc);
                        _accCount = 0;
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warn("WebSocket PCM 处理异常: " + ex.Message);
            }
        }
    }

    void BroadcastFrame(short[] pcm)
    {
        byte[] encoded = new byte[1275];
        foreach (var (ws, state) in _clients)
        {
            if (ws.State != WebSocketState.Open) continue;
            try
            {
                int n = state.Encoder.Encode(pcm, pcm.Length / Channels, encoded, encoded.Length);
                if (n <= 0) continue;
                // 帧格式：[2字节长度小端][Opus数据]
                var frame = new byte[2 + n];
                frame[0] = (byte)(n & 0xFF);
                frame[1] = (byte)((n >> 8) & 0xFF);
                Buffer.BlockCopy(encoded, 0, frame, 2, n);
                state.Frames.Writer.TryWrite(frame);
            }
            catch { }
        }
    }

    async Task SendLoopAsync(ClientState state)
    {
        try
        {
            await foreach (var frame in state.Frames.Reader.ReadAllAsync(_ct))
            {
                if (state.Socket.State != WebSocketState.Open) break;
                await state.Socket.SendAsync(frame, WebSocketMessageType.Binary, true, _ct);
            }
        }
        catch { }
    }

    public void Dispose()
    {
        foreach (var (ws, _) in _clients)
        {
            try { ws.Abort(); } catch { }
        }
        _clients.Clear();
    }

    // ---- 线性重采样器（复用 WebRtcService 的逻辑）----

    sealed class LinearResampler
    {
        readonly double _step;
        readonly int _ch;
        readonly short[] _prev;
        bool _hasPrev;
        long _absInFrames;
        double _next;

        public LinearResampler(int inRate, int outRate, int ch)
        {
            _step = (double)inRate / outRate;
            _ch = ch;
            _prev = new short[ch];
        }

        public int Process(short[] src, int inFrames, short[] dst)
        {
            int written = 0;
            long srcEnd = _absInFrames + inFrames;
            while (true)
            {
                long i0 = (long)_next;
                long i1 = i0 + 1;
                if (i1 >= srcEnd) break;
                double frac = _next - i0;
                for (int c = 0; c < _ch; c++)
                {
                    double s0 = SampleAt(src, inFrames, i0, c);
                    double s1 = SampleAt(src, inFrames, i1, c);
                    dst[written * _ch + c] = (short)Math.Round(s0 + (s1 - s0) * frac);
                }
                written++;
                _next += _step;
            }
            for (int c = 0; c < _ch; c++)
                _prev[c] = src[(inFrames - 1) * _ch + c];
            _hasPrev = true;
            _absInFrames += inFrames;
            return written;
        }

        short SampleAt(short[] src, int inFrames, long absIndex, int ch)
        {
            long rel = absIndex - _absInFrames;
            if (rel < 0) return _hasPrev ? _prev[ch] : src[ch];
            return src[rel * _ch + ch];
        }
    }
}
