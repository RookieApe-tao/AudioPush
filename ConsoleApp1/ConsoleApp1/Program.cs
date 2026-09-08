using System.Text;
using YuPinTuiSong;

// ============================================================
// audio2phone — 电脑全系统声音 → 局域网 → 手机播放（纯控制台版）
// 原理: WASAPI loopback 采集 → LAME 编码 MP3（失败退回 WAV）
//       → TcpListener 手写 HTTP 推流 → 手机浏览器 / VLC / App
// 鉴权: ?token=xxx（首次运行自动生成，存 config.json）
// ============================================================

var baseDir = AppContext.BaseDirectory;
Log.Init(baseDir);
var cfg = Config.Load(baseDir);
var hub = new AudioHub();
using var cts = new CancellationTokenSource();

var webrtc = new WebRtcService(cts.Token);
var wsAudio = new WebSocketAudioService(cts.Token);
var server = new StreamServer(cfg, hub, cts.Token, webrtc, wsAudio);
server.Start();

var engine = new CaptureEngine(cfg, hub, cts.Token);
engine.Pcm16Captured += webrtc.OnPcmCaptured;   // 分接 PCM 给 WebRTC 低延迟通道
engine.Pcm16Captured += wsAudio.OnPcmCaptured;   // 分接 PCM 给 WebSocket 通道
engine.Start();

// 等采集源就绪（最多 5 秒），让打印的横幅带上实际设备信息
var sw = System.Diagnostics.Stopwatch.StartNew();
while (hub.SourceInfo == "初始化中…" && sw.Elapsed < TimeSpan.FromSeconds(5))
    await Task.Delay(100);

Console.OutputEncoding = Encoding.UTF8;
var ips = Utils.GetLanIps();
Console.WriteLine();
Console.WriteLine("============================================================");
Console.WriteLine(" audio2phone — 电脑声音 → 手机（局域网推送）");
Console.WriteLine($" 采集: {(hub.SourceInfo == "初始化中…" ? "（未找到输出设备，后台重试中，详见日志）" : hub.SourceInfo)}");
Console.WriteLine($" 鉴权: {(cfg.RequireAuth ? "已开启" : "关闭")} | 配置: {cfg.FilePath}");
Console.WriteLine();

// 生成二维码（取第一个局域网 IP）
var primaryUrl = $"http://{(ips.Count > 0 ? ips[0] : "127.0.0.1")}:{cfg.Port}/?token={cfg.Token}";
try
{
    using var qrGen = new QRCoder.QRCodeGenerator();
    var qrData = qrGen.CreateQrCode(primaryUrl, QRCoder.QRCodeGenerator.ECCLevel.M);

    // PNG 真图片版：控制台字符二维码受字体渲染影响（块字符有细缝/宽高比失真）经常扫不出，
    // 生成 PNG 到临时目录并自动用系统看图工具打开，手机扫码 100% 可识别
    try
    {
        var pngQr = new QRCoder.PngByteQRCode(qrData);
        var pngPath = Path.Combine(Path.GetTempPath(), "audio2phone-qr.png");
        File.WriteAllBytes(pngPath, pngQr.GetGraphic(12));
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = pngPath,
            UseShellExecute = true,
        });
        Console.WriteLine(" 手机扫码连接：已弹出二维码图片窗口（audio2phone-qr.png），用 App「扫码连接」扫一下即可");
    }
    catch (Exception ex)
    {
        Log.Warn("二维码图片生成/打开失败（不影响功能）: " + ex.Message);
    }

    // 控制台字符版（备用）：半块字符渲染（▀▄█）约 37 列 × 19 行，深色背景下可扫。
    // 注意：GetGraphicSmall 返回整段字符串（\n 分行），不是 string[]，不能直接 foreach 遍历；
    // string[] 版本是 GetLineByLineGraphic。
    var asciiQr = new QRCoder.AsciiQRCode(qrData);
    var qrText = asciiQr.GetGraphicSmall(drawQuietZones: true, invert: false);
    Console.WriteLine();
    Console.WriteLine(" 若图片未弹出，可扫下方字符二维码：");
    Console.WriteLine();
    foreach (var line in qrText.Split('\n'))
        Console.WriteLine("  " + line.TrimEnd('\r'));
    Console.WriteLine();
}
catch (Exception ex)
{
    Log.Warn("二维码生成失败（不影响功能）: " + ex.Message);
}

Console.WriteLine(" 手机与电脑连同一个 Wi-Fi，用浏览器打开：");
foreach (var ip in ips)
    Console.WriteLine($"   http://{ip}:{cfg.Port}/?token={cfg.Token}");
Console.WriteLine();
Console.WriteLine(" 低延迟 ≈0.1s: 浏览器点按钮 / App 连接（息屏不中断）");
Console.WriteLine(" MP3 兼容 ≈2~5s: 浏览器 <audio> / VLC");
Console.WriteLine(" App WebSocket:  ws://<IP>:8818/ws?token=xxx");
Console.WriteLine(" Ctrl+C 停止");
Console.WriteLine("============================================================");

Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

try { await Task.Delay(Timeout.Infinite, cts.Token); }
catch (OperationCanceledException) { }

Log.Info("正在停止…");
engine.WaitStop(TimeSpan.FromSeconds(5));
Log.Info("已退出");
return 0;
