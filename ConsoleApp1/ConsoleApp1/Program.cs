using System.Text;
using YuPinTuiSong;

// ============================================================
// audio2phone — 电脑全系统声音 → 局域网 → 手机播放（纯控制台版）
// 原理: WASAPI loopback 采集 → LAME 编码 MP3（失败退回 WAV）
//       → TcpListener 手写 HTTP 推流 → 手机浏览器 / VLC
// 鉴权: ?token=xxx（首次运行自动生成，存 config.json）
// ============================================================

var baseDir = AppContext.BaseDirectory;
Log.Init(baseDir);
var cfg = Config.Load(baseDir);
var hub = new AudioHub();
using var cts = new CancellationTokenSource();

var server = new StreamServer(cfg, hub, cts.Token);
server.Start();

var engine = new CaptureEngine(cfg, hub, cts.Token);
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
Console.WriteLine(" 手机与电脑连同一个 Wi-Fi，用浏览器或 VLC 打开：");
foreach (var ip in ips)
    Console.WriteLine($"   http://{ip}:{cfg.Port}/?token={cfg.Token}");
Console.WriteLine();
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
