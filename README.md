# TaskbarLyrics · QQ音乐任务栏歌词

[![CI](https://github.com/DoingStone/DesktopMusic/actions/workflows/ci.yml/badge.svg)](https://github.com/DoingStone/DesktopMusic/actions/workflows/ci.yml)
[![Release](https://img.shields.io/github/v/release/DoingStone/DesktopMusic?label=release)](https://github.com/DoingStone/DesktopMusic/releases/latest)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)
[![.NET](https://img.shields.io/badge/.NET-8.0-512BD4)](https://dotnet.microsoft.com/download/dotnet/8.0)

把 **QQ音乐** 正在播放的歌词画在 Windows 任务栏的空白区域（时钟左侧）：双行显示、按演唱进度逐字高亮、有翻译就联动显示，鼠标移上去才让出位置并显形播放控制按键。

不注入、不 Hook 播放器，只读系统媒体会话；**零第三方依赖**（.NET 8 + WPF）；歌词条可拖动、可脱离任务栏悬浮到屏幕任意位置。

歌词匹配与解析思路参考了 [ANYNC/TaskbarLyrics](https://github.com/ANYNC/TaskbarLyrics) 与 [WXRIW/Lyricify-Lyrics-Helper](https://github.com/WXRIW/Lyricify-Lyrics-Helper)，代码为独立实现。

---

## 目录

- [功能一览](#功能一览)
- [下载与安装](#下载与安装)
- [快速开始](#快速开始)
- [设置界面](#设置界面)
- [拖动、悬浮与吸附](#拖动悬浮与吸附)
- [播放控制与全局快捷键](#播放控制与全局快捷键)
- [环境要求](#环境要求)
- [从源码构建与发布](#从源码构建与发布)
- [工程结构](#工程结构)
- [技术要点](#技术要点)
- [配置文件](#配置文件)
- [命令行诊断](#命令行诊断)
- [验证状态](#验证状态)
- [故障排查](#故障排查)
- [已知限制](#已知限制)
- [更新日志](#更新日志)
- [致谢与许可](#致谢与许可)

---

## 功能一览

| 能力 | 说明 |
| --- | --- |
| **任务栏歌词** | 直接在任务栏空白区渲染，不占窗口、不进 Alt+Tab |
| **逐字高亮** | 当前行按演唱进度用高亮色「刷」过去，而不是整行闪烁 |
| **翻译行 / 下一行预览** | 有翻译时在原文下方联动显示；下一行提前淡入 |
| **悬停显形** | 鼠标移入左侧淡入封面、歌名、歌手与播放按键，歌词缓动让位；移开自动收起 |
| **点击穿透** | 锁定状态下完全不挡任务栏操作；但悬停时仍会显形播放按键，且仍可点击 |
| **可拖动 / 可悬浮** | 解锁后直接拖动；拖出任务栏即成悬浮小条，可放屏幕任意位置 |
| **吸附对齐** | 拖动时自动吸附屏幕边缘、水平/垂直居中与任务栏那一行 |
| **自适应配色** | 按任务栏明暗自动决定字色，浅色任务栏配深色字，无需背景板 |
| **超长行滚动** | 文字超出可用区域时改为跑马灯，不会与播放按键重叠 |
| **多源检索** | QQ音乐 / 网易云音乐 / LRCLIB 三源并发，加权打分择优 |

---

## 效果

鼠标移入时左侧淡入唱片封面、歌名、歌手与播放控制，歌词让位到右侧；移开后歌词缓动回整条正中：

![任务栏歌词](docs/overlay.png)

设置界面（Win11 设置页风格，三个分页，改动即时生效）：

![设置界面](docs/settings.png)

```
┌─ 任务栏 ─────────────────────────────────────────────────────────┐
│  ...   │  丢掉了画画和长发  │  🔊 🌐  14:32  2026/9/17  │
└──────────────────────────────────────────────────────────────────┘
   ↑ 青色 = 已唱部分（逐字推进）  浅色 = 未唱部分
```

---

## 下载与安装

1. 打开 [最新 Releases](https://github.com/DoingStone/DesktopMusic/releases/latest)。
2. 下载 `TaskbarLyrics-win-x64.zip`，解压到任意目录（免安装）。
3. 装上 [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0)（多数 Win11 机器已有）。
4. 双击 `TaskbarLyrics.exe`：显示歌词并自动打开设置窗口。

> 压缩包**不含字体文件**：内置的 MiSans 有独立授权，不由本项目再分发（详见[设置界面](#设置界面)里的字体说明）。
> 目录里约 28 MB，ZIP 约 7 MB。需要在没装 .NET 的机器上跑，用 `publish.ps1 -SelfContained` 自己出一个独立版。

---

## 快速开始

从源码一键构建并启动：

```powershell
powershell -ExecutionPolicy Bypass -File scripts\run-app.ps1
```

启动后：

| 操作 | 方式 |
| --- | --- |
| **打开设置界面** | 双击 exe 即会自动打开；之后可用托盘右键 → **设置…**，或 `TaskbarLyrics.exe --settings` |
| 显示 / 隐藏歌词 | **双击**托盘图标（单击不响应，避免误触） |
| 移动位置 | 设置 → 取消「锁定位置」→ 直接拖动（横竖都跟手）→ 再锁定；或用设置里的方向微调按钮 |
| 脱离任务栏 | 取消「锁定位置」后把歌词条往任务栏外拖，松手即成悬浮小条；点「吸附回任务栏」或拖回任务栏上松手即入坞 |
| 歌词偏移校准 | 设置 → 位置与显示 → 歌词同步微调 |
| **退出程序** | 设置窗口右下角 **退出程序**；或托盘右键 → **退出** |

启动参数：

| 参数 | 作用 |
| --- | --- |
| （无） | 显示歌词**并打开设置窗口**（双击 exe 的默认行为） |
| `--settings` | 强制打开设置窗口 |
| `--tray` / `--silent` | 只显示歌词、不打开任何窗口（适合开机自启） |
| `--selftest` | 运行设置读写自检并退出（返回码 0 表示通过，结果写入 `%TEMP%\taskbar-lyrics-selftest.txt`） |

关闭 QQ音乐、暂停播放或当前歌曲没有歌词时，歌词条会按设置自动隐藏 / 冻结。

---

## 设置界面

双击 exe 自动打开，或托盘右键 → **设置…**。三个分页，**所有修改即时生效**（任务栏歌词实时变化）。

右下角三个按钮：

| 按钮 | 行为 |
| --- | --- |
| **保存** | 保存设置，窗口保持打开，可继续调整 |
| **关闭** | 收起窗口，**歌词继续显示**；改动会被保存，不会丢失 |
| **退出程序** | 二次确认后关闭歌词并退出 |

左下角「恢复默认设置」会重置所有外观 / 位置 / 来源设置（歌词缓存不受影响）。

| 分页 | 可调项 |
| --- | --- |
| **外观** | 字体、字重（Light / Normal / SemiBold / Bold）、原文字号、译文字号、**字间距**；已唱高亮色、未唱文字色、下一行预览色、背景板色（取色器 + 24 色色板，也支持手输 `#RRGGBB` / `#AARRGGBB`）；**自动适配任务栏配色**、背景板开关与圆角；逐字高亮、翻译行、下一行预览、**换行淡入**开关 |
| **位置与显示** | **宽度、高度（0 = 自动）**、**垂直对齐、水平偏移、垂直偏移**、**整体不透明度 15%~100%**；四方向微调 + 恢复默认位置 + 吸附回任务栏；锁定位置（点击穿透）、脱离任务栏（自由位置）、拖动时吸附对齐、是否显示、暂停时是否显示、无歌词时是否隐藏；歌词偏移（−5000…+5000 毫秒） |
| **歌词来源** | QQ音乐 / 网易云音乐 / LRCLIB 启用开关；「重新匹配当前歌曲」「清空歌词缓存」；「查看当前歌词匹配详情」诊断弹窗 |
| **播放控制** | 上一首 / 暂停播放 / 下一首 三个按钮、**鼠标移上去才显形**；四个**全局快捷键**（可自定义，留空即关闭） |

颜色会规范化为 `#AARRGGBB` 后写入配置。

字体下拉框第一项是 **MiSans（内置·汽水同款）**——它读取 `src\TaskbarLyrics.App\Fonts\MiSans-subset.ttf`，选中即用，不需要额外装字体；解析不到时会自动退回系统字体，不会渲染成怪字形。**仓库不附带任何字体文件**（字体有独立授权，不由本项目再分发，生成方法与出处见 `src\TaskbarLyrics.App\Fonts\README.md`）：把自备的 TTF 放到该路径后重新构建即可启用，文件缺席时构建照常，只是这一项退化为系统字体。

> 调字体和颜色时请直接看任务栏——那才是最真实的预览。

---

## 拖动、悬浮与吸附

在「位置与显示」里**取消勾选「锁定位置」**，就能直接用鼠标按住歌词框拖动，横竖两个方向都跟手。

- **脱离任务栏**：往任务栏带子外拖，松手即变成**悬浮小条**，可以放到屏幕任意位置；拖出屏幕会被自动夹住，不会丢。
- **吸附对齐**：拖动时默认按 **12 DIP 阈值**吸附显示器左 / 右 / 上 / 下边缘（留 8 DIP 边距）、水平与垂直居中、以及任务栏那一行，松手前就能看到对齐结果；不想要可以在设置里关掉。
- **重新入坞**：拖回任务栏上松手，或点「位置与显示 → 吸附回任务栏」，或托盘右键 → 「恢复默认位置」。
- **记忆**：松手后位置自动保存（按 DPI 正确换算）；重新勾选「锁定位置」恢复点击穿透，不会挡住任务栏操作。

---

## 播放控制与全局快捷键

歌词框在**锁定状态**下是**点击穿透**的——这是刻意的，否则它会吞掉任务栏上的点击；但鼠标停在上面时仍会显形播放按键（并给它们让出位置），这三个按键在锁定状态下也依然可点。

因此播放控制有两条途径：

1. **设置 → 播放控制**：三个按钮直接控制当前播放器。
2. **全局快捷键**（可在设置里改）：

| 功能 | 默认快捷键 |
| --- | --- |
| 暂停 / 播放 | `Ctrl+Alt+Space` |
| 下一首 | `Ctrl+Alt+Right` |
| 上一首 | `Ctrl+Alt+Left` |
| 显示 / 隐藏歌词 | `Ctrl+Alt+L` |

命令通过系统 SMTC 发送给「正在为歌词提供播放信息」的那个播放器。若该播放器未开放相应控件（部分播放器不暴露「下一首」），界面会提示不支持而不是静默失败。格式为 `修饰键+按键`，如 `Ctrl+Alt+Space`、`Ctrl+Shift+Right`、`Ctrl+Alt+P`。

---

## 环境要求

- Windows 10 / 11 x64
- **运行已发布的 exe**：需 [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0)（目标机器没有就用 `-SelfContained` 发布免运行时版）
- **从源码构建**：需 .NET 8 SDK
- QQ音乐（其他播放器只要向系统上报媒体信息也能用）

无需安装 WebView2，无外部运行库。

---

## 从源码构建与发布

```powershell
# 构建（解决方案里含 Core / App / Cli / IconGen 四个项目）
dotnet build TaskbarLyrics.sln -c Release

# 构建并启动
powershell -ExecutionPolicy Bypass -File scripts\run-app.ps1

# 发布为可分发文件夹 + ZIP（文件夹约 28 MB，ZIP 约 7 MB）
powershell -ExecutionPolicy Bypass -File scripts\publish.ps1

# 免 .NET 运行时的独立版（体积大得多）
powershell -ExecutionPolicy Bypass -File scripts\publish.ps1 -SelfContained

# 需要把内置字体一起打进 ZIP（仅供私有分发）时
powershell -ExecutionPolicy Bypass -File scripts\publish.ps1 -IncludeFonts
```

产物在 `publish\TaskbarLyrics\`，主程序 `TaskbarLyrics.exe`；同目录的 `TaskbarLyrics.dll`、`Microsoft.Windows.SDK.NET.dll` 等必须与 exe 放在一起。

发布出来的 **ZIP 默认不包含字体文件**（`Fonts\*.ttf` 带有独立授权，不由本项目再分发；本地 `publish\TaskbarLyrics\` 目录仍保留字体，方便本机直接运行）。

---

## 工程结构

```
src/
  TaskbarLyrics.Core/          歌词与播放状态引擎（无 UI 依赖）
    Media/
      SmtcMediaSessionSource.cs  Windows SMTC 读取播放状态
    Lyrics/
      LrcParser.cs               LRC / QRC 解析、翻译合并、元数据识别
      LyricMatcher.cs            候选歌词打分（标题/歌手/时长/版本冲突）
      LyricResolver.cs           多源并发检索与择优、缓存
      Providers/
        QqMusicProvider.cs       QQ音乐（搜索 + 歌词/翻译）
        NetEaseProvider.cs       网易云音乐
        LrclibProvider.cs        LRCLIB（开放数据库）
    Models/                      LyricDocument / LyricLine / PlaybackSnapshot
  TaskbarLyrics.App/            WPF 界面
    Views/OverlayWindow          任务栏悬浮窗
    Views/SettingsWindow         设置界面（三个分页，改动即时生效）
    Configuration/               AppSettings + 设置自检（SettingsSelfTest）
    Controls/KaraokeLine.cs      逐字高亮自绘控件
    Controls/ColorField.xaml     取色器（色板 + 十六进制输入）
    Platform/TaskbarLocator.cs   任务栏几何 + DPI 换算
    Interop/                     Win32 互操作（托盘、样式、透明模式）
  TaskbarLyrics.Cli/            命令行诊断工具（tblc，不随发布包分发）
scripts/                        启动与发布脚本（run-app.ps1 / publish.ps1）
tools/                          验收与排查脚本（见「验证状态」）
docs/                           效果图与开发记录
```

---

## 技术要点

### 1. 播放状态：Windows SMTC

不注入、不 Hook QQ音乐，直接读取系统媒体会话（`GlobalSystemMediaTransportControlsSession`），也就是音量弹窗里那个「正在播放」信息：

| 字段 | 实测值 |
| --- | --- |
| SourceAppUserModelId | `QQMusic.exe` |
| Title / Artist / Album | 完整可用 |
| Position / EndTime | 可用，**每秒更新约 1 次** |

由于 SMTC 更新频率只有约 1 Hz，渲染层用 `PlaybackSnapshot.ExtrapolatedPosition()` 按本机时钟外推播放进度，因此歌词滚动是平滑的而不是每秒跳一格。

### 2. 歌词来源：为什么必须用 `musicu` 接口

这是本项目最关键的一个发现：

| 接口 | 歌词 | 翻译 |
| --- | --- | --- |
| `c.y.qq.com/lyric/fcgi-bin/fcg_query_lyric_new.fcg` | ✅ | ❌ **恒为空** |
| `u.y.qq.com/cgi-bin/musicu.fcg` → `GetPlayLyricInfo` | ✅ | ✅ **无需登录即可取得** |

后者返回 base64 编码的 `lyric` 与 `trans`，解码后即是带时间轴的 LRC。实测《Shape of You》可拿到 92 行原文 + 137 行翻译；未翻译的行以 `//` 占位，解析时会被丢弃。

三个来源并发检索，按「标题 55 / 歌手 30 / 时长 15」加权打分择优；`Live`、`Remix`、`伴奏` 等版本差异会额外扣分，避免播录音室版却匹配到 Live 版歌词。

### 3. 逐字高亮

`KaraokeLine` 是自绘控件（`OnRender` + `FormattedText`）：同一行文字绘制两遍，第二遍用「已唱宽度」的矩形裁剪。因为两遍使用完全相同的排版参数，字形宽度一致，高亮边界会精确落在字符上。

- 有 QRC 逐字时间轴时 → 按真实音节边界推进
- 只有行级 LRC 时 → 按行内进度比例推进（中文歌词视觉上等价于逐字）
- 扫描**行进中**时，高亮前沿带一小段透明度渐变，看起来是「刷」过去而不是硬边；**唱满整行后渐变关闭**，并把裁剪矩形放宽 2 DIP（字形墨迹会超出其排版宽度），保证整行不留未唱残边——这正是 CI 里 `fully swept: highlight covers the whole line` 那条自检所守的不变量。

### 4. 两个必须处理的 Windows 坑

**(a) DPI 坐标换算**

Win32 返回的是**物理像素**，而 WPF 的 `Window.Left/Top/Width/Height` 是**设备无关像素（DIP）**。本项目进程为 `SYSTEM_AWARE`，在 125% 缩放的显示器上，若直接把物理像素赋给 WPF，窗口会偏移并拉伸（实测偏了 324 px）。因此所有几何量都要除以 `DpiScale`（取自 `VisualTreeHelper.GetDpi(window)`，与 WPF 自己的换算保持一致）。

排查脚本也必须在截图前声明 DPI 感知（`SetProcessDpiAwarenessContext(PER_MONITOR_AWARE_V2)`），否则截到的是被虚拟化的坐标区域——**这正是本次开发中最耗时的一个陷阱**：截图看起来「窗口没渲染」，实际是截图取错了区域（虚拟化后 2048×1152，真实为 2560×1440）。`tools/capture-dpi-aware.ps1` 已处理这一点。

**(b) 透明合成模式**

默认使用 WPF 逐像素透明（`AllowsTransparency`，画质最好：文字抗锯齿、背景半透明）。个别环境下这种分层窗口可能只栅格化却不合成到屏幕，表现为「日志一切正常但屏幕上看不见」。

`TransparencyMode.VerifyPerPixelAlpha()` 会在启动时做一次**实证检测**：临时隐藏内容树、把窗口涂成洋红哨兵色（隐藏内容是为了避免半透明背景掩盖检测色），等合成器稳定后**直接读屏幕上那一个像素**，颜色对不上就自动退回「不透明窗口 + `LWA_COLORKEY` + `SetWindowRgn` 圆角」方案。

> 调试提示：该检测本身也曾因用 `Window.Left/Top`（DIP）而非窗口真实设备像素矩形计算采样点而误报失败，导致自动降级到色键模式。修复后正常保留逐像素透明。

两种模式都可用环境变量强制指定：

```powershell
$env:TBL_COMPOSITE = 'alpha'     # 强制逐像素透明
$env:TBL_COMPOSITE = 'colorkey'  # 强制色键方案
```

### 5. 渲染循环

歌词用 `DispatcherTimer` 驱动重绘，间隔 8 ms、并设 12 ms 的下限护栏，实测稳定在 56–63 fps（`DispatcherTimer` 的实际间隔会被系统量化，所以不能只写 16 ms 了事）。逐像素透明模式会强制软件渲染，CPU 占用明显高于色键模式，介意时可用上面的 `TBL_COMPOSITE=colorkey`。

---

## 配置文件

配置文件：`%APPDATA%\TaskbarLyrics\settings.json`（首次修改后生成；托盘菜单可打开）。

常用项：

| 键 | 说明 | 默认 |
| --- | --- | --- |
| `Width` | 悬浮窗宽度（DIP） | `460` |
| `Height` | 悬浮窗高度（0 = 自动） | `0` |
| `OffsetX` / `OffsetY` | 相对默认锚点的偏移，拖动即改 | `0` |
| `FontFamily` | 字体（选「MiSans（内置·汽水同款）」时读 `Fonts\MiSans-subset.ttf`，该文件需自备） | `Microsoft YaHei UI` |
| `OriginalFontSize` / `TranslationFontSize` | 原文 / 译文字号 | `11` / `10` |
| `HighlightColor` | 已唱高亮色（`AutoAdaptColors` 关闭时生效） | `#FF3ABEFF` |
| `BaseColor` | 未唱文字色（同上） | `#FFE8E8E8` |
| `ContextColor` | 下一行颜色（同上） | `#8CFFFFFF` |
| `AutoAdaptColors` | 按任务栏明暗自动决定字色，无需背景板 | `true` |
| `BackgroundColor` | 背景板颜色（含透明度） | `#B3121212` |
| `ShowBackground` | 显示背景板 | `false` |
| `ShowTranslation` | 显示翻译行 | `true` |
| `ShowContextLines` | 显示下一行预览 | `true` |
| `LineTransition` | 换行时淡入 | `true` |
| `EnableWordHighlight` | 逐字高亮 | `true` |
| `HoverRevealControls` | 鼠标移上去才显示控制按钮 | `true` |
| `GlobalOffsetMs` | 全局歌词偏移（毫秒，正数延后） | `0` |
| `Locked` | 锁定位置（点击穿透） | `true` |
| `FreePosition` | 使用自由位置（脱离任务栏）而不是相对锚点的偏移 | `false` |
| `FreeX` / `FreeY` | 自由位置坐标（DIP，仅 `FreePosition` 为真时生效） | `0` |
| `SnapToEdges` | 拖动时吸附屏幕边缘与居中线 | `true` |
| `ShowWhenPaused` / `HideWhenNoLyrics` | 暂停时显示 / 无歌词时隐藏 | `true` / `true` |
| `EnableQqMusic` / `EnableNetEase` / `EnableLrclib` | 启用歌词源 | 全开 |

> 注：`BackgroundColor` 同时是背景板填充色与 `colorkey` 模式下的面板色，修改后重新匹配 / 重启生效。
>
> 注：`AutoAdaptColors` 打开时（默认），歌词字色由任务栏取样决定——浅色任务栏配深色字、深色任务栏配浅色字，此时上面三个颜色不参与绘制；关掉它才回到手工配色。默认不画背景板，正是因为自适应字色已经保证了可读性。

调试用环境变量：

| 变量 | 作用 |
| --- | --- |
| `TBL_SETTINGS` | 把设置文件重定向到指定路径（跑隔离测试时用，不动你的真实配置） |
| `TBL_DIAG=1` | 把渲染 / 悬停诊断写入 `%TEMP%\taskbar-lyrics-diag.log` |
| `TBL_COMPOSITE` | `alpha` / `colorkey`，强制透明合成模式 |

---

## 命令行诊断

`TaskbarLyrics.Cli` 输出为 `tblc.exe`，不依赖 UI，用来定位问题：

```powershell
$exe = "src\TaskbarLyrics.Cli\bin\Release\net8.0-windows10.0.19041.0\tblc.exe"

& $exe selftest   # 101 项解析 / 匹配 / 外推单元校验
& $exe now        # 打印当前曲目、各歌词源命中情况、当前歌词窗口
& $exe watch      # 持续滚动当前歌词（类似任务栏效果）
```

设置读写校验在 App 内（走真实的产品代码路径；WinExe 无控制台，结果同时写入 `%TEMP%\taskbar-lyrics-selftest.txt`）：

```powershell
& "src\TaskbarLyrics.App\bin\Release\net8.0-windows10.0.19041.0\TaskbarLyrics.exe" --selftest
echo $LASTEXITCODE   # 0 = 全部通过
```

完整验收（构建产物 + 单元校验 + SMTC 实读 + 歌词解析 + 叠加窗真机像素 + 设置持久化）：

```powershell
powershell -ExecutionPolicy Bypass -File tools\acceptance.ps1
```

`now` 的输出示例：

```
=== Track (SMTC) ===
  source   : QQMusic.exe
  title    : 河流 (Live)      artist : 川川南
  duration : 00:05:20.61      position : 00:04:12.01

=== Provider traces (830 ms) ===
  网易云音乐   candidates=10  best= 100.0  有结果  「河流 (Live)」 - 川川南
  QQ音乐      candidates=10  best=  99.7  有结果  「河流 (Live)」 - 川川南
  LRCLIB     candidates=0   best=   0.0  无结果

=== Resolved === source=NetEase score=100.0 lines=46
```

---

## 验证状态

### 持续集成（GitHub Actions）

每次提交都会在 `windows-latest` 上跑 [`.github/workflows/ci.yml`](.github/workflows/ci.yml)：还原 → `dotnet build -c Release` → 引擎自检 `tblc selftest`（101 项）→ 设置自检 `TaskbarLyrics.exe --selftest`（29 项，含色板离屏渲染）→ `dotnet publish` 验证打包 → 上传构建产物。徽章见页首，最近一次：[run 35849300995](https://github.com/DoingStone/DesktopMusic/actions/runs/35849300995) 全部步骤成功。

### 本机验收

在 Windows 11 (26200) / 2560×1440 @125% / .NET 8 上实测，`tools\acceptance.ps1` **19/19 全部通过**：

| 项目 | 结果 |
| --- | --- |
| 构建产物 | app / cli 均生成 |
| 引擎单元校验 | 101 passed, 0 failed（歌词解析、版本匹配、URI 构建、位置外推） |
| 设置读写校验 | 29 passed, 0 failed |
| SMTC 实读 | `QQMusic.exe` 曲目 / 歌手 / 时长 / 进度全部可用 |
| 歌词解析 | 匹配分 95.0，QQ音乐源，54 行 |
| 叠加窗真机像素 | 575×58 物理像素（460.0×46.4 DIP），300+ 种颜色 |
| 自适应配色 | 浅色任务栏下自动转为深色字（`AutoAdaptColors`） |

针对交互的脚本（都在真机上跑，**需要应用处于运行状态**）：

| 脚本 | 覆盖 | 结果 |
| --- | --- | --- |
| `tools\verify-free-drag.ps1` | 锁定态悬停显形 / 移开收起；解锁态拖出任务栏、边缘吸附、拖回入坞 | 12/12 |
| `tools\verify-nav-rail-center.ps1` | 设置窗左侧栏收起 / 展开时折叠按钮的居中与右边距（UI Automation 读控件矩形） | 6/6 |
| `tools\verify-cluster-modes.ps1` | 三种控件显形策略下的让位几何（DIP 不变量） | 全 PASS |
| `tools\verify-p0-look.ps1 -HoverTest` | 静止时左侧区域零墨迹、悬停才显形、透视率与渲染异常计数 | 8/8 |
| `tools\verify-hover-shift.ps1` | 悬停时歌词让位、移开后动画回中、两侧墨迹宽度一致（不裁字） | 通过，见下注 |
| `tools\verify-toggle-disc.ps1` | 展开态折叠按钮的悬停圆盘（弦长分布证明是圆不是方） | 6/6，需设置窗在前台 |

> 已知的工具限制（不影响产品行为）：`verify-hover-shift.ps1` 的墨迹掩码用「与底色亮度差 > 40」判定文字，当未唱字色（`#FFE8E8E8`）贴近浅色任务栏底色时，它只统计到**已唱**那一截，于是「静止居中」一项会随当前歌词行与扫描位置漂移（同一构建换一行歌词即由 2 项 FAIL 变为全 PASS：墨迹 `left=99 px` = 79.2 DIP，正好等于 460 DIP 条带里居中所需的 80 DIP）。`verify-toggle-disc.ps1` 需要设置窗在前台，否则 Mica 会把整窗压成同一色调而找不到窗格边缘——这两条都是脚本脆弱点，不是渲染缺陷。

> 说明：`abs` 值的差异来自 125% 缩放——Win32 报 2560×1440 物理像素，未声明 DPI 感知的工具会看到 2048×1152 的虚拟化坐标。

---

## 故障排查

| 现象 | 处理 |
| --- | --- |
| 任务栏看不到歌词 | 托盘右键 → 确认「显示任务栏歌词」为勾选；再试「重新匹配当前歌曲歌词」 |
| 歌词不同步 | 托盘 → 调整歌词偏移；或在设置里改 `GlobalOffsetMs` |
| 匹配到错误版本（如 Live） | 设置里调整偏移无效时，用 `tblc now` 看各源命中；必要时清缓存后重试 |
| 界面完全不见但进程在跑 | `scripts\run-app.ps1 -Diag`，查看 `%TEMP%\taskbar-lyrics-diag.log`；强制 `-Composite colorkey` |
| 任务栏是浅色导致文字发灰 | 调大 `BackgroundColor` 的 alpha（如 `#CC121212`） |
| 播放器不支持 | 该播放器需向系统上报媒体信息；用 `tblc now` 看 `source` 是否出现 |
| 拖不动歌词条 | 设置 → 位置与显示 → 取消「锁定位置」（锁定状态是刻意的点击穿透） |

`tools/` 下的脚本用于复现显示问题：

```powershell
# DPI 感知截图 + 叠加窗口像素统计
powershell -ExecutionPolicy Bypass -File tools\capture-dpi-aware.ps1
# 叠加窗口 z-order 与真实屏幕像素
powershell -ExecutionPolicy Bypass -File tools\zorder-diag.ps1 -Mode alpha
# 两种透明模式对比
powershell -ExecutionPolicy Bypass -File tools\test-modes.ps1 -Mode colorkey
# 歌词接口探针（原文 / 翻译 / 逐字）
powershell -ExecutionPolicy Bypass -File tools\qq-musicu-lyric.ps1
```

---

## 已知限制

- **QRC 逐字时间轴**：QQ音乐的 `format=qrc` 返回加密数据，本项目未做解密，因此逐字高亮目前是按行内比例推进的（视觉上已接近逐字，但并非真实音节时间）。
- **翻译覆盖率**：取决于歌曲本身；中文歌多数无翻译，欧美歌曲覆盖较好。
- **多显示器**：当前只在**主显示器**任务栏显示；悬浮条拖到别的显示器上会停在主显示器范围内，换显示器只做位置夹取，不做重新落位。
- **吸附没有引导线**：拖动时按 12 DIP 阈值吸附边缘与居中线，但不会像专业截图工具那样画出对齐参考线（汽水音乐同样没有）。
- **本地歌词**：暂不支持读取本地 `.lrc` / `.qrc` 文件。
- **逐像素透明的代价**：为画质默认启用逐像素透明，代价是软件渲染（CPU 高于色键模式）；介意可用 `TBL_COMPOSITE=colorkey`。

---

## 更新日志

### 未发布

- 修复：整行唱满后高亮尾巴仍在淡出、且裁剪停在排版宽度导致最后一列留残边——`KaraokeLine` 现在只在扫描行进中淡出，唱满后关闭渐变并放宽 2 DIP；设置自检的高亮判定也改为「任何蓝色染色」（原来要求饱和蓝，把最后一列抗锯齿淡边误判为未唱）。CI 由此从红转绿（`4660131`）。

### v1.1.0

**悬浮条：自由拖动、吸附与收起行为**

- 新增**脱离任务栏**：解锁位置后把歌词条拖出任务栏那条带子，松手即成悬浮小条，可放到屏幕任意位置；拖回任务栏上松手重新入坞。
- 新增**吸附对齐**：拖动时按 12 DIP 阈值吸附显示器左 / 右 / 上 / 下边缘（留 8 DIP 边距）、水平与垂直居中、以及任务栏那一行；可在设置里关掉。
- 新增设置项 `FreePosition` / `FreeX` / `FreeY` / `SnapToEdges`，设置窗「位置与显示」新增「脱离任务栏（自由位置）」「拖动时吸附对齐」开关与「吸附回任务栏」按钮；位置按 DPI 正确换算后自动保存。
- 修复：关闭「可拖动」后鼠标移开时控件不收起的毛病——悬停显形的判断不再依赖锁定状态，锁定（点击穿透）时悬停仍会显形播放按键，移开后正常收起并让歌词缓动回中。
- 修复：设置窗左侧栏**收起为图标栏后折叠按钮没有居中**（原来按展开态固定 8 DIP 右边距，实际偏右 10 DIP；现在右边距由轨道宽度推导，实测偏差 0.5 px）。
- 新增 `tools\verify-free-drag.ps1`（12/12）与 `tools\verify-nav-rail-center.ps1`（6/6，改用 UI Automation 读取控件矩形，不再依赖截图像素启发式）。
- 发布流程调整：`scripts\publish.ps1` 生成的 ZIP 默认剔除字体文件（字体授权不允许再分发），需要时用 `-IncludeFonts`。

### v1.0.0

首个公开版本：任务栏歌词（双行、逐字高亮、翻译行、下一行预览）、多源歌词检索（QQ音乐 / 网易云 / LRCLIB，加权打分择优）、SMTC 播放状态读取与进度外推、悬停显形播放控制与三处信息（歌名 / 歌手 / 封面）、自适应任务栏配色、DPI 感知与逐像素透明、Win11 风格设置界面（三页、即时生效）。

---

## 致谢与许可

思路参考：

- [ANYNC/TaskbarLyrics](https://github.com/ANYNC/TaskbarLyrics) — MIT，任务栏歌词与 Windows 合成桥接方案
- [WXRIW/Lyricify-Lyrics-Helper](https://github.com/WXRIW/Lyricify-Lyrics-Helper) — 多平台歌词检索与解密
- [LRCLIB](https://lrclib.net) — 开放歌词数据库

本项目为独立实现，代码原创。歌词内容版权归各音乐平台与版权方所有，本工具仅用于本地播放时的辅助显示。内置字体 MiSans 的授权归其权利人所有，本仓库不附带字体文件。

## 许可证

[MIT](LICENSE) © 2026 DoingStone
