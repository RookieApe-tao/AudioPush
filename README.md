# audio2phone（yinpintuisong）

把 **Windows 全系统声音**（任何 App：视频、游戏、音乐、网页……）通过**局域网**实时推送到手机播放。
适用于电脑没有音箱/耳机（或输出设备损坏）的场景，手机就是你的「无线音箱」。

提供**三通道**：

```
任意 App 播放声音
   ↓  WASAPI loopback（抓取默认输出设备的系统混音）
CaptureEngine（NAudio 采集 + float→pcm16 转换，分接三路）
   ├─→ App 通道：WebSocket 推送 Opus 10ms 帧（~100ms 延迟，息屏不中断）
   ├─→ 低延迟通道：WebRTC Opus（浏览器 ~0.1 秒，息屏自动重连）
   └─→ 兼容通道：MP3 192kbps（浏览器 <audio> / VLC，延迟 2~5 秒）
   ↓  Wi-Fi 局域网（TcpListener 手写 HTTP + WebSocket，免管理员权限，token 鉴权）
手机   http://<电脑IP>:8818/?token=xxxxxxxx   或   ws://<电脑IP>:8818/ws?token=xxx
```

仓库包含三套客户端：
- **Android App（推荐）**：`App/` 目录，.NET MAUI，WebSocket 接收 Opus + AudioTrack 播放，
  后台 ForegroundService 保活，**息屏不断**，延迟约 100ms。
- **C# 服务端**：`ConsoleApp1/` 解决方案，.NET 10，程序集名 `yinpintuisong`。
- **Python 版（参考/备用实现）**：根目录 `audio2phone.py` 单文件脚本，依赖 `PyAudioWPatch` + `lameenc`，默认端口同为 8818。仅 MP3 兼容通道，无 WebRTC。

## 快速开始

1. **运行**（任选其一）：
   - Visual Studio 打开 `ConsoleApp1.slnx` 按 F5；
   - 命令行：`dotnet run --project ConsoleApp1\ConsoleApp1 -c Release`；
   - 直接双击 `ConsoleApp1\ConsoleApp1\bin\Release\net10.0\yinpintuisong.exe`。
2. 首次运行自动生成 `config.json`（含随机 **8 位访问令牌 Token**），控制台打印可访问地址和二维码。
3. 手机连同一 Wi-Fi，选择播放方式：
   - **App（推荐）**：下载安装 `App\bin\Debug\net9.0-android\com.companyname.audio2phoneapp.apk`，
     输入电脑 IP 和 Token，点连接 → **息屏不中断**，延迟约 100ms。
   - **浏览器**：扫码或打开打印的地址，点「🎧 低延迟模式」→ WebRTC 播放（~0.1 秒，息屏自动重连）；
     或用 MP3 兼容播放器 / VLC 打开 `http://<IP>:8818/stream?token=xxx`（延迟 2~5 秒，息屏不中断）。
4. 首次运行如弹出 Windows 防火墙提示，勾选「专用网络和公用网络」后允许；
   或管理员执行：
   ```
   netsh advfirewall firewall add rule name="audio2phone" dir=in action=allow protocol=TCP localport=8818
   ```

## ⚠️ 电脑没有任何输出设备时

程序采集的是「默认输出设备的系统混音」。若设备列表为空（日志报 `0x80070490`），任选其一，
**程序每 2~30 秒自动重试，设备出现后无需重启自动恢复**：

