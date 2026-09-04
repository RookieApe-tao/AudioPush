#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
audio2phone — 把 Windows 全系统声音（任何 App，不只是浏览器）通过局域网推给手机播放。

原理:
    WASAPI loopback 抓取默认输出设备的实时声音（全系统混音）
    -> lameenc 编码成 MP3（没装 lameenc 则退回原始 WAV，仅 VLC 播放）
    -> Python 内置 HTTP 服务向局域网广播
    -> 手机浏览器或 VLC 打开  http://<电脑IP>:<端口>/  即可听

用法:
    python audio2phone.py [端口]        # 默认 8818，Ctrl+C 停止

依赖:
    pip install PyAudioWPatch lameenc
"""

import ctypes
import queue
import socket
import struct
import subprocess
import sys
import threading
import time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

DEFAULT_PORT = 8818
MP3_BITRATE = 192          # kbps
MP3_QUALITY = 2            # 0~9，越小音质越高、编码越慢
FRAMES_PER_BUFFER = 1024   # 每次采集帧数（约 21ms @48kHz）
CLIENT_QUEUE_BLOCKS = 400  # 每个客户端的排队缓冲块数

try:
    import pyaudiowpatch as pyaudio
except ImportError:
    pyaudio = None


# ---------------------------------------------------------------- 广播中心
class Hub:
    """采集线程 publish，HTTP 客户端 join/leave，一写多读。"""

    def __init__(self):
        self.lock = threading.Lock()
        self.clients = []                 # 每个在线客户端一个 queue.Queue
        self.mime = "audio/mpeg"          # 流的 Content-Type
        self.prelude = b""                # 连接建立后立刻发送的头部（WAV 模式用）
        self.ready = threading.Event()    # 采集 + 编码器就绪
        self.info = "初始化中…"

    def join(self):
        q = queue.Queue(maxsize=CLIENT_QUEUE_BLOCKS)
        with self.lock:
            self.clients.append(q)
        return q

    def leave(self, q):
        with self.lock:
            if q in self.clients:
                self.clients.remove(q)

    def publish(self, chunk: bytes):
        with self.lock:
            clients = list(self.clients)
        for q in clients:
            try:
                q.put_nowait(chunk)
            except queue.Full:            # 该客户端太慢：丢最旧的一块
                try:
                    q.get_nowait()
                    q.put_nowait(chunk)
                except Exception:
                    pass


HUB = Hub()


# ---------------------------------------------------------------- 编码
def open_encoder(rate: int, ch: int):
    """返回 (encoder|None, mime)。encoder=None 表示退回原始 WAV。"""
    try:
        import lameenc
    except ImportError:
        return None, "audio/wav"
    enc = lameenc.Encoder()
    enc.set_bit_rate(MP3_BITRATE)
    enc.set_in_sample_rate(rate)
    enc.set_channels(ch)
    enc.set_quality(MP3_QUALITY)
    return enc, "audio/mpeg"


def wav_header(rate: int, ch: int, bits: int = 16) -> bytes:
    """无限长流式 WAV 头（长度字段置最大值），VLC 可直接播。"""
    byte_rate = rate * ch * bits // 8
    block_align = ch * bits // 8
    return struct.pack(
        "<4sI4s4sIHHIIHH4sI",
        b"RIFF", 0xFFFFFFFF, b"WAVE", b"fmt ", 16, 1, ch,
        rate, byte_rate, block_align, bits, b"data", 0xFFFFFFFF,
    )


# ---------------------------------------------------------------- 采集
def find_loopback(p):
    """找到默认输出设备对应的 WASAPI loopback 设备；找不到返回 None。"""
    if p is None:
        return None
    try:  # PyAudioWPatch >= 0.2.12.6 自带便捷方法
        return p.get_default_wasapi_loopback()
    except Exception:
        pass
    try:
        wasapi = p.get_host_api_info_by_type(pyaudio.paWASAPI)
        default_out = p.get_device_info_by_index(wasapi["defaultOutputDevice"])
        if default_out.get("isLoopbackDevice"):
            return default_out
        for lb in p.get_loopback_device_info_generator():
            if default_out["name"] in lb["name"]:
                return lb
    except Exception:
        pass
    return None


def capture_worker(stop: threading.Event):
    pa = None
    stream = None
    try:
        dev = find_loopback(pyaudio)
        if dev is None:
            print("[!] 没有找到可用的音频输出/回环设备。")
            print("    如果电脑当前确实没有任何输出设备（无声卡/被禁用）：")
            print("      1) 安装免费的虚拟声卡 VB-Cable  https://vb-audio.com/Cable/")
            print("      2) 系统声音设置里把「CABLE Input」设为默认输出设备")
            print("      3) 重新运行本脚本（抓的仍是全系统声音）")
            stop.set()
            return

        rate = int(dev["defaultSampleRate"])
        ch = int(dev["maxInputChannels"])
        HUB.info = f"{dev['name']} @ {rate}Hz {ch}ch"
        print(f"[*] 采集设备: {HUB.info}")

        enc_ch = min(ch, 2)               # lameenc 最多 2 声道
        enc, mime = open_encoder(rate, enc_ch)
        HUB.mime = mime
        if enc is None:
            HUB.prelude = wav_header(rate, enc_ch)
            print("[*] 未安装 lameenc，退回原始 WAV 模式（带宽约 1.5Mbps，建议用 VLC 播放）。")
        else:
            print(f"[*] 编码: MP3 {MP3_BITRATE}kbps")

        pa = pyaudio.PyAudio()
        stream = pa.open(
            format=pyaudio.paInt16,
            channels=ch,
            rate=rate,
            frames_per_buffer=FRAMES_PER_BUFFER,
            input=True,
            input_device_index=int(dev["index"]),
        )
        HUB.ready.set()
        print("[*] 采集开始（抓取系统全部声音）。")

        def downmix_front(data: bytes) -> bytes:
            """多于 2 声道时，保留最前面的左/右声道。"""
            step = ch * 2
            out = bytearray()
            for i in range(0, len(data), step):
                out += data[i:i + 4]
            return bytes(out)

        while not stop.is_set():
            data = stream.read(FRAMES_PER_BUFFER, exception_on_overflow=False)
            if ch > 2:
                data = downmix_front(data)
            if enc is None:
                HUB.publish(data)
            else:
                mp3 = enc.encode(data)
                if mp3:
                    HUB.publish(mp3)
    except Exception as e:
        print(f"[!] 采集线程出错: {e!r}")
        stop.set()
    finally:
        try:
            stream.stop_stream()
            stream.close()
        except Exception:
            pass
        try:
            pa.terminate()
        except Exception:
            pass


# ---------------------------------------------------------------- HTTP
INDEX_HTML = """<!doctype html>
<html><head><meta charset="utf-8">
<meta name="viewport" content="width=device-width,initial-scale=1">
<title>电脑声音</title></head>
<body style="font-family:sans-serif;max-width:640px;margin:40px auto;padding:0 16px">
<h2>&#128266; 电脑声音实时转发</h2>
<p><audio controls autoplay style="width:100%" src="/stream"></audio></p>
<p>延迟约 2~5 秒，属正常现象。若浏览器不能播放，请安装 <b>VLC</b>，
在「网络串流」里打开 <code>http://&lt;电脑IP&gt;:8818/stream</code>。</p>
<p>电脑端按 Ctrl+C 停止推流。</p>
</body></html>
"""


class Handler(BaseHTTPRequestHandler):
    def do_GET(self):
        path = self.path.split("?")[0]
        if path in ("/", "/index.html"):
            body = INDEX_HTML.encode("utf-8")
            self.send_response(200)
            self.send_header("Content-Type", "text/html; charset=utf-8")
            self.send_header("Content-Length", str(len(body)))
            self.end_headers()
            self.wfile.write(body)
        elif path.startswith("/stream"):
            q = HUB.join()
            try:
                self.send_response(200)
                self.send_header("Content-Type", HUB.mime)
                self.send_header("Cache-Control", "no-cache, no-store")
                self.send_header("Connection", "close")
                self.send_header("Access-Control-Allow-Origin", "*")
                self.end_headers()
                if HUB.prelude:
                    self.wfile.write(HUB.prelude)
                while True:
                    try:
                        chunk = q.get(timeout=30)
                    except queue.Empty:
                        break                     # 30 秒无数据则断开
                    self.wfile.write(chunk)
            except (BrokenPipeError, ConnectionResetError,
                    ConnectionAbortedError, TimeoutError):
                pass
            except Exception:
                pass
            finally:
                HUB.leave(q)
        elif path == "/status":
            with HUB.lock:
                n = len(HUB.clients)
            body = ('{"clients": %d, "mime": "%s"}' % (n, HUB.mime)).encode()
            self.send_response(200)
            self.send_header("Content-Type", "application/json")
            self.send_header("Content-Length", str(len(body)))
            self.end_headers()
            self.wfile.write(body)
        else:
            self.send_error(404)

    def log_message(self, fmt, *args):  # 安静模式
        pass


# ---------------------------------------------------------------- 工具
def lan_ips():
    ips = []
    try:  # 主网卡 IP（UDP trick，不实际发包）
        s = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        s.connect(("8.8.8.8", 80))
        ips.append(s.getsockname()[0])
        s.close()
    except Exception:
        pass
    try:
        for info in socket.getaddrinfo(socket.gethostname(), None, socket.AF_INET):
            ip = info[4][0]
            if (ip.startswith(("192.168.", "10.", "172.")) and not ip.startswith("127.")
                    and ip not in ips):
                ips.append(ip)
    except Exception:
        pass
    return ips


def firewall_hint(port: int):
    cmd = ('netsh advfirewall firewall add rule name="audio2phone" '
           f'dir=in action=allow protocol=TCP localport={port}')
    try:
        is_admin = ctypes.windll.shell32.IsUserAnAdmin() != 0
    except Exception:
        is_admin = False
    if is_admin:
        try:
            subprocess.run(cmd, shell=True, capture_output=True, timeout=15)
            print("[*] 已添加防火墙放行规则 (audio2phone)。")
            return
        except Exception:
            pass
    print("[*] 如果手机连不上，请在管理员终端执行：")
    print(f"    {cmd}")
    print("    或首次运行时在 Windows 防火墙弹窗中勾选专用+公用网络并允许。")


def main():
    port = DEFAULT_PORT
    if len(sys.argv) > 1:
        try:
            port = int(sys.argv[1])
        except ValueError:
            print(f"[!] 端口需为数字: {sys.argv[1]}")
            sys.exit(2)

    if pyaudio is None:
        print("[!] 缺少依赖，请先执行:  pip install PyAudioWPatch lameenc")
        sys.exit(1)

    stop = threading.Event()
    threading.Thread(target=capture_worker, args=(stop,), daemon=True).start()

    deadline = time.time() + 8
    while not HUB.ready.is_set() and not stop.is_set() and time.time() < deadline:
        time.sleep(0.1)
    if stop.is_set():
        sys.exit(1)

    print()
    print("=" * 60)
    print(" 手机与电脑连同一个 Wi-Fi，然后用任意一种方式打开：")
    for ip in lan_ips():
        print(f"   网页播放:  http://{ip}:{port}/")
        print(f"   音频流:    http://{ip}:{port}/stream")
    print(f" 当前模式: {HUB.mime} | 延迟约 2~5 秒属正常 | Ctrl+C 停止")
    print("=" * 60)
    print()

    firewall_hint(port)

    srv = ThreadingHTTPServer(("0.0.0.0", port), Handler)
    srv.daemon_threads = True
    try:
        srv.serve_forever()
    except KeyboardInterrupt:
        pass
    print("\n[*] 已停止。")


if __name__ == "__main__":
    main()
