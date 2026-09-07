# CLAUDE.md

本文件为 Claude Code (claude.ai/code) 在此仓库中工作时提供指导。

## 项目概述

**audio2phone（yinpintuisong）**：把 Windows 全系统声音（任何 App 的混音）通过局域网实时推送到手机播放。适用于电脑没有音箱/耳机（或输出设备损坏）的场景，手机即「无线音箱」。

核心链路（三通道）：
```
任意 App 播放声音
   ↓  WASAPI loopback（抓取默认输出设备的系统混音，无需虚拟声卡即可采集）
CaptureEngine（NAudio 采集 + float→pcm16 转换，分接三路）
   ├─→ App 通道：WebSocket 推送 Opus 10ms 帧，约 100ms，息屏不中断
   ├─→ 低延迟通道：WebRTC Opus（浏览器 ~0.1 秒，息屏自动重连）
   └─→ 兼容通道：MP3 192kbps（浏览器 <audio> / VLC，延迟 2~5 秒）
   ↓
StreamServer（TcpListener 手写 HTTP + WebSocket，免管理员权限，token 鉴权）
   ↓  Wi-Fi 局域网
手机 App / 浏览器 / VLC   http://<电脑IP>:8818/?token=xxx   ws://<IP>:8818/ws?token=xxx
```

仓库包含三套客户端：
- **Android App（推荐）**：`App/` 目录，.NET MAUI，WebSocket 接收 Opus + AudioTrack 播放，后台 ForegroundService 保活，**息屏不断**，延迟约 100ms。
- **C# 服务端**：`ConsoleApp1/` 解决方案，.NET 10（net10.0），程序集名 `yinpintuisong`。
- **Python 版（参考/备用实现）**：根目录 `audio2phone.py` 单文件脚本，依赖 `PyAudioWPatch` + `lameenc`，默认端口同为 8818。仅 MP3 兼容通道，无 WebRTC。

## 常用命令

```bash
# 构建 & 运行（C# 主实现，任选其一）
dotnet run --project ConsoleApp1/ConsoleApp1 -c Release
# 或 Visual Studio 打开 ConsoleApp1/ConsoleApp1.slnx 按 F5
# 或直接运行编译产物 ConsoleApp1/ConsoleApp1/bin/Release/net10.0/yinpintuisong.exe

# 构建 Android App
dotnet build App/Audio2PhoneApp.csproj -f net9.0-android -c Debug
# ADB 安装（需连接手机）
D:\Android\android-sdk\platform-tools\adb.exe install -r App/bin/Debug/net9.0-android/com.companyname.audio2phoneapp-Signed.apk

# Python 版
pip install PyAudioWPatch lameenc
python audio2phone.py [端口]      # 默认 8818，Ctrl+C 停止

# 防火墙放行（首次运行也可在弹窗中勾选允许）
netsh advfirewall firewall add rule name="audio2phone" dir=in action=allow protocol=TCP localport=8818
```

没有单元测试；验证改动以「能编译 + 实机播放」为主。WebRTC 通道可用无头浏览器打开播放页点
「低延迟模式」按钮，读 `window.__rtc.state / bytes` 验证（bytes 持续增长 = RTP 正常）。

## 架构与代码结构

### 服务端（C# .NET 10）

```
ConsoleApp1/ConsoleApp1/
├── Program.cs        入口：装配各组件 + 控制台横幅 + QR 码 + Ctrl+C 收尾
├── Config.cs         config.json 读写与随机 8 位令牌生成
├── Log.cs            控制台 + 文件日志（logs\yyyy-MM-dd.log）
├── AudioHub.cs       一写多读广播中心：慢客户端丢旧块，绝不阻塞采集线程
├── CaptureEngine.cs  WASAPI loopback 采集 + float→pcm16 转换 + LAME 编码
│                     + Pcm16Captured 分接事件（供 WebRTC + WebSocket 通道）
│                     + 设备热插拔/默认设备变更自动重启采集（每 2~30 秒重试）
├── WebRtcService.cs  低延迟通道（浏览器）：信令 + 重采样 + 10ms 帧 + Opus 编码 + RTP
├── WebSocketAudioService.cs  App 通道：WebSocket 推送 Opus 帧 + 重采样
├── StreamServer.cs   TcpListener 手写 HTTP + WebSocket：路由 / 鉴权 / 推流 / 播放页
└── Utils.cs          局域网 IP 枚举
```