- **方案 A（推荐，无需硬件）**：安装免费虚拟声卡 [VB-Cable](https://vb-audio.com/Cable/)
  （仓库根目录附带 `VBCABLE_Driver_Pack45.zip`）→ 系统声音设置把「**CABLE Input**」设为默认输出设备。
- **方案 B**：往机箱插一副任意耳机/音箱 → 端点自动激活。

## 三通道说明

| | App（推荐） | 低延迟模式（WebRTC） | 兼容模式（HTTP+MP3） |
|---|---|---|---|
| 延迟 | 约 100ms | 约 0.1~0.2 秒 | 约 2~5 秒 |
| 息屏播放 | ✅ 不中断 | ❌ 断开，亮屏自动重连 | ✅ 不中断 |
| 适用 | 日常收听（推荐） | 看视频对口型（浏览器） | 听歌、VLC 播放 |
| 编码 | Opus 128kbps 立体声 10ms 帧 | Opus 128kbps 10ms 帧 | MP3 192kbps |
| 播放端 | MAUI Android App | 浏览器点按钮 | 浏览器 <audio> / VLC |
| 协议 | WebSocket (`/ws`) | WebRTC (`/rtc`) | HTTP (`/stream`) |

App 通道：手机通过 WebSocket 连接 `ws://<IP>:8818/ws?token=xxx`，服务端推送 Opus 帧，
App 用 Concentus 解码 + Android AudioTrack 播放，前台服务保活，息屏完全不受影响。
WebRTC 通道：浏览器原生播放，息屏后浏览器冻结 JS 导致断连，亮屏自动重连（约 1~2 秒延迟）。

## 鉴权

- `/stream`、`/status`、`/rtc`、`/ws` 必须携带令牌，三种等价方式：
  - URL 参数：`?token=xxxxxxxx`（推荐）
  - 请求头：`X-Token: xxxxxxxx`
  - 请求头：`Authorization: Bearer xxxxxxxx`
- 令牌在 `config.json` 的 `Token` 字段，可修改后重启；`"RequireAuth": false` 可关闭（不建议）。

## 配置项（config.json）

| 字段 | 默认 | 说明 |
|---|---|---|
| `Port` | 8818 | HTTP/WebSocket 监听端口（改端口后同步防火墙规则） |
| `Mp3Bitrate` | 192 | MP3 码率 kbps |
| `RequireAuth` | true | 是否强制 token 鉴权 |
| `Token` | 随机 8 位 | 访问令牌 |

## HTTP 接口

| 路径 | 说明 |
|---|---|
| `/` 或 `/index.html` | 播放页（低延迟按钮 + MP3 兼容播放器） |
| `/stream` | MP3 音频流（`audio/mpeg`，MP3 不可用时 `audio/wav`） |
| `/rtc` | WebRTC 信令：POST JSON `{type:"offer", sdp}` → 返回 answer |
| `/ws` | WebSocket 低延迟通道：`ws://host:port/ws?token=xxx`，推送 Opus 帧（App 用） |
| `/status` | JSON 状态：MP3 客户端数、WebRTC 对等端数、WebSocket 客户端数、编码格式、采集源 |

## App 使用

### 安装

1. 手机开启 USB 调试，连接电脑。
2. 在项目根目录执行：
   ```
   cd App
   dotnet build -f net9.0-android -c Debug
   ```
3. APK 生成在 `App/bin/Debug/net9.0-android/com.companyname.audio2phoneapp-Signed.apk`。
4. 用 ADB 安装：
   ```
   D:\Android\android-sdk\platform-tools\adb.exe install -r App\bin\Debug\net9.0-android\com.companyname.audio2phoneapp-Signed.apk
   ```

### 配置

- 打开 App，输入电脑的局域网 IP（如 `192.168.1.100`）、端口（默认 `8818`）和 Token。
- 点「连接」→ 立即播放。
- 配置会自动保存，下次打开无需重新输入。

### 后台播放

- App 连接后自动启动前台服务，**息屏不中断**。
- 部分手机（如小米、华为）需要在设置中授予「后台运行」权限并关闭电池优化。
- 通知栏会显示「正在播放电脑声音」，点击可返回 App。

## 故障排查

| 现象 | 处理 |
|---|---|
| 手机打不开页面 | 确认同一 Wi-Fi；防火墙放行 exe/端口；地址里 IP 是电脑的局域网 IP |
| `401 unauthorized` | 地址缺 `?token=`，令牌看 `config.json` |
| 日志报 0x80070490 | 没有任何输出设备 → 见上文「方案 A/B」 |
| App 连接失败 | 确认电脑 IP 和 Token 正确；防火墙放行 8818 端口 |
| App 息屏后断开 | 确认已授予「后台运行」权限；部分手机需关闭电池优化 |
| 低延迟模式一直「连接中」 | 刷新页面重试；确认浏览器非「省流模式」；看 logs 里 `WebRTC 对等端状态` |
| 低延迟模式报「信令 HTTP 500」 | 看 logs 中 `WebRTC 信令处理失败` 具体原因 |
| 浏览器能连但不响 | 先在电脑端播放任意声音确认采集正常；MP3 模式换 VLC 试 |
| 日志位置 | `logs\yyyy-MM-dd.log`（与 exe 同目录） |

## 代码结构

```
ConsoleApp1\ConsoleApp1\         ← 服务端（C# .NET 10）
├── Program.cs        入口：装配 + 控制台横幅 + QR 码 + Ctrl+C 收尾
├── Config.cs         config.json 读写与令牌生成
├── Log.cs            控制台 + 文件日志
├── AudioHub.cs       MP3 广播中心：一写多读，慢客户端丢旧块
├── CaptureEngine.cs  WASAPI loopback 采集 + pcm16 转换 + LAME 编码
│                     + Pcm16Captured 分接事件 + 设备热插拔自动重启
├── WebRtcService.cs  低延迟通道（浏览器）：信令 + 重采样 + 10ms 帧 + Opus 编码 + RTP
├── WebSocketAudioService.cs  App 通道：WebSocket 推送 Opus 帧 + 重采样
├── StreamServer.cs   TcpListener 手写 HTTP + WebSocket：路由 / 鉴权 / 推流
└── Utils.cs          局域网 IP 枚举

App\                           ← Android App（.NET MAUI）
├── MainPage.xaml     连接配置 UI（IP / 端口 / Token）
├── MainPage.xaml.cs  WebSocket 连接 + Concentus 解码 + AudioTrack 播放
├── MauiProgram.cs    MAUI 注册
└── Platforms/Android/  AndroidManifest.xml 权限（INTERNET / FOREGROUND_SERVICE）
```

## 已知限制

- 多声道（>2ch）采集保留最前面的左/右声道；采集格式支持 float32 / pcm16。
- WAV 回退模式带宽约 1.5 Mbps，建议 VLC 播放。
- WebRTC 通道音频单向（PC → 手机），浏览器刷新会新建对等端，旧对等端自动清理。
- App 需要 Android 5.0+（API 21+），需手动安装 APK（未上架应用商店）。
