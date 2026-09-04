# CLAUDE.md

本文件为 Claude Code (claude.ai/code) 在此仓库中工作时提供指导。

## 项目概述

**audio2phone（yinpintuisong）**：把 Windows 全系统声音（任何 App 的混音）通过局域网实时推送到手机播放。适用于电脑没有音箱/耳机（或输出设备损坏）的场景，手机即「无线音箱」。

核心链路：
```
任意 App 播放声音
   ↓  WASAPI loopback（抓取默认输出设备的系统混音，无需虚拟声卡即可采集）
CaptureEngine（NAudio + LAME 编码 MP3 192kbps，编码器不可用自动退回 WAV）
   ↓
StreamServer（TcpListener 手写 HTTP，免管理员权限，支持多客户端）
   ↓  Wi-Fi 局域网
手机浏览器 / VLC   http://<电脑IP>:8818/?token=xxxxxxxx
```

仓库包含两套实现：
- **C# 版（主实现）**：`ConsoleApp1/` 解决方案，.NET 10（net10.0），程序集名 `yinpintuisong`。
- **Python 版（参考/备用实现）**：根目录 `audio2phone.py` 单文件脚本，依赖 `PyAudioWPatch` + `lameenc`，默认端口同为 8818。

## 常用命令

```bash
# 构建 & 运行（C# 主实现，任选其一）
dotnet run --project ConsoleApp1/ConsoleApp1 -c Release
# 或 Visual Studio 打开 ConsoleApp1/ConsoleApp1.slnx 按 F5
# 或直接运行编译产物 ConsoleApp1/ConsoleApp1/bin/Release/net10.0/yinpintuisong.exe

# Python 版
pip install PyAudioWPatch lameenc
python audio2phone.py [端口]      # 默认 8818，Ctrl+C 停止

# 防火墙放行（首次运行也可在弹窗中勾选允许）
netsh advfirewall firewall add rule name="audio2phone" dir=in action=allow protocol=TCP localport=8818
```

没有单元测试；验证改动以「能编译 + 实机播放」为主。

## 架构与代码结构（C# 版）

```
ConsoleApp1/ConsoleApp1/
├── Program.cs        入口：装配各组件 + 控制台横幅 + Ctrl+C 收尾
├── Config.cs         config.json 读写与随机 8 位令牌生成
├── Log.cs            控制台 + 文件日志（logs\yyyy-MM-dd.log）
├── AudioHub.cs       一写多读广播中心：慢客户端丢旧块，绝不阻塞采集线程
├── CaptureEngine.cs  WASAPI loopback 采集 + float→pcm16 转换 + LAME 编码
│                     + 设备热插拔/默认设备变更自动重启采集（每 2~30 秒重试）
├── StreamServer.cs   TcpListener 手写 HTTP：路由 / token 鉴权 / 流推送
└── Utils.cs          局域网 IP 枚举
```

## 关键设计与注意事项

- **鉴权**：`/stream` 与 `/status` 必须带令牌，三种等价方式：URL 参数 `?token=`、请求头 `X-Token:`、`Authorization: Bearer x`。令牌存于 `config.json` 的 `Token` 字段；`RequireAuth: false` 可关闭（不安全，不建议）。
- **config.json 首次运行自动生成**（含随机 Token），与 exe 同目录；**不要提交到 git**（含令牌，已在 .gitignore 排除）。
- **无输出设备时**采集会报 `0x80070490`：程序每 2~30 秒自动重试，设备出现后自动恢复，无需重启。无硬件时推荐装 VB-Cable（仓库根目录附带 `VBCABLE_Driver_Pack45.zip`）并把「CABLE Input」设为默认输出。
- **AudioHub 契约**：采集线程是唯一写入方，任何下游（编码、HTTP 推流）都不允许反向阻塞它；慢客户端靠丢弃旧块处理。
- **延迟**：HTTP + MP3 + 播放器缓冲，端到端约 2~5 秒；不适合对口型看本地视频，低延迟需换 WebRTC/UDP 方案。
- 多声道（>2ch）采集保留最前面的左/右声道；采集格式支持 float32 / pcm16（WASAPI 共享模式两种标准格式）；WAV 回退模式约 1.5 Mbps，建议用 VLC 播放。

## 配置项（config.json）

| 字段 | 默认 | 说明 |
|---|---|---|
| `Port` | 8818 | HTTP 监听端口（改端口后同步防火墙规则） |
| `Mp3Bitrate` | 192 | MP3 码率 kbps（LAN 内 128~256 均可） |
| `RequireAuth` | true | 是否强制 token 鉴权 |
| `Token` | 随机 8 位 | 访问令牌 |

## HTTP 接口

| 路径 | 说明 |
|---|---|
| `/` 或 `/index.html` | 播放页（带 token 时内嵌 `<audio>` 播放器） |
| `/stream` | 音频流（`audio/mpeg`，MP3 不可用时 `audio/wav`） |
| `/status` | JSON 状态：客户端数、编码格式、采集源、鉴权开关 |

## Git 约定

- 提交排除：`bin/`、`obj/`、`.vs/`、`logs/`、`config.json`（见 `.gitignore`）。
- `VBCABLE_Driver_Pack45.zip` 为分发给用户使用的免安装驱动包，随仓库保留。
