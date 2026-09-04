using System.Net;
using System.Net.Sockets;

namespace YuPinTuiSong;

public static class Utils
{
    /// <summary>取本机局域网 IPv4 列表（控制台打印可访问地址用）。</summary>
    public static List<string> GetLanIps()
    {
        var ips = new List<string>();
        void Add(string? ip)
        {
            if (string.IsNullOrEmpty(ip) || ip.StartsWith("127.") || ips.Contains(ip)) return;
            ips.Add(ip);
        }

        try   // 默认路由网卡 IP（UDP trick，不实际发包）
        {
            using var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            s.Connect("8.8.8.8", 80);
            Add((s.LocalEndPoint as IPEndPoint)?.Address.ToString());
        }
        catch { }

        try
        {
            foreach (var a in Dns.GetHostEntry(Dns.GetHostName()).AddressList)
                if (a.AddressFamily == AddressFamily.InterNetwork) Add(a.ToString());
        }
        catch { }

        return ips;
    }
}