### Android App（.NET MAUI）

```
App/
├── MainPage.xaml     连接配置 UI（IP / 端口 / Token / 音量滑块）
├── MainPage.xaml.cs  WebSocket 连接 + Concentus Opus 解码 + AudioTrack 播放
├── MauiProgram.cs    MAUI 注册
├── Platforms/Android/  AndroidManifest.xml 权限（INTERNET / FOREGROUND_SERVICE / WAKE_LOCK）
└── Audio2PhoneApp.csproj  依赖 Concentus 2.2.2
```

## 关键设计与注意事项

- **鉴权**：`/stream`、`/status`、`/rtc`、`/ws` 必须带令牌，三种等价方式：URL 参数 `?token=`、请求头 `X-Token:`、`Authorization: Bearer x`。令牌存于 `config.json` 的 `Token` 字段；`RequireAuth: false` 可关闭（不安全，不建议）。
- **config.json 首次运行自动生成**（含随机 Token），与 exe 同目录；**不要提交到 git**（含令牌，已在 .gitignore 排除）。
- **无输出设备时**采集会报 `0x80070490`：程序每 2~30 秒自动重试，设备出现后自动恢复，无需重启。无硬件时推荐装 VB-Cable（仓库根目录附带 `VBCABLE_Driver_Pack45.zip`）并把「CABLE Input」设为默认输出。
- **AudioHub 契约**：采集线程是唯一写入方，任何下游（编码、HTTP/WebSocket 推流）都不允许反向阻塞它；慢客户端靠丢弃旧块处理。
- **WebSocket 通道**：App 通过 `ws://host:port/ws?token=xxx` 连接，服务端用 `WebSocket.CreateFromStream` 从 TcpListener 流升级，推送 Opus 10ms 帧（帧格式：2 字节小端长度 + Opus 数据）。设备混音非 48kHz 时线性重采样。
- **WebRTC 通道**：纯局域网 host 候选，无需 STUN/TURN。SIPSorcery v10 的注意点——`AudioFormat` 5 参构造首参是 formatID（payload type，必须 ≤127），`localDescription.sdp` 是 SDP 对象需 `ToString()` 后再 JSON 下发，信令请求体是 JSON 信封需先解包取 `sdp`。
- **延迟**：App 通道约 100ms（推荐，息屏不中断）；WebRTC 通道约 0.1~0.2 秒（浏览器，息屏断连自动重连）；MP3 兼容通道约 2~5 秒。
- 多声道（>2ch）采集保留最前面的左/右声道；采集格式支持 float32 / pcm16（WASAPI 共享模式两种标准格式）；WAV 回退模式约 1.5 Mbps，建议用 VLC 播放。
- **Android App**：用 `AudioTrack` 低延迟播放，`Concentus` 纯 C# 解码（无原生依赖）。ForegroundService 保活，通知栏显示播放状态。配置自动保存到 `Preferences`。

## 配置项（config.json）

| 字段 | 默认 | 说明 |
|---|---|---|
| `Port` | 8818 | HTTP/WebSocket 监听端口（改端口后同步防火墙规则） |
| `Mp3Bitrate` | 192 | MP3 码率 kbps（LAN 内 128~256 均可） |
| `RequireAuth` | true | 是否强制 token 鉴权 |
| `Token` | 随机 8 位 | 访问令牌 |

## HTTP 接口

| 路径 | 说明 |
|---|---|
| `/` 或 `/index.html` | 播放页（低延迟按钮 + MP3 兼容播放器） |
| `/stream` | MP3 音频流（`audio/mpeg`，MP3 不可用时 `audio/wav`） |
| `/rtc` | WebRTC 信令：POST JSON `{type:"offer", sdp}` → 返回 answer JSON |
| `/ws` | WebSocket 低延迟通道：`ws://host:port/ws?token=xxx`，推送 Opus 帧 |
| `/status` | JSON 状态：MP3 客户端数、WebRTC 对等端数、WebSocket 客户端数、编码格式、采集源 |

## Git 约定

- 提交排除：`bin/`、`obj/`、`.vs/`、`logs/`、`config.json`（见 `.gitignore`）。
- `VBCABLE_Driver_Pack45.zip` 为分发给用户使用的免安装驱动包，随仓库保留。
- `App/` 目录为 MAUI Android 项目，构建产物（`bin/`、`obj/`）不提交。
