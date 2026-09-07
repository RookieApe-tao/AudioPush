using System.Text.Json;
using System.Threading.Channels;
using Concentus;
using Concentus.Enums;
using Concentus.Structs;
using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;

namespace YuPinTuiSong;

/// <summary>
/// 低延迟通道：WASAPI PCM 分接 → 线性重采样 48kHz → 10ms 帧 → Opus 编码(Concentus 纯 C#)
/// → SIPSorcery WebRTC RTP 发送。信令走本服务 POST /rtc（offer 换 answer），
/// 浏览器原生解码播放，局域网延迟约 0.1~0.2 秒，适合看视频对口型。
/// </summary>
public sealed class WebRtcService : IDisposable
{
    const int OutRate = 48000;
    const int Channels = 2;
    const int FrameSamples = OutRate / 100;   // 10ms = 480 帧
    const int OpusBitrate = 128_000;

    readonly CancellationToken _ct;
    readonly object _gate = new();
    readonly List<Peer> _peers = new();

    LinearResampler? _resampler;
    int _inRate;
    short[] _src = [];                        // 输入展开缓冲
    short[] _dst = [];                        // 重采样输出缓冲
    readonly short[] _acc = new short[FrameSamples * Channels];
    int _accCount;

    volatile bool _disposed;

    sealed class Peer
    {
        public required RTCPeerConnection Pc;
        public required IOpusEncoder Encoder;
        public required Channel<short[]> Frames;
        public required Task Sender;
    }

    public WebRtcService(CancellationToken ct) => _ct = ct;

    public int PeerCount { get { lock (_gate) return _peers.Count; } }

    // ------------------------------------------------------------ 信令

    /// <summary>处理浏览器 offer，返回 answer JSON；失败返回 null。</summary>
    public async Task<string?> HandleOfferAsync(string offerSdp)
    {
        RTCPeerConnection? pc = null;
        try
        {
            pc = new RTCPeerConnection(new RTCConfiguration());   // 纯局域网：无需 STUN
            // 注意 5 参构造顺序: (formatID, 名称, 时钟率, 声道数, fmtp 参数)
            var opus = new AudioFormat(111, "OPUS", OutRate, Channels,
                "minptime=10;useinbandfec=1;stereo=1");   // 111 = OPUS 标准动态 payload type
            pc.addTrack(new MediaStreamTrack(new List<AudioFormat> { opus },
                MediaStreamStatusEnum.SendOnly));

            var remote = pc.setRemoteDescription(new RTCSessionDescriptionInit
            {
                type = RTCSdpType.offer,
                sdp = offerSdp,
            });
            if (remote != SetDescriptionResultEnum.OK)
            {
                Log.Error("WebRTC setRemoteDescription 失败: " + remote);
                pc.close();
                return null;
            }

            var peer = new Peer
            {
                Pc = pc,
                Encoder = NewEncoder(),
                Frames = Channel.CreateBounded<short[]>(new BoundedChannelOptions(40)
                {
                    SingleReader = true,
                    FullMode = BoundedChannelFullMode.DropOldest,   // 未连上时丢弃旧帧
                }),
                Sender = Task.CompletedTask,
            };

            pc.onconnectionstatechange += state =>
            {
                Log.Info($"WebRTC 对等端状态: {state}");
                if (state is RTCPeerConnectionState.failed
                    or RTCPeerConnectionState.closed
                    or RTCPeerConnectionState.disconnected)
                    RemovePeer(peer);
            };

            var gathered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            pc.onicegatheringstatechange += state =>
            {
                if (state == RTCIceGatheringState.complete) gathered.TrySetResult();
            };

            lock (_gate) _peers.Add(peer);
            peer.Sender = Task.Run(() => SenderLoopAsync(peer));

            await pc.setLocalDescription(pc.createAnswer());   // Task 完成即本地 SDP 就绪
            try { await gathered.Task.WaitAsync(TimeSpan.FromSeconds(2)); }
            catch (TimeoutException) { /* LAN 上一般 <300ms，超时也先返回 */ }

            Log.Info($"新 WebRTC 对等端接入（当前 {PeerCount} 个）");
            // localDescription.sdp 是 SDP 对象，必须显式转成 SDP 文本再下发
            return JsonSerializer.Serialize(new { type = "answer", sdp = pc.localDescription.sdp.ToString() });
        }
        catch (Exception ex)
        {
            Log.Error("WebRTC 信令处理失败: " + ex.Message);
            try { pc?.close(); } catch { }
            return null;
        }
    }

