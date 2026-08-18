# ScreenCast — C# 安卓投屏（低延迟接收端）

一个用 **C# / WPF** 编写的安卓投屏接收端（类 scrcpy），支持 USB 与无线连接、低延迟画面、反向控制与声音回传，界面采用 **Material Design 3** 风格。

![.NET](https://img.shields.io/badge/.NET-8.0-512BD4) ![Platform](https://img.shields.io/badge/Platform-Windows-blue) ![License](https://img.shields.io/badge/License-MIT-green)

## ✨ 功能特性

- 📱 **USB 数据线连接**（ADB）
- 📶 **无线连接**（`adb tcpip` + `adb connect`）
- ⚡ **低延迟投屏**：H.264 硬件编码 + 本地 Socket 传输 + FFmpeg 解码
- 🖱️ **电脑反向控制手机**：鼠标点击/拖动、滚轮、键盘、返回键
- 🔊 **电脑播放手机声音**：OPUS → FFmpeg 解码 → NAudio 播放
- ⚡ **高帧率**：最高支持 120 FPS（可在设置里选择）
- 🖥️ **独立投屏窗口**：手机画面在单独窗口显示，画面可自由缩放/切换显示模式
- 🎨 **Material Design 3** 风格界面（圆角卡片、MD3 色板、Filled/Outlined 控件）

> 开箱即用：依赖（.NET SDK / FFmpeg 原生 dll / ADB / scrcpy-server）均已就绪，
> `dotnet run --project src/ScreenCast` 即可启动。

---

## 📸 界面示意
<img width="1226" height="753" alt="image" src="https://github.com/user-attachments/assets/cf7eb4f4-6f8d-4156-8945-58cbdc8de1dd" />
<img width="415" height="914" alt="image" src="https://github.com/user-attachments/assets/c8d824ed-8179-409c-99cf-575adcbe35a8" />


```
┌─ 主窗口（控制面板）───────────────────────────┐   ┌─ 独立投屏窗口 ───────────┐
│ 顶栏：刷新设备 | USB连接 | 显示模式▾ | 开始投屏 |   │                          │
│ ┌─────┐  ┌──────────────────────────────┐ │   │   手机画面（可缩放）        │
│ │设备  │  │  手机画面将在独立窗口显示        │ │   │   （铺满/等比/完整显示）    │
│ │列表  │  │  开始投屏后自动弹出             │ │   │                          │
│ └─────┘  └──────────────────────────────┘ │   └──────────────────────────┘
│ 右侧：分辨率 / 码率 / 帧率 / 音频 / 控制      │
└────────────────────────────────────────────┘
```

---

## 🏗️ 总体架构（低延迟链路）

```
┌─────────────── Android 手机 ────────────────┐     ┌────────────── PC (WPF) ──────────────┐
│  MediaProjection 采集屏幕 / 采集声音          │     │                                      │
│  MediaCodec H.264 编码 + OPUS 音频编码        │     │  AdbService: adb forward / tcpip      │
│  监听 localabstract:scrcpy（视频/音频/控制）  │─ADB─▶│  StreamReceiver: 三通道 TcpClient      │
│                                             │     │  ├ 视频: FfmpegDecoder → BGRA → 画面   │
│                                             │     │  ├ 音频: AudioChannel (OPUS→NAudio)    │
│                                             │     │  └ 控制: ControlChannel (鼠标/键盘)     │
└──────────────────────────────────────────────┘     └──────────────────────────────────────┘
```

- **传输**：`adb forward tcp:<本地> localabstract:scrcpy`，数据走本地回环，USB 下几乎零额外延迟；无线模式先 `adb connect` 再走同一通道。
- **编码**：手机端 `MediaCodec` H.264 硬编码 + OPUS 音频编码。
- **解码**：PC 端 FFmpeg（`FFmpeg.AutoGen`）解码，视频转 BGRA 直接上屏，音频转 PCM 用 NAudio 播放。
- **控制**：鼠标/键盘事件编码为 scrcpy 控制协议，经独立控制通道回传手机。

---

## 🛠️ 环境要求

| 依赖 | 说明 |
|------|------|
| **Windows 10/11** | 目标平台 |
| **.NET SDK 8.0+** | 编译运行 |
| **ADB** | Android platform-tools；`AdbService` 会自动在环境变量、常见路径、`winget` 包目录中查找 `adb.exe` |
| **FFmpeg 原生 dll** | 由 NuGet 包 `Sdcb.FFmpeg.runtime.windows-x64` 构建时自动复制到输出目录 `ffmpeg/` |
| **scrcpy-server** | 已随仓库内置在 `Assets/`（v4.1），运行期自动 `adb push` + `app_process` 启动 |

---

## 🚀 快速开始

```powershell
# 1. 还原依赖（联网拉取 NuGet）
dotnet restore

# 2. 编译
dotnet build -c Debug

# 3. 运行
dotnet run --project src/ScreenCast
```

### 手机准备
- **USB 连接**：开启「开发者选项 → USB 调试」。
- **无线连接**：开启「无线调试」，记录 IP 与端口。

### 使用步骤
1. 点 **刷新设备**，列表显示 `adb devices` 结果。
2. USB：选中设备 → **USB 连接**。
3. 无线：填 IP/端口 → **无线连接**。
4. 点 **开始投屏** → 自动弹出**独立投屏窗口**。
5. 在投屏窗口内用鼠标/键盘操作手机（需勾选「电脑反向控制手机」）。

### 设置项
| 设置 | 说明 |
|------|------|
| 分辨率 | 1920×1080 / 1280×720 / 854×480 |
| 码率 (Mbps) | 默认 8，越大越清晰越占带宽 |
| 帧率 | 60 / 90 / 120，越高越流畅越占带宽 |
| 播放手机声音 | 勾选后经电脑扬声器播放手机声音 |
| 反向控制 | 勾选后可用鼠标/键盘控制手机 |
| 显示模式（顶栏） | 铺满 / 等比 / 完整显示 |

---

## 📁 目录结构

```
ScreenCast.sln
src/ScreenCast/
├── ScreenCast.csproj                  # 引用 FFmpeg 原生 dll 与 NAudio
├── App.xaml / App.xaml.cs             # 全局异常处理（写日志 + 弹窗）
├── MainWindow.xaml / .cs              # 主界面（MD3，控制面板）
├── CastWindow.xaml / .cs              # 独立投屏窗口（含显示模式切换）
├── Themes/
│   ├── Colors.xaml                    # MD3 色板
│   ├── Typography.xaml                # 字体/字阶
│   ├── Controls.xaml                  # Button/Card/ComboBox 样式
│   └── Styles.xaml                    # 合并字典
├── Models/
│   ├── AdbDevice.cs
│   └── StreamSettings.cs
├── Services/
│   ├── IAdbService.cs / AdbService.cs     # adb 定位、设备枚举、USB/无线、forward
│   ├── IStreamReceiver.cs / StreamReceiver.cs  # 三通道接收 + 启动 scrcpy-server + 分帧解析
│   ├── ControlChannel.cs                  # 反向控制协议（触摸/按键/滚动/返回）
│   ├── AudioChannel.cs                    # OPUS 音频解码 + NAudio 播放
│   └── Decoder/
│       ├── IFrameDecoder.cs
│       └── FfmpegDecoder.cs               # H.264 -> BGRA 像素（FFmpeg 硬解）
├── Assets/
│   └── scrcpy-server                      # 手机端采集服务（v4.1，已内置）
└── Converters/
    └── BoolToVisibilityConverter.cs
```

---

## 🔌 协议说明（对接 scrcpy-server）

- **服务器启动**：`CLASSPATH=... app_process / com.genymobile.scrcpy.Server <版本号> <参数...>`
  - 第一个参数必须是客户端版本号（与 server 版本一致，当前为 `4.1`）。
  - 参数为下划线风格、不带 `--`，例如 `video=true audio=true control=true tunnel_forward=true max_size=1920 video_bit_rate=8000000 max_fps=90`。
- **视频通道**：`dummy(1) + 设备名(64) + codecId(4) + session(12) + [frameMeta(12)+H.264]*`
  - 旋转/分辨率变化时，帧间插入原始 session 头 `[flags:4][width:4][height:4]`，程序已自动处理。
  - H.264 自动识别 Annex-B / AVCC 并统一转 Annex-B 解码。
- **音频通道**：`codecId(4) + [frameMeta(12)+OPUS]*`（音频无 dummy 字节）。
- **控制通道**：`dummy(1) + 控制消息`（大端序，触摸/按键/滚动/返回等）。

---

## 🧩 已知限制与说明

- 反向控制与声音回传按 scrcpy 4.1 真实协议实现，已在部分机型（小米 / Android 16）实测可用。
- 音频为 48kHz / 16bit / 双声道，延迟随网络波动。
- 高帧率 / 高分辨率会显著增加码率与 CPU 占用，卡顿时可调低分辨率或帧率。
- 无线连接需手机与电脑在同一局域网；USB 连接保持数据线为「文件传输/调试」模式。
- 若 `adb devices` 显示 `unauthorized`，需在手机上允许调试授权。

---

## 📄 许可证

本项目仅供学习交流使用，请遵守相关开源协议与当地法律。

- [scrcpy](https://github.com/Genymobile/scrcpy) — GPL v3（本项目仅在运行期调用其 server，不包含其源码）
- 使用前请确认你拥有所投屏设备的授权。
