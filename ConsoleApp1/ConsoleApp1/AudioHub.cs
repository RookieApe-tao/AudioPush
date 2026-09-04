using System.Threading.Channels;

namespace YuPinTuiSong;

/// <summary>
/// 一写多读的音频广播中心：采集/编码线程 Publish，每个 HTTP 客户端 Join 一个有界队列，
/// 慢客户端丢最旧的块，绝不阻塞采集。
/// </summary>
public sealed class AudioHub
{
    readonly object _gate = new();
    readonly List<Channel<byte[]>> _clients = new();

    /// <summary>流的 Content-Type（audio/mpeg 或回退 audio/wav）。</summary>
    public string Mime { get; set; } = "audio/mpeg";

    /// <summary>连接建立后先发的头部（WAV 模式用，MP3 为空）。</summary>
    public byte[] Prelude { get; set; } = [];

    /// <summary>当前采集源描述。</summary>
    public string SourceInfo { get; set; } = "初始化中…";

    public DateTime StartedAt { get; } = DateTime.Now;

    public int ClientCount { get { lock (_gate) return _clients.Count; } }

    public Channel<byte[]> Join()
    {
        var ch = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(512)
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.DropOldest,
        });
        lock (_gate) _clients.Add(ch);
        return ch;
    }

    public void Leave(Channel<byte[]> ch)
    {
        lock (_gate) _clients.Remove(ch);
    }

    public void Publish(ReadOnlySpan<byte> data)
    {
        if (data.Length == 0) return;
        Channel<byte[]>[] snapshot;
        lock (_gate)
        {
            if (_clients.Count == 0) return;
            snapshot = _clients.ToArray();
        }
        var chunk = data.ToArray();
        foreach (var ch in snapshot) ch.Writer.TryWrite(chunk);
    }
}