    // ------------------------------------------------------------ PCM 分接

    /// <summary>CaptureEngine 的 PCM 分接入口（int16 交叠，≤2 声道，设备混音率）。</summary>
    public void OnPcmCaptured(int rate, byte[] pcm16)
    {
        if (rate <= 0 || pcm16.Length == 0) return;
        lock (_gate)
        {
            if (_disposed || _peers.Count == 0) return;
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
                        Log.Info($"WebRTC 通道重采样 {rate}Hz → {OutRate}Hz");
                    }
                    int need = (int)((long)frames * OutRate / rate) + 16;
                    if (_dst.Length < need * Channels) _dst = new short[need * Channels];
                    outFrames = _resampler.Process(_src, frames, _dst);
                }

                // 切 10ms 帧广播给每个对等端
                int srcPos = 0;
                while (srcPos < outFrames)
                {
                    int copy = Math.Min(outFrames - srcPos, FrameSamples - _accCount);
                    Array.Copy(_dst, srcPos * Channels, _acc, _accCount * Channels, copy * Channels);
                    _accCount += copy;
                    srcPos += copy;
                    if (_accCount == FrameSamples)
                    {
                        var frame = new short[FrameSamples * Channels];
                        Array.Copy(_acc, frame, frame.Length);
                        foreach (var p in _peers) p.Frames.Writer.TryWrite(frame);
                        _accCount = 0;
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warn("WebRTC PCM 处理异常: " + ex.Message);
            }
        }
    }

    // ------------------------------------------------------------ 发送

    async Task SenderLoopAsync(Peer peer)
    {
        byte[] encoded = new byte[1275 * 2];   // Opus 单帧长度上限
        try
        {
            await foreach (var frame in peer.Frames.Reader.ReadAllAsync(_ct))
            {
                if (_disposed) return;
                if (peer.Pc.connectionState != RTCPeerConnectionState.connected) continue;
                int n = peer.Encoder.Encode(frame, frame.Length / Channels, encoded, encoded.Length);
                if (n <= 0) continue;
                var payload = new byte[n];               // SendAudio 按数组长度发包，必须精确
                Buffer.BlockCopy(encoded, 0, payload, 0, n);
                peer.Pc.SendAudio((uint)FrameSamples, payload);
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (Exception ex) { Log.Warn("WebRTC 发送循环退出: " + ex.Message); }
    }

    static IOpusEncoder NewEncoder()
    {
        var enc = OpusCodecFactory.CreateEncoder(OutRate, Channels, OpusApplication.OPUS_APPLICATION_AUDIO);
        enc.Bitrate = OpusBitrate;
        enc.Complexity = 5;
        return enc;
    }

    void RemovePeer(Peer peer)
    {
        bool removed;
        lock (_gate) removed = _peers.Remove(peer);
        if (!removed) return;
        peer.Frames.Writer.TryComplete();
        try { peer.Pc.close(); } catch { }
        Log.Info($"WebRTC 对等端已断开（剩余 {PeerCount} 个）");
    }

    public void Dispose()
    {
        List<Peer> peers;
        lock (_gate)
        {
            _disposed = true;
            peers = _peers.ToList();
            _peers.Clear();
        }
        foreach (var p in peers)
        {
            p.Frames.Writer.TryComplete();
            try { p.Pc.close(); } catch { }
        }
    }

    /// <summary>跨调用的线性插值重采样器（流式，任意块大小，块间保持相位）。</summary>
    sealed class LinearResampler
    {
        readonly double _step;
        readonly int _ch;
        readonly short[] _prev;
        bool _hasPrev;
        long _absInFrames;   // 已消费输入帧总数
        double _next;        // 下一个输出样本对应的绝对输入帧位置

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
            if (rel < 0) return _hasPrev ? _prev[ch] : src[ch];   // 跨块回看上一帧
            return src[rel * _ch + ch];
        }
    }
}
