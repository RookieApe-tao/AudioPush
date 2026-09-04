# audio2phone（yinpintuisong）

把 **Windows 全系统声音**（任何 App：视频、游戏、音乐、网页……）通过**局域网**实时推送到手机播放。
适用于电脑没有音箱/耳机（或输出设备损坏）的场景，手机就是你的「无线音箱」。

```
任意 App 播放声音
   ↓  WASAPI loopback（抓取系统混音，无需虚拟声卡即可抓默认设备）
CaptureEngine（NAudio + LAME 编码 MP3 192kbps，编码器不可用自动退回 WAV）
   ↓
StreamServer（TcpListener 手写 HTTP，免管理员权限，支持多客户端）
   ↓  Wi-Fi 局域网
手机浏览器 / VLC   http://电脑IP:8818/?token=xxxxxxxx
```

## 快速开始

1. **运行**（任选其一）：
   - Visual Studio 打开 `ConsoleApp1.slnx` 按 F5；
   - 命令行：`dotnet run --project ConsoleApp1\ConsoleApp1 -c Release`；
   - 直接双击编译产物 `ConsoleApp1\ConsoleApp1\bin\Release\net10.0\yinpintuisong.exe`。
2. 首次运行自动生成 `config.json`（含随机 **8 位访问令牌 Token**），控制台打印可访问地址。
3. 手机连同一 Wi-Fi，浏览器或 VLC 打开打印的完整地址（**必须带 `?token=`**）。
4. 首次运行如弹出 Windows 防火墙提示，勾选「专用网络和公用网络」后允许；
   或管理员执行：
   ```
   netsh advfirewall firewall add rule name="audio2phone" dir=in action=allow protocol=TCP localport=8818
   ```

## ⚠️ 电脑当前没有任何输出设备时（本机现状）

程序采集的是「默认输出设备的系统混音」。若设备列表为空（日志报 `0x80070490` 找不到元素），
任选其一解决，**程序每 2~30 秒自动重试，设备出现后无需重启自动恢复**：

- **方案 A（推荐，无需硬件）**：安装免费虚拟声卡
  [VB-Cable](https://vb-audio.com/Cable/) → 系统声音设置里把「**CABLE Input**」设为默认输出设备。
  之后所有 App 的声音都进虚拟声卡，被本工具抓走推给手机。
- **方案 B**：往机箱插一副任意耳机/音箱 → 端点自动激活（本地也能听）。

## 鉴权

- 音频流 `/stream` 与状态 `/status` 必须携带令牌，三种方式等价：
  - URL 参数：`?token=xmju7p22`（推荐，手机书签一次搞定）
  - 请求头：`X-Token: xmju7p22`
  - 请求头：`Authorization: Bearer xmju7p22`
- 令牌在 `config.json` 的 `Token` 字段，可自行修改后重启；
  `"RequireAuth": false` 可关闭鉴权（不建议，流会暴露给整个局域网）。
- 无 token 访问 `/` 会得到提示页（不泄露令牌本身）；带 token 访问得到带播放器的页面。

## 配置项（config.json）

| 字段 | 默认 | 说明 |
|---|---|---|
| `Port` | 8818 | HTTP 监听端口（改端口后记得同步防火墙规则） |
| `Mp3Bitrate` | 192 | MP3 码率 kbps（LAN 内 128~256 均可） |
| `RequireAuth` | true | 是否强制 token 鉴权 |
| `Token` | 随机 | 访问令牌 |

## 接口

| 路径 | 说明 |
|---|---|
| `/` 或 `/index.html` | 播放页（带 token 时内嵌 `<audio>` 播放器） |
| `/stream` | 音频流（`audio/mpeg`，MP3 不可用时 `audio/wav`） |
| `/status` | JSON 状态：客户端数、编码格式、采集源、鉴权开关 |

## 延迟与限制

- HTTP + MP3 + 播放器缓冲，端到端延迟约 **2~5 秒**：听音乐/看直播没问题，
  **不适合对着口型看本地视频**（需要低延迟得换 WebRTC/UDP 方案）。
- 多声道（>2ch）采集会保留最前面的左/右声道。
- 采集格式支持 float32 / pcm16（WASAPI 共享模式的两种标准格式）。
- WAV 回退模式带宽约 1.5 Mbps，建议 VLC 播放。

## 故障排查

| 现象 | 处理 |
|---|---|
| 手机打不开页面 | 确认同一 Wi-Fi；防火墙放行 exe/端口；地址里 IP 是电脑的局域网 IP |
| `401 unauthorized` | 地址缺 `?token=`，令牌看 `config.json` |
| 日志报 0x80070490 | 没有任何输出设备 → 见上文「方案 A/B」 |
| 浏览器能连但不响 | 设备刚恢复时等 2~30 秒自动重连；或换 VLC 试 |
| 日志位置 | `logs\yyyy-MM-dd.log`（与 exe 同目录） |

## 代码结构

```
ConsoleApp1\ConsoleApp1\
├── Program.cs        入口：装配 + 控制台横幅 + Ctrl+C 收尾
├── Config.cs         config.json 读写与令牌生成
├── Log.cs            控制台 + 文件日志
├── AudioHub.cs       一写多读广播中心（慢客户端丢旧块，不阻塞采集）
├── CaptureEngine.cs  WASAPI loopback 采集 + float→pcm16 转换 + LAME 编码
│                     + 设备热插拔/默认设备变更自动重启
├── StreamServer.cs   TcpListener 手写 HTTP：路由 / 鉴权 / 流推送
└── Utils.cs          局域网 IP 枚举
```
