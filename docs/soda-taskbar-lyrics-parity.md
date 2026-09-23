# 与「汽水音乐」任务栏歌词的观感对齐评估

> 评估对象：本仓库（TaskbarLyrics · QQ音乐任务栏歌词，.NET 8 + WPF）
> 对标实物：本机安装的 **汽水音乐 3.7.0**
> 方法：不靠截图猜测，直接解包汽水音乐随包发布的 Electron 资源，从**未压缩的原始源码**（source map 内嵌 `sourcesContent`）读出它的真实实现规格，再逐条对照本仓库代码。
> 本文只做评估与方案，不代表已实施。

---

## 0. 结论

**可以改造成同等观感，且不需要动架构。** 判断依据：

1. **架构同构**。汽水音乐的任务栏歌词不是"注入任务栏"，而是一个独立置顶小窗：`taskbar_widget_helper.node`（原生插件）+ `Z_ORDER_GUARD_INTERVAL_MS = 250`（z 序守卫）+ `BLANK_POLL_INTERVAL_MS = 500`（空白区轮询）。本项目用 `TaskbarZOrderWatcher` + `TopmostCheckIntervalMs = 30` + `TaskbarLocator` 做的是同一件事，只是节奏更快。
2. **数据侧已就绪**。本项目引擎已经产出逐字时值（`LyricLine.Syllables`、`LyricDocument.HasRealWordTiming`，来自 YRC / TTML），改观感不需要新增歌词能力。
3. **差距全部集中在渲染层**，且每一项都有明确、可验证的对应改法（见 §4）。

预估收益：**不做 P0/P1 时观感差距约 70%，做完 P0（半天）能抹掉一半，做完 P0+P1（1–2 天）可达到"看不出是另一个软件"的程度（≈ 汽水 90%）。** 剩下的 10% 是字体与亚像素动效细节，属于长尾。

---

## 1. 汽水音乐的真实规格（逆向所得，非推测）

数据来源：

- `D:\Users\Qianmory\AppData\Local\Programs\Soda Music\3.7.0\resources\taskbarWidget.asar`（1188103 B，仅 4 个文件）
- 同目录 `taskbarWidgetShadow.asar`、`desktopLyrics.asar`
- `resources\fonts\MiSansVF.woff2`（11907336 B）
- `.js.map` 的 `sourcesContent` 里是**原始 Vue 3 SFC / TypeScript 源码**，可直接阅读

### 1.1 组件结构与尺寸

`src/rendererTaskbarWidget/Widget.vue` 的一行横向 flex：

```
[拖拽把手 22px][封面 28×28 r4][标题 10px + 歌词 11px（固定 128px 宽）][◀ ⏯ ▶][红心 32×32][hover 关闭 右上4px]
```

`src/services/taskbarWidget/constants.ts`：

```ts
TASKBAR_WIDGET_WIDTH_DIP = { nano: 108, normal: { 1: 248, 2: 388 } }
TASKBAR_WIDGET_MAX_HEIGHT_DIP = 40
Z_ORDER_GUARD_INTERVAL_MS = 250
BLANK_POLL_INTERVAL_MS = 500
DOCKED_VISIBILITY_TRANSITION_DURATION_MS = 140   // 手动 16ms 帧循环
DOCKED_EDGE_MARGIN_DIP = 12
```

- 容器查询（不是媒体查询）：宽度 `< 248` 隐藏主区；variant 2 在 `< 388` 时隐藏。
- 拖拽用 `-webkit-app-region: drag`（原生窗口拖拽），不是 JS 算位移。
- variant 1（默认）：hover 时歌词/标题区 **淡出**，三个播放键以 `position:absolute; inset:0` 覆盖整块；variant 2：按钮常驻。

### 1.2 视觉语言 —— "高级感"的真正来源

`Widget.vue` 的 scoped SCSS：

```scss
.container {
  padding-inline: 22px 12px;
  border-radius: 8px;
  border: 0.5px solid transparent;          // 停靠时：只有一根透明边框
  transition: border-color .1s ease-out, background-color .1s ease-out;
  .wrapper[data-hover] & {                  // hover 才浮出极淡的底
    border-color: light-dark(rgba(255,255,255,.08), rgba(255,255,255,.04));
    background-color: light-dark(rgba(255,255,255,.3), rgba(255,255,255,.04));
  }
  .wrapper[data-widget-state='free'] & {    // 只有被拖出任务栏才有卡片底
    border-color: light-dark(rgba(0,0,0,.08), rgba(255,255,255,.12));
    background-color: light-dark(rgba(255,255,255,.9), rgba(0,0,0,.7));
  }
}
```

**停靠状态下背景是完全透明的** —— 歌词直接压在任务栏上，没有任何底衬。这是最关键的一条。

明暗自适应不写两套皮肤，全靠 CSS `light-dark()` + `color-scheme`：

| 元素 | 亮色任务栏 | 暗色任务栏 |
|---|---|---|
| 未唱歌词 | `rgba(0,0,0,.5)` | `rgba(255,255,255,.5)` |
| 已唱歌词 | `rgba(0,0,0,.9)` | `rgba(255,255,255,.9)` |
| 歌名 10px/500 | `rgba(0,0,0,.5)` | `rgba(255,255,255,.5)` |
| 按钮 | `rgba(0,0,0,.7)` | `rgba(255,255,255,.7)` |
| 播放键底 | `rgba(0,0,0,.07)`，hover `.12` | `rgba(255,255,255,.07)`，hover `.12` |

字体：`MiSans`（`resources/fonts/MiSansVF.woff2`，可变字重），歌词 `font-size:11px; font-weight:600; line-height:15px; height:1lh`。
过渡统一 `.1s ease-out`；红心 `#e0214f` 配 `heart-in .3s cubic-bezier(.65,0,.35,1)`（scale .5→1）。

### 1.3 歌词引擎（`src/rendererTaskbarWidget/useWidgetLyric.ts`）

- 解析 **KRC** `[start,dur]<wStart,wDur>字…`（逐字）与 **LRC** `[mm:ss.xxx]`（整行）。LRC 无逐字时 `playedWidth = 整行宽` —— 立即整行点亮，不假装进度。
- 逐字宽度取自 canvas `measureText`，**按 (font, char) 两级 Map 永久缓存**（`renderer/utils/text.ts::getCharWidth`）：

  ```ts
  const matchedCache = textWidthCache.get(font)?.get(char)
  if (matchedCache) return matchedCache
  ```

- 已唱宽度是**亚像素连续**量，正在唱的那个字按比例插值：

  ```ts
  const inCharProgress = (progressMs - charStartTime) / char.duration
  result += charWidth * inCharProgress
  ```

- 驱动时钟（`src/renderer/compositions/progress.ts`）：

  ```ts
  useRafFn(({ delta }) => {
    if (isPlaying && !isLoading && progressSeconds !== 0) progressSeconds += delta / 1000
  })
  ```

  即**每一动画帧推进一次**（60fps），只在播放且非加载时。

### 1.4 渲染方式（`WidgetLyric.vue`）

两层同一份文字，上层按**像素宽度**裁剪：

```html
<div class="lyric lyric--base">{{ currentLineText }}</div>
<div class="lyric lyric--played" :style="{ width: `${playedWidth}px` }">{{ currentLineText }}</div>
```

```scss
.lyric--base   { color: light-dark(rgba(0,0,0,.5), rgba(255,255,255,.5)); }
.lyric--played { position:absolute; inset:0 auto 0 0; overflow:hidden;
                 color: light-dark(rgba(0,0,0,.9), rgba(255,255,255,.9)); }
```

思路与本项目 `KaraokeLine` 的"画两遍 + 裁剪"完全一致，差别在**裁剪宽度怎么算**（逐字缓存 vs 每帧重测）和**多久更新一次**（60fps vs 12.5fps）。

**长行滚动是"高亮驱动"，不是匀速跑马灯**：

```ts
const LYRIC_SCROLL_TRIGGER_RATIO = 0.9   // 已唱边缘越过可视宽 90% 才滚
const LYRIC_SCROLL_ANCHOR_RATIO  = 0.1   // 滚到"已唱边缘落在可视宽 10%"处
const LYRIC_SCROLL_LERP_SPEED    = 5
...
const lerpRatio = 1 - Math.exp(-LYRIC_SCROLL_LERP_SPEED * dt)  // 帧率无关的指数跟随
renderedScrollLeft += diff * lerpRatio                          // 差 <0.5px 直接吸附
```

换行时 `scrollLeft` 归零；行切换用 `.paragraph { transition: transform .1s linear }`。

### 1.5 桌面歌词（`desktopLyrics.asar`）—— 另一套"美"的样板

单节点 + 渐变裁剪，只画一遍：

```js
backgroundImage: `linear-gradient(to right, ${blurColor} 50%, ${focusColor} 50%)`
backgroundPositionX: `${progressWidth}px`
filter: colorPreset?.filter
```

```scss
.line { color: transparent; background-size: 400%; background-position: 0 0;
        background-clip: text; -webkit-background-clip: text; white-space: nowrap; }
```

配色（`services/desktopLyrics/type.ts`）：

| 预设 | 已唱 focus | 未唱 blur | filter |
|---|---|---|---|
| 百搭灰 | `#99FFAA` | `#B3B3B3` | — |
| 明亮白 | `#99FFAA` | `white` | `drop-shadow(0px 1px 2px rgba(0,0,0,.6))` |
| 深沉黑 | `#2CC513` | `rgba(0,0,0,.8)` | — |

超长行：`offset = startOffset - (totalWidth - width) * 行进度`，随该行进度线性横移。

---

## 2. 本项目现状（工作区版本，含未提交改动）

| 维度 | 汽水音乐 | 本项目 | 位置 |
|---|---|---|---|
| 背景 | 停靠时**全透明** | 默认 `#B3121212` 暗板（70% 黑） | `AppSettings.BackgroundColor` |
| 文字色 | 由 `light-dark()` 跟随任务栏 | 固定浅色 `#FFE8E8E8` + 高亮 `#FF3ABEFF` | `AppSettings.BaseColor/HighlightColor` |
| 更新节拍 | 60fps（rAF 逐帧推进） | **12.5fps**（`RenderInterval = 80ms`） | `src/TaskbarLyrics.App/App.xaml.cs:19`，定时器 `:138-143` |
| 逐字宽度 | canvas 逐字测量 + 永久缓存 | 每帧每字 `new FormattedText` | `KaraokeLine.cs:572`、`:613`、`:621` |
| 每帧对象分配 | 无（缓存命中即返回） | 每帧 6–10 个 `FormattedText` + 一行日志字符串 | `KaraokeLine.cs:403`（无条件下求值）、`:418`、`:421`、`:655`、`:668` |
| 长行 | 高亮驱动 + 指数跟随，90%/10% 锚点 | 匀速跑马灯 30 DIP/s、30fps | `KaraokeLine.cs:106`、`ScrollSpeed = 30` |
| 行切换 | 100ms 线性过渡 | 直接赋值，无过渡 | `OverlayWindow.xaml.cs` `RenderCore` |
| 字体 | MiSans VF，11px/600/15px | Microsoft YaHei UI，12.5px + 译文 11px | `AppSettings.FontFamily` |
| 排版 | 一行歌词 + hover 才出现的控制键，248 DIP | 常驻 3 按钮 + 进度条 + 时间 + 两行歌词，460 DIP | `OverlayWindow.xaml` 列0/列1 |
| 高度 | 上限 40 DIP | `taskbar.HeightDip - 2` | `OverlayWindow.xaml.cs` `PlaceOverWindow()` |
| z 序守卫 | 250ms | `TopmostCheckIntervalMs = 30` | `OverlayWindow.xaml.cs:43` |

**一个容易被忽略但影响很大的事实：本机任务栏是亮色的。**
`HKCU\...\Themes\Personalize` 实测 `SystemUsesLightTheme = 1`、`AppsUseLightTheme = 1`；任务栏像素采样 `#D1DFD9 / #D4DFDC`，中位亮度 219。

而 `docs/overlay.png` 的像素统计是：92% 为暗色（≈`rgb(64,64,64)`，即 `#121212` 板压在任务栏上）、3% 为青色（`#3ABEFF`）、其余为浅色文字 —— 一块**深色贴纸贴在亮色任务栏上**。汽水在同样环境下是深灰字直接压在任务栏表面。这单一项就贡献了观感差距的一半。

**已经做对的部分**（工作区未提交改动）：`MeasureSungWidthByChar` 已把"按总宽均匀推进"改成"按真实字形边界逐字累加"，方向与汽水的逐字宽度一致 —— 缺的只是缓存。

---

## 3. 可以照搬 / 不能照搬

**能等价实现**（下面 §4 给出各自做法）：透明底、明暗自适应、逐字宽度缓存、60fps 帧推进、高亮驱动滚动、行切换过渡、渐变擦除、hover 显隐控件、宽度折叠阈值。

**不能照搬**（Chromium 专有，WPF 无对应物）：`light-dark()`、容器查询 `@container`、`background-clip:text`、`backdrop-filter`、`-webkit-app-region: drag`。

**物理限制**：WPF `AllowsTransparency=True` 的分层窗口内容不走 GPU 快路径，且 **ClearType 失效**（改用灰度抗锯齿），小字号锐度天然不如 Chromium 合成器。因此 60fps 必须靠"少重绘 + 缓存绘制内容"实现，而不是指望硬件加速；现有 `TransparencyMode` 的 colorkey 降级路径反而可能更快，值得在 60fps 下重新对比。

**数据侧上限**：汽水自带 KRC 逐字歌词。本项目 QQ 音乐走 YRC、AMLL 走 TTML 都有逐字；但网易云 / LRCLIB 只有整行时间轴，那些源上做多好都只能是"整行渐亮"（汽水对 LRC 也是整行点亮，属同等对待）。

---

## 4. 实现思路（按性价比排序）

### P0 · 观感对齐（约半天，收益最大）

1. **默认去掉背板**：`ShowBackground` 默认 `false`，或把 `BackgroundColor` 的 alpha 从 `B3` 降到 `14`–`1A`；把"有卡片底"留给被拖出任务栏的悬浮态（对应汽水的 `data-widget-state=free`）。
2. **文字色自适应任务栏**：复用已有的 `SampleBackdropLuminance()`（`OverlayWindow.xaml.cs:450-467` 已用它决定播放键的黑白对比），把采样亮度映射成文字色 —— 亮任务栏用 `rgba(0,0,0,.5)/.9`，暗任务栏用白 `50%/90%`；高亮色同步（可直接取汽水的 `#99FFAA`）。
3. **排版对齐**：原文 11px / `SemiBold` / 行高 15px，译文 10px；把 1px 硬投影换成柔和阴影，或按任务栏亮度决定是否加 `drop-shadow(0,1,2,rgba(0,0,0,.6))`。
4. **行切换过渡 100ms**：给 `KaraokeLine` 加位移/淡入插值（建议用 `CompositionTarget.Rendering` 自绘，避免再引入一个 Storyboard 计时器）。
5. **控件降噪**：`ShowTransportControls` 默认 `false`，或改成 hover 才显示 —— 命中判定已现成（`OverlayWindow.xaml.cs:330 IsOverInteractiveElement`、`:335 IsOverAButton`）。

### P1 · 流畅度（1–2 天，收益次大；必须先做分配优化）

6. **`KaraokeLine` 去分配三件套**：
   - `Diag.Log` 调用点短路（`KaraokeLine.cs:403` 目前无论 `TBL_DIAG` 是否为 1 都会拼字符串并调 `DescribeBrush` 两次）；
   - 按 `(text, fontSize, fontWeight, dpi, letterSpacing)` 缓存 `FormattedText`，行内容不变就不重建；
   - **逐字宽度缓存**，等价于汽水的 `getCharWidth`：`Dictionary<(font, size, char), double>`，再按行缓存宽度前缀和数组，`MeasureSungWidth` 只做累加/二分，零分配。
7. **渲染节拍 80ms → 16ms**（或直接改成 `CompositionTarget.Rendering` 帧驱动 + 内部节流 60fps）。时间轴用 `PlaybackClock` 外推，不依赖 tick 精度。
8. **性能验收**：连续 30s 采样，`RenderTick` 单帧 UI 线程耗时 P95 < 3ms、UI 线程 CPU < 2%、Gen0 GC ≈ 0 次；用 `tools\` 现有诊断脚本 + `tblc.exe` 采集。

### P2 · 滚动策略（约 1 天）

9. 把匀速跑马灯换成汽水式**高亮驱动滚动**：已唱边缘越过可视宽度 90% → 目标偏移 = `已唱宽 − 可视宽 × 10%`；帧驱动指数跟随 `ratio = 1 − exp(−5·dt)`，差 <0.5px 吸附；换行归零。跟唱感明显强于匀速跑马灯，且长行开头不会立刻开滚。

### P3 · 精致度（1–2 天，可选）

10. **渐变擦除**：把"裁剪 + 同色重绘"改为"裁剪 + 渐变刷"，或在 `KaraokeLine` 里用 `LinearGradientBrush`（同色硬停、`MappingMode=Absolute`、`StartPoint.X = sungWidth`）一次画完 —— 等价于汽水桌面歌词的 `background-clip:text` + `background-position`，还能省掉一遍绘制。
11. **字体**：MiSans VF 是 woff2，WPF 需要 ttf/otf，且全量 >10MB —— 对一行字属杀鸡用牛刀，建议**先做字形子集**，或直接评估 `HarmonyOS Sans` / 思源黑体。**采用前必须自行确认 MiSans 的授权条款**（本项目是 MIT 开源仓库，字体授权需与之兼容）。若要内置字体，需扩展 `KaraokeLine.ResolveTypeface()`（`KaraokeLine.cs:313-321`，目前只在 `Fonts.SystemFontFamilies` 里找，见 `SettingsWindow.xaml.cs:618`）。
12. **可选"完整小组件"形态**：封面 28×28 + 歌名 10px + 单行 11px 歌词 + hover 播放控制，固定 248 DIP —— 这是汽水最精致的一档，但会把项目从"歌词条"变成"任务栏音乐组件"，属产品决策而非纯视觉改造。

---

## 5. 建议验收标准

- **视觉**（同曲、同任务栏、截图对比）：背板 alpha；文字色相对任务栏亮度的对比度；单行字号/字重/行高与规格一致；hover 才出现控件。
- **流畅**：60fps 下 `RenderTick` P95 < 3ms、Gen0 GC ≈ 0；逐字高亮边缘在 1080p 录屏逐帧检查中**每帧都有位移**（现在是每 80ms 跳一次）。
- **行为**：长行在已唱边缘到达可视宽 90% 时触发跟随、停在 10% 锚点；换行后偏移归零；暂停时高亮停在原地。

---

## 6. 复现方式（逆向可重跑）

汽水音乐的 asar 里 `assets/*.js.map` 带 `sourcesContent`，解包即可拿到原始源码：

```powershell
# asar 头：offset 4 = headerSize，offset 12 = jsonLen，dataOffset = 8 + headerSize
node "$env:TEMP\soda-asar\extract.js" list    "$res\taskbarWidget.asar"
node "$env:TEMP\soda-asar\extract.js" extract "$res\taskbarWidget.asar" "$env:TEMP\soda-asar\tw"
# 再从 .js.map 的 sourcesContent 还原 src/**（扁平命名）
node "$env:TEMP\soda-asar\unmap2.js" "$env:TEMP\soda-asar\tw\assets\taskbarWidget-*.js.map" "$env:TEMP\soda-asar\flat-tw"
```

关键文件（解包后）：
`src__rendererTaskbarWidget__Widget.vue`、`useWidgetLyric.ts`、`WidgetLyric.vue`、`src__renderer__utils__text.ts`、`src__renderer__compositions__progress.ts`、`src__services__taskbarWidget__constants.ts`、`src__rendererLyrics__Lyrics.vue`、`src__services__desktopLyrics__type.ts`。

---

## 7. P0 实施记录（已落盘并通过全部验收）

### 7.1 改了哪些文件

| 文件 | 改动 |
| --- | --- |
| `src\TaskbarLyrics.App\Configuration\AppSettings.cs` | `OriginalFontSize` 12.5 → `11.0`、`TranslationFontSize` 11.0 → `10.0`；`ShowBackground` 默认改为 `false`（无初始器即 false）；**新增** `AutoAdaptColors`（:43）、`HoverRevealControls`（:188）、`LineTransition`（:195），三者默认 `true` |
| `src\TaskbarLyrics.App\Views\OverlayWindow.xaml.cs` | 每个渲染 tick 采样任务栏底色 → 派生前景色（亮任务栏 `base=#8C000000` / `hl=#E6000000`，暗任务栏白 50% / 90%）；播放控制面板默认隐藏、鼠标进入才淡入（含 `IsHitTestVisible` 与穿透判定）；换行淡入；无背板时用 1/255 alpha 的 `HitTestFloor` 兜住命中测试；指针位置改为 `GetCursorPos` 轮询驱动 |
| `src\TaskbarLyrics.App\Controls\KaraokeLine.cs` | 新增 `ShadowEnabled` 依赖属性：自适应模式下关掉那层 1px 硬投影 |
| `src\TaskbarLyrics.App\Views\OverlayWindow.xaml` | 播放控制面板独占一列（不再压在歌词行上），注释与实际布局一致 |
| `src\TaskbarLyrics.App\Views\SettingsWindow.xaml(.cs)` | 三个新开关接线：`AutoAdaptColorsCheck`、`HoverRevealControlsCheck`、`LineTransitionCheck`（Load / 写回各一处，`OnRestoreDefaults` 走 `new AppSettings{}` 自动跟随默认值） |
| `tools\verify-p0-look.ps1` | **新增**：不依赖参考帧的像素验证（抓条带 → 统计墨色/透视率/左带墨色/进度条蓝/自适应墨色 → 7 项判定） |
| `tools\acceptance.ps1` | 高亮色检查改为双模式：`AutoAdaptColors` 打开时验"自适应墨色与任务栏底色对比度足够"（最多重采样 5 次 × 3s，避免换行瞬间采不到近黑像素）；`AmllTtml` 加入真实源白名单 |
| `README.md` | 设置能力表补三个新开关；配置键表补 `AutoAdaptColors`/`ShowBackground`/`LineTransition`/`HoverRevealControls` 与字号默认值；CLI `selftest` 项数 22 → 101（原文陈旧） |

### 7.2 与 §4 P0 提议的三处偏离（及原因）

1. **P0-2 没有采用汽水的 `#99FFAA` 高亮**，改成"同色两档 alpha"的单色方案（亮底黑 55%/90%、暗底白 50%/90%）。原因：汽水在生产代码里是 `light-dark()` 两态切换，高亮与正文只在亮度上分层；浅色任务栏上绿色高亮会显著降低对比度，而任务栏以浅色为主。用户关闭 `AutoAdaptColors` 后其自定义 `HighlightColor` 立即恢复生效，能力没有丢。
2. **P0-3 的"柔和阴影"没有加**，只把原来的 1px 硬投影在自适应模式下关掉。原因：WPF 的 `DropShadowEffect` 是每帧位图特效，与 §4 P1-7 的"16ms 帧驱动"目标直接冲突；先做减法、把加法留给 P3。
3. **P0-4 的过渡用现有计时器做透明度插值**，没有引入 `CompositionTarget.Rendering`。原因：整条渲染环的帧驱动是 P1-7 的活，现在做等于做两遍。
4. **P0-5 保留了 `ShowTransportControls` 默认 `true`**，只是面板默认隐藏、hover 才显形。视觉结果与"默认关掉"一致，但功能没被砍掉。

### 7.3 验证证据（Release 构建）

- `tools\acceptance.ps1`：**19 passed, 0 failed**。其中 `self-test: 101 passed, 0 failed`、`settings self-test: 29 passed, 0 failed`、`source is a real provider (AmllTtml) score=88.2`、`overlay is actually painting (distinct=424)`、`adaptive palette paints legible ink (bar luma=219 ink=25,27,25 dominant=211,222,212 after 1 sample(s))`。
- `tools\verify-p0-look.ps1`（对发布版实测）：墨色 `1991 px (6.0%)`、透视率 **93.0%**（有背板会趋近 0）、任务栏亮度 219 / 最深墨色 25.5、左带墨色 `0 px`（控件确实默认不画）、进度条蓝 `0 px`（控件确实隐藏）、自适应墨色 `1532 px dark-on-light` → 7/7 PASS。
- 产物：`publish\TaskbarLyrics`（8 文件 / 24.8 MB）与 `publish\TaskbarLyrics-win-x64.zip`（6.3 MB），已按发布版启动。

### 7.4 与用户现有配置的交互（重要）

`%APPDATA%\TaskbarLyrics\settings.json` 里没有三个新键 → 全部按新默认 `true` 生效；其 `HighlightColor #FF999999` 会被自适应接管（关掉 `AutoAdaptColors` 即恢复）；字号 16 / 译文 11 是用户自己的选择，未被默认值改动。

### 7.5 仍未做

§4 的 P2（高亮驱动滚动）、P3（渐变擦除 / 字体 / 完整小组件形态）未动。P1（去分配三件套 + 帧驱动）已在 §8 完成。

---

## 8. P1 实施记录 + 汽水风格播放按键（已落盘并通过全部验收）

### 8.1 改了哪些文件

| 文件 | 改动 |
| --- | --- |
| `src\TaskbarLyrics.App\Controls\KaraokeLine.cs` | ① 每帧 `Diag.Log`（含两次 `DescribeBrush`）改为**按行文本变化节流**（`_diagText` 缓存）；② `FormattedText` 缓存（`EnsureTextCache`/`TextCacheMatches`，键 = 文本+译文+字体+字重+字号+字间距+dpi）；③ **逐字宽度缓存 + 前缀和数组**（`BuildCharAdvances`，静态 `CharAdvanceCache`/`RunAdvanceCache`，`RunAdvanceCacheLimit = 4096` 满则清空）；④ `MeasureSungWidthByChar` 从"每帧逐字 `new FormattedText`"变成 `prefix[fullChars] + advances[fullChars] * partial`（O(1)、零分配）；⑤ `DrawAt` 换色从"重建 `FormattedText`"改成 `text.SetForegroundBrush(...)`（省掉每帧 4–8 次字形重排） |
| `src\TaskbarLyrics.App\Diag.cs` | `private static readonly bool Enabled` → `public static bool Enabled { get; }`。调用点的插值字符串会**先求值再进 `Log`**，`Log` 内部守卫挡不住格式化开销，所以守卫必须放在调用点 |
| `src\TaskbarLyrics.App\App.xaml.cs` | `RenderInterval` 80ms → **8ms**，并加 `MinRenderIntervalMs = 12` 的护栏（见 §8.2）；`RenderTick` 拆成薄包装 + `RenderTickCore`，仅在 `Diag.Enabled` 时用 `Stopwatch` 计时；新增 `RecordFrameTime`，每 300 帧输出 `[perf] render tick n=300 fps=… mean=… p50=… p95=… max=… gen0=… gen1=…` |
| `src\TaskbarLyrics.App\Views\OverlayWindow.xaml.cs` | ① `SetProgress` 节流：时间文本只在**显示秒**变化时重建（`_shownSeconds`/`_shownDurationSeconds`），进度条宽度只在**像素差 ≥ 0.5 DIP** 时赋值（`SetFillWidth` + `_shownFillWidth`）——每帧给 `ProgressFill.Width` 赋值会让整条布局失效；② `[overlay] Render pos=` 日志按"行号 / 宽度变化"节流（`_diagIndex`/`_diagWidth`），空文档分支同理 |
| `src\TaskbarLyrics.App\Controls\TransportButton.cs` | **新增**（自绘播放按键，见 §8.6） |
| `src\TaskbarLyrics.App\Views\OverlayWindow.xaml` | 旧的字形按钮样式整块替换为四个 `StreamGeometry`（坐标取自汽水 SVG）+ `TransportGlyphButton` 样式 + 三个 `controls:TransportButton`；进度区由竖排改横排；新增左侧拖拽手柄 |

### 8.2 三个卡点（都是实测撞出来的）

**卡点一：`DispatcherTimer` 的间隔是被 Windows 量化的，16ms 拿不到 60fps。**
把 `RenderInterval` 设成 16ms 后实测只有 **37.8–39.8fps** —— 16ms 请求被量化到约 25ms 而不自知。降到 **8ms** 才拿到 59.7–61.3fps（护栏 `MinRenderIntervalMs = 12` 防止 8ms 定时器过密触发）。这是本次最大的一处"反直觉"发现：**帧率瓶颈是定时器间隔量化，不是排版也不是计时精度**。

**卡点二：`timeBeginPeriod(1)` 是错误方向（已试并回退）。**
一度新建 `Interop\TimerResolution.cs`（`winmm!timeBeginPeriod(1)`，播放时开、暂停时关）来"提高计时精度"。实测：**不加也照样 59–63fps**，且常驻 CPU 疑似被抬高 → 已删除该文件与调用点。

**卡点三：逐像素 alpha 让绘制成本翻数倍（但这是视觉代价，不是可选项）。**
`AllowsTransparency="True"`（逐像素 alpha 分层窗口）会迫使 WPF 走软件渲染路径。同一会话内 40fps 下对比：alpha **8.02%** vs colorkey **1.89%**（约 4 倍）。60fps 下两者都落进 4–9% 的噪声带（见 §8.3），单次读数不足以区分；但"暂停时成本掉到 ~0"（§8.4）已经证明：**开销的大头是重绘本身，不是 tick 循环**。

### 8.3 实测数据（本机 Win11 26200 / 2560×1440 @125%）

| 阶段 | 节拍 | fps | tick p50 | tick p95 | Gen0 / 300 帧 | CPU（单核占比） |
| --- | --- | --- | --- | --- | --- | --- |
| P1 前（旧代码） | 80ms | ≈12.5（名义） | — | — | — | **7.02%**（pid 888，20s） |
| P1 后、16ms、日志未节流 | 16ms | 37.8 | 0.064ms | 4.795ms | 0 | 未测（mean 1.063ms/帧，被每帧写盘污染） |
| P1 后、16ms、日志已节流 | 16ms | 39.8 | 0.054ms | 0.092ms | 0–1 | alpha 8.02% / colorkey 1.89%（40fps） |
| **P1 后、8ms（最终）** | ~15.6ms | **59.7 / 60.6 / 61.2 / 61.3** | 0.037–0.049ms | **0.064–0.087ms** | **0–1** | 见下 |

60fps 下 CPU（发布版 Release、无 diag、同一首歌、连续采样窗口）：

- 播放中 alpha：`5.3 | 9.99 | 6.56` 与 `12.8 | 10.46`（后者含"暂停→恢复"后的重新解析）；早期两轮干净读数为 **5.78% / 7.5%**。
- 播放中 colorkey：`4.11% / 9.01%`。
- **暂停时 alpha：`0.45 | 2.45 | 3.79 | 1.78`**（典型 <2%）。

读数波动主要来自机器负载，**结论只能给区间**：60fps 播放中约 **4–10%**（典型 6% 上下），暂停时约 **0.5–2%**。

### 8.4 机制结论

1. **分配目标达成**：600 帧只有 1–2 次 Gen0（P1 前是每帧十几次 `FormattedText` 构造 + 大字符串）。
2. **单帧 UI 线程成本降到 0.04ms 量级**（P95 0.087ms），相对 §4 P1-8 的"P95 < 3ms"有 ~35 倍余量；也就是说 **tick 本身已经不是瓶颈**，剩下的是 WPF 把这一帧画到屏幕上的固定成本。
3. **暂停时成本掉到 ~0.5–2%** 是关键证据：没有高亮边缘移动 → 没有 `InvalidateVisual` → 没有重绘。`SetProgress` 的节流把"每帧改 `Width`/改文本"这两个无谓的失效源也堵掉了。所以"少重绘"这一侧还有余量，"重绘本身更便宜"这一侧只剩切合成模式（有机表代价）。
4. **60fps 的视觉意义**：逐字高亮边缘从"每 80ms 跳一次"变成每帧都有位移，1080p 录屏逐帧检查下不再有台阶。

### 8.5 纠正 §4 里的两条估算

- **§4 P1-8 的「UI 线程 CPU < 2%」不成立**。那是估算值；实测 60fps 播放中约 6%（4–10% 区间），只有暂停态才落在 2% 以内。真实下限由 WPF 逐像素 alpha 的软件合成决定，不是本项目的代码问题。**可选的优化方向**是色键模式（`$env:TBL_COMPOSITE='colorkey'`）——但它无法混合半透明，文字抗锯齿会退化，属于"要 CPU 还是要观感"的取舍，默认仍用 alpha。
- **§3 里「colorkey 降级路径反而可能更快」得到证实**（40fps 下 1.89% vs 8.02%），但 60fps 下两条路径的差距被机器噪声淹没，且 colorkey 有观感代价，因此**不改默认值**。

### 8.6 汽水风格播放按键

对照 `ActionButton.vue` / `Widget.vue`（§1 已逆向）逐条落地：

| 汽水规格 | 本项目实现 |
| --- | --- |
| 普通键 32×32；播放键 48 宽、`border-radius:9999px` | 30×30；播放键 44×30、`PillCornerRadius=15` |
| 图标 `prev/next` 16×16、`play/pause` 20×20，`fill=currentColor` | `IconSize=16/16/18`、`IconViewBox=16/16/20`，四个 `StreamGeometry` 直接取汽水 SVG 路径 |
| 颜色 70%，hover 100%，`transition .1s ease-out` | `IconColor=ink@B3` → `IconHoverColor=ink@FF`，`ColorAnimation(100ms, QuadraticEase EaseOut)` |
| 播放键底 7%，hover 12% | `PillColor=ink@12` → `PillHoverColor=ink@1F` |
| 按钮间距 `gap:10px` | `Margin="0,0,10,0"` / `"10,0,0,0"` |
| 左侧 22px 区域里的 2×16 圆角竖条，hover 才显形、拖拽中 50% | `DragHandle`：2×16、`CornerRadius=1`、`ink@1F` → 拖拽中 `ink@80`，不占歌词宽度 |
| 无边框/无阴影 | 无边框；仅当**既无背板又采不到任务栏底色**时给图标描一圈 `HaloColor`（汽水总有任务栏垫底，不存在这种情况） |

- `TransportButton` 是自绘控件（构造函数里 `Template = new ControlTemplate(typeof(TransportButton))` 清掉主题模板），`OnRender` 顺序：透明命中矩形 → 胶囊 → 描边 → 图标；只开一个 `ScaleTransform` 做 viewBox 缩放。**没有用 `Effect` 做发光** —— `DropShadowEffect` 会把 7% 的胶囊一起发光成一块亮板，所以光晕改用描边。
- 播放/暂停图标按 `isPlaying` 切换（`SetPlayPauseIcon`），并在 `RenderCore` 顶部调用。
- **像素级验证**：抓条带做逐列墨迹剖面，四个图标的几何与按 DIP 预算的预测坐标**逐项吻合 ±1px**（prev 竖条 x20–21 / 三角 x24–36；暂停两根 5×20px 竖条 x80–84 与 x90–94，间隔 5px；next 三角 x137–151 / 竖条 x154–155）；发媒体键暂停后同一位置变成"左缘满高、向右收尖"的播放三角。

### 8.7 验收

- `& tools\acceptance.ps1`：**19 passed, 0 failed**（`self-test: 101 passed`、`settings self-test: 29 passed`、overlay rect `(326,1381)-(901,1439)` 575×58、distinct colours 301、`source is a real provider (NetEase)` 48 行、逐字时间轴可用）。
- `& tools\verify-p0-look.ps1 -HoverTest`：**8/8 PASS**（idle 墨色 1206px / 透视率 95.1% / 左带 0px；hover 左带 609px、蓝 81px、墨色 2045px；自适应色 `base=#8C000000 hl=#E6000000`）。
- 产物：`publish\TaskbarLyrics`（8 文件 / 24.8 MB）+ `publish\TaskbarLyrics-win-x64.zip`（6.3 MB）。

### 8.8 踩坑备忘（复现时别再踩）

- 构建/启动前必须 `Get-Process TaskbarLyrics | Stop-Process -Force`，否则 `error MSB3027/MSB3021: 无法将 … 复制到 … 文件被"TaskbarLyrics (pid)"锁定`。构建命令：`dotnet build src\TaskbarLyrics.App\TaskbarLyrics.App.csproj --no-restore -c Debug|Release`。
- `verify-p0-look.ps1` 的 `render ticks` 统计的是 `[overlay] Render pos=` 日志行数；节流后只剩 1–3 行，属正常（**不是**判定项）。该脚本启动 app 后固定等 12s 就抓图，若正好撞上换歌的播放空档会误报 3 项 FAIL —— 重跑即可。
- PowerShell 里抓图/发按键前先把 `Add-Type` 与位图预热做完：曾把 `Add-Type -AssemblyName System.Drawing` 放在点击之后，实际抓图晚了约 20s，拿到"已恢复播放"的错误状态截图，差点得出"点击没生效"的错误结论。**状态结论一律以日志为准。**

### 8.9 未做

§4 的 P2（高亮驱动滚动：已唱边缘过可视宽 90% 才跟随、`1 − exp(−5·dt)` 指数跟随、<0.5px 吸附、换行归零）与 P3（渐变擦除 / MiSans 子集 / 完整小组件形态）未动。其中 P2 与 60fps 组合起来才是"跟唱感"的最后一档。

## 9. 悬停布局 + 曲目信息三开关（已落盘并通过全部验收）

### 9.1 需求（用户原话要点）

> 鼠标停留在歌词上才显示播放按键，但歌词始终靠在右侧；期望鼠标停留时歌词保持原位（靠右），**鼠标离开时歌词平滑动画到整个区域的居中位置**，不要瞬移。另加开关分别控制是否显示歌曲名 / 歌手 / 唱片封面。自行完成测试验证。

### 9.2 设计决定（附理由，理由才是重点）

- **封面 / 歌名 / 歌手只在鼠标停留时出现**（左侧，与播放按键同批），离开时整条只剩歌词并真正居中。理由：只有悬停时才占用左侧，不悬停时没有任何东西和高亮歌词抢位置，才能做到「整条居中」；若把它们常驻左侧，居中的目标就只能退化成「剩余区域居中」或「与歌词重叠」。
- **让位用 `ContentInset`（`KaraokeLine` 的渲染期依赖属性），既不用 `Margin`，也不做「平移 + 裁剪」**。理由：`ContentInset` 只参与 `OnRender` 的排版计算——把文字在「整条宽 − 左侧预留」的剩余区域里重新居中，不触发布局、不重新测量文字，所以能跟着 160 / 240 ms 的缓动逐帧动，而**不切掉任何字形**。早先的 `TranslateTransform` + `Clip` 方案会把每行开头裁掉（见 §9.4 与 §9.5.8），已废弃。
- **两行歌词改为 `Grid.ColumnSpan="2"`（横跨整条）**。理由：若让左簇（封面+文案+三个按键，实测 203 DIP）继续占着第 0 列，静止时歌词列只剩 `460 − 16 − (203 + 6) ≈ 235` DIP，用户 16px 字号下约 14 个字就触发跑马灯；横跨整条后静止态天然居中（位移恰为 0），可用宽度回到 444 DIP。
- **（已废弃）「平移半个左簇 + 每行自己的 `Clip` 裁掉左簇区域」**。它确实不触发重新测量、能跟着位移动，但代价是把每行开头切掉：同一条短行静止 128 DIP、悬停只剩 74 DIP（悬停墨迹最左正好落在裁剪边），句子读不完整。正确做法是把文字**重新排进剩余区域**（`ContentInset`），而不是把它裁掉。
- **左簇宽度实测**（`LeftCluster.ActualWidth` + 它自己的右 margin），没有任何硬编码宽度。理由：歌名 / 歌手 / 封面 / 按键任一变化都会改变它，写死必然错位。

对应的不变量（脚本每次都会验）：`inset ≤ strip − 16`（不让位到吃掉整条）；左簇不出现时 `inset == 0`；左簇出现时 `inset == cluster + 6`。唯一的数字来源是 app 自己那行日志：`[overlay] lyric inset=… cluster=… info=… title=… artist=… trans=… strip=… line=… occupied=… hovered=…`。

### 9.3 改了哪些文件

| 文件 | 改动 |
| --- | --- |
| `src\TaskbarLyrics.App\Configuration\AppSettings.cs` | 新增 `ShowSongTitle` / `ShowSongArtist` / `ShowCoverArt`（默认 true）；`ConfigPath` 支持 `TBL_SETTINGS` 覆盖（供脚本隔离测试用，与 `TBL_DIAG` / `TBL_COMPOSITE` 同一套路） |
| `src\TaskbarLyrics.Core\Media\IMediaSessionSource.cs` | 新增 `Task<byte[]?> TryReadAlbumArtAsync(CancellationToken ct)` |
| `src\TaskbarLyrics.Core\Media\SmtcMediaSessionSource.cs` | 该接口的 SMTC 实现：`TryGetMediaPropertiesAsync` → `Thumbnail.OpenReadAsync` → `DataReader`；`MaxAlbumArtBytes = 8 MB` 上限；封面与 `GetCurrentAsync` 分开，只在换歌时读 |
| `src\TaskbarLyrics.App\App.xaml.cs` | `PollMediaAsync` 灌歌名/歌手，并按曲目 key 拉封面（`LoadCoverArtAsync`，读回后再比对 key，防止旧封面盖到新曲目）；`DecodeCoverArt` 用 `DecodePixelWidth = 64` + `OnLoad` + `Freeze`；`ApplySettingsLive` 里重新打开封面开关时补拉一次（否则不会再有换歌事件来补） |
| `src\TaskbarLyrics.App\Views\OverlayWindow.xaml` | 新增 `LeftCluster` → `InfoPanel`（28×28 圆角封面 + `SongTitle` / `SongArtist`，`MaxWidth = 104`、`TextTrimming = CharacterEllipsis`）+ 原有 `TransportPanel`；两行 `KaraokeLine` 加 `Grid.ColumnSpan="2"`（横跨整条，静止即天然居中；`TranslateTransform` 与逐行 `Clip` 都已移除，让位改由 `ContentInset` 完成） |
| `src\TaskbarLyrics.App\Views\OverlayWindow.xaml.cs` | `HasTrackInfo`；`_hoverReveal` 纳入信息区；`SetControlsShown` 统一用 `FadeTo`（播放区 / 信息区 / 拖动柄同一时钟）；`UpdateLyricShift`（算出 `inset`，用 160 / 240 ms `CubicEase` 缓动两行的 `KaraokeLine.ContentInset`）；`ApplyCoverBackground`、`SetTrackInfo`、`SetCoverArt`；`ApplyTransportPalette` 里补信息区配色与 halo |
| `src\TaskbarLyrics.App\Views\SettingsWindow.xaml(.cs)` | 「歌词条内容」组里新增三个 CheckBox（歌名 / 歌手 / 封面），载入与回写各三行 |
| `tools\verify-hover-shift.ps1` | 新增：真机像素验证静止居中 / 悬停避让 / 是渐变不是瞬移 |
| `tools\verify-cluster-modes.ps1` | 新增：`TBL_SETTINGS` 隔离跑「常显模式 / 信息全关 / 打开设置窗口」三种路径 |

### 9.4 实测数据（本机 Win11 26200 / 2560×1440 @125%，真机像素）

- `& tools\verify-hover-shift.ps1` → **4/4 PASS**（外加一条坐标空间交叉校验直接 PASS）
  - 坐标空间自证：`screen 2560x1440 px, dpi 120 -> scale 1.25`；`overlay rect (326,1381)-(901,1439) 575x58 px = 460.0x46.4 DIP`
  - 静止：墨迹 188–386 px（宽 **198 px**），质心 **290.6 vs 条中心 287.5（偏 3.1 px）**
  - 悬停：让位后文字房间 314–565 px（`inset = 242.8 DIP`），房间内墨迹 340–538 px（宽 **198 px**），质心 443.9 vs 剩余区域中心 439.5（偏 4.4 px）
  - **关键证据：`ink width 198 px at rest vs 198 px on hover`——墨迹宽度不再缩水，即不再切掉字形**（旧「平移 + 裁剪」版是 128 DIP → 74 DIP）
  - 返回：一帧内 **14 个不同距离**（8588 → 3522 px）→ 是渐变而非瞬移
  - **新增交叉校验**：`PASS coordinate space: 460.0 DIP from 575 px matches the app's own W=460.0 DIP`（拿 app 日志里的 `Render … W=` 反查脚本自己的 DIP 换算；缺行则 SKIP）
  - advisory：`ClusterEnd` 回 -1——新布局把文字重新排过之后，不再保证左簇与文字之间存在 40 DIP 宽的纯空白带，该项已降级为只打印
- `& tools\verify-cluster-modes.ps1` → **全 PASS（15 项）**
  - 钉住常显（`HoverRevealControls=false`）：`inset=178.0 cluster=172.0`、`inset=242.8 cluster=236.8` —— 无需悬停就永远避开左簇
  - 三个信息开关全关：`inset=130.0 cluster=124.0 info=0.0 trans=124.0`（只剩播放区），悬停时不再移动；离开时 `inset=0.0`、`occupied=False`
  - `--settings` 打开设置窗口：0 条 `UNHANDLED`，overlay 仍在渲染
- `& tools\acceptance.ps1` → **19 passed, 0 failed**（engine self-test 101、settings self-test 29、overlay rect `(326,1381)-(901,1439)` 575×58、distinct colours 346）
- `& tools\verify-p0-look.ps1 -HoverTest` → **8/8 PASS**（idle 左带 0 px、透视 93.0%、hover 左带 606 px、蓝 262 px、`palette auto=True lightBar=True`）
- 封面确实在渲染：hover 帧封面区（x 11–46）**44 色 / 亮度 67–221**，rest 帧同区 3 色（纯背景）
- 帧率无回归：`[perf] render tick n=300 fps=56.3 p50=0.036ms p95=0.075ms gen0=1`

### 9.5 踩坑备忘（新增，复现时别再踩）

1. **DPI 不感知的 PowerShell 抓图全是假数据**。`GetWindowRect` 返回虚拟化逻辑坐标（460×46 而不是 575×58），`CopyFromScreen` 于是拿到降采样虚化画面：墨迹从真实约 2000 px 虚高到 7481 px、质心完全错位，差点据此判定「布局坏了」。**抓图前必须 `SetProcessDPIAware()`**，两个新脚本都在最前面调用（并把屏幕分辨率打印出来自证）。
2. **PowerShell 函数名撞上内建别名会被静默吞掉**。函数命名 `Measure` 后被别名 `measure`（Measure-Object）遮蔽，函数从未执行，`$rest` / `$hover` 为 `$null`，断言全 FAIL 却看不出原因。改名解决。
3. `Add-Type -TypeDefinition` 里**不能引用 System.Drawing**：本机 pwsh 的 `TRUSTED_PLATFORM_ASSEMBLIES` 长度为 0，`[System.Drawing.Bitmap].Assembly.Location` 指向 GAC 里的 .NET Framework 版本，编译期直接报 `System.Drawing.Imaging` 不存在。→ 像素扫描全部在 `byte[]` 上做，位图由 PowerShell 侧 `LockBits` + `Marshal.Copy` 提供。
4. `Start-Process -ArgumentList @()` 直接抛参数验证错误（不接受空集合），没有参数时不要传这个开关。
5. **覆盖 `$env:APPDATA` 换不掉设置文件**：app 走的是 shell 已知文件夹（`GetFolderPath`），不是环境变量，所以「隔离 APPDATA」的测试实际上一直在读写真实配置——第一轮三种模式数字完全相同就是这么被骗过去的。为此新增 `TBL_SETTINGS` 覆盖后三例才真正分开。
6. **这个 shell 是 Windows PowerShell 5.1，不是 PowerShell 7**（`$PSVersionTable.PSVersion` = `5.1.26100.9444`，尽管工具名写作 `pwsh`）：`Get-Content` / `Add-Content` 默认走 ANSI（本机 GBK），把 UTF-8 中文读成乱码再写回去，文件立刻变成**非法 UTF-8**（`read` 工具直接报 `cannot read ...: invalid UTF-8 text`；`Get-Content .Count` 报 289 而实际 391 行）。**中文文件一律只用 `read` / `write` / `edit` 工具改**；万不得已要在 pwsh 里拼接，就做**字节级**操作：`[System.IO.File]::ReadAllBytes` 定位 ASCII 标记（本轮用 `## 9. ` 的字节偏移 `0x23 0x23 0x20 0x39 0x2E 0x20`）→ 保留其之前的原始字节 → 用 `WriteAllBytes` 接上新段落的原始 UTF-8 字节（本轮 `31130 + 8175 = 39305` 字节修回）。另外**脚本里不要写中文字面量做判定**，改用 ASCII 排除法（排除 IME / GDI / 合成管线窗口后剩下的就是自己的窗口）。窗口标题同理不可靠。
7. `AllowsTransparency="True"` 下 `Effect`（halo）会走软件渲染：本轮只给歌名/歌手加了 halo，与 `ProgressText` 同一处理，未见帧率回归（p95 仍 0.075ms），但不要在全条铺开。
8. **「平移 + 裁剪」不等于「让位」**。`TranslateTransform` 平移半个左簇再 `Clip` 掉左簇区域，会把每行的开头切进裁剪区：同一条短行静止时墨迹 128 DIP，悬停后只剩 74 DIP，句子读不完整。更坏的是它曾经「通过」了检查——旧脚本拿 DIP 魔数当像素用（`$split = $w / 2` 在 125% 下等于 230 DIP），恰好越过了真实的左簇末端。修法是**重新排版**（`ContentInset`）而不是裁掉；断言也必须同时比「静止 / 悬停的墨迹宽度」和「墨迹质心 vs 剩余区域中心」，只比整条中心会把这种缺陷放过去。
9. **脚本里的几何一律 DIP 命名 + 单一换算**。抓图与 `GetWindowRect` 给的是设备像素，`SetProcessDPIAware()` 之后必须**自证**：`SM_CXSCREEN` 要等于 `GetDeviceCaps(hdc, DESKTOPHORZRES)`（后者永不被虚拟化）。旧脚本把 `100`、`$w - 15`、`40` 这些 DIP 魔数当像素阈值，125% 下只剩 22 DIP 余量，而且从不校验 DPI 感知是否真的生效。
10. **边界不要从像素猜**：左簇占多宽改从 app 自己的日志读 `inset=`（DIP），脚本里只做一次 `Convert-Dip`；像素启发式（`ClusterEnd`）只留作交叉校验。

### 9.6 未做

- §4 的 P2（高亮驱动滚动）与 P3（渐变擦除 / MiSans 子集 / 完整小组件形态）当时仍未做——**本轮（见 §10）已完成 P2 与 P3 的渐变擦除、MiSans 子集已产出但未接入，仅「完整小组件形态」仍未做**。
- 封面只在换歌时读一次；同一曲目播放中封面若变化，SMTC 没有事件可依，不会自动更新。
- 翻译行仍跟随原文字号独立居中，未做「译文靠左对齐原文字头」这类精修。

---

## 10. 播放键圆盘 + P2 跟唱滚动 + P3 渐变擦除/MiSans 子集（本轮）

### 10.0 结论一览（用户问「能否完成、完成到哪一步」）

| 项目 | 能否完成 | 本轮完成情况 |
| --- | --- | --- |
| 任务一：播放/暂停键底板改成正圆 | 能 | **已完成**，有真机像素证据（§10.2） |
| P2 高亮驱动滚动（跟唱感） | 能 | **已完成**，有真机 `follow` 轨迹逐项核对（§10.3） |
| P3a 高亮边缘渐变擦除 | 能 | **已完成**，但缺像素级证据（§10.4） |
| P3b MiSans 子集（汽水同款字体） | 能 | **已完成并接入**：子集化后作为内置字体随包发布（设置 → 字体 → MiSans（内置·汽水同款）），有真机 `face=MiSans` 证据（§10.5） |
| P3c 完整小组件形态 | 能，属结构性改造 | **未做**（§10.6） |

### 10.1 本轮需求

- 任务一：把点击框底部那块深色区域改成圆形，参照附图；有歧义先确认。澄清后用户答复（转述）：**「就是鼠标悬停在右上角时会变深色的那种区域」**。
- 任务二：评估并尝试完成 §4 的 P2 与 P3，说明能否完成与完成情况。

### 10.2 任务一：播放/暂停键底板 = 正圆

**定位过程。** 参考图（52×50）4 色量化直方图：`#F3F3F3`×1595（Win11 浅色任务栏底）、`#E7E8E9`×730（≈5% 黑）、`#61666B`×52、`#83868A`×41 ⇒ 读作「任务栏底色上一枚 ≈35 px 的浅灰圆盘」。结合用户答复，我们条内唯一会因悬停加深的区域就是播放/暂停键底板（7% → 12% 黑），故它就是目标；参考图是圆 ⇒ 做成**正圆**（上一首/下一首保持无底板）。

**对齐汽水原件。** `ActionButton.vue`（`%TEMP%\soda-asar\flat-tw\src__rendererTaskbarWidget__ActionButton.vue`）：按钮 32×32、`background: transparent`、图标色 `light-dark(rgba(0,0,0,.70), rgba(255,255,255,.70))`、hover `light-dark(#000,#fff)`；`.action-button--playback` 宽 48 px、`border-radius: 9999px`、底 `light-dark(rgba(0,0,0,.07), rgba(255,255,255,.07))`、hover `.12`。⇒ **汽水只有播放/暂停有底板，且是 48×32 的胶囊**；本轮按用户要求做得比汽水更「圆」。

**改动。** `OverlayWindow.xaml` 的 `PlayPauseButton`：`Width` 44 → **30**、`Height` 30、圆角 **15**（半径 = 两轴一半 ⇒ WPF 画出来即正圆）。

**像素证据**（`%TEMP%\tbl-plate2.ps1`，hover 帧 575×58 px、scale 1.25；用 `%TEMP%\tbl-plate.json` 关掉封面以免中间调污染）：

- 取样点：`(165,20)=#C3CDC8`、`(165,29)=#C3CDC8`、`(180,29)=#C4CEC8`（luma ≈ 201–202）vs `(120,29)=#D4DFDA`、`(150,29)=#D4DFD9`（luma ≈ 219）⇒ 底盘 luma ≈ 202，任务栏底 ≈ 219（Δ17 ≈ 12% 黑），底盘只出现在 x ≈ 162..199 px 一带。
- 逐行水平跨度（px）：y2=7、y3=12、y4=18、y5=22、y6=24、y7=26、y8=28、y9=30、y10=32、y12=34、y14=36、**y18..y24 平台峰值 38**、y28=34、y30=32、y32=30、y34=26、y35=24、y36=22；中心固定在 x ≈ 180.5 px，垂直跨度同为 ≈38 px。
- ⇒ 峰值 **38 px / 1.25 = 30.4 DIP**，与代码里的 30 DIP 吻合；跨度单调先增后减、**没有等宽平顶与直边** ⇒ 是**正圆**而非胶囊/圆角矩形（胶囊会在赤道附近出现等宽平顶）。

### 10.3 P2：高亮驱动滚动（跟唱的最后一档）

**设计**（`src\TaskbarLyrics.App\Controls\KaraokeLine.cs`）：

- 常量：`FollowFraction = 0.9`、`FollowRate = 8.0`、`FollowSnapPx = 0.5`、`HighlightFadeRatio = 0.45`。
- 每帧在算出 `scrolling` 之后：`var progress = Math.Clamp(Progress, 0, 1); var sungWidth = IsCurrent && WordHighlightEnabled ? MeasureSungWidth(main, progress) : 0.0; FollowSweep(scrolling, room, sungWidth, progress);`
- `FollowSweep`：`maxOffset = Math.Max(0.0, textWidth - room)`；`target = Math.Clamp(sungWidth - FollowFraction * room, 0.0, maxOffset)`；`_scrollOffset` 以 `1 - exp(-FollowRate * dt)` 指数逼近 `target`，差值 < 0.5 px 直接吸附；换行归零（`_lastProgress` / `_singing`）。
- `AdvanceScroll()` 增加 `if (_singing) return;` ⇒ 跑马灯只服务「已唱完但还装不下」的尾段，与跟唱互斥。
- 诊断（仅 `Diag.Enabled`，且与上次输出相差 > 4 px 才写）：`[karaoke] follow offset=… target=… sung=… room=… max=… text=… progress=…`。

**真机证据**（`%TEMP%\tbl-demo.json`：字号 30 + `Width=300`，同一曲目 `text=368.9 DIP`，`room` 在 90.8 / 284 两态间随悬停切换）：

```
offset=5.0   target=11.8  sung=93.6  room=90.8  max=278.1 progress=0.224
offset=9.0   target=16.6  sung=98.3              progress=0.238
offset=73.5  target=80.3  sung=162.0             progress=0.392
offset=79.0  target=84.4  sung=166.1             progress=0.407
offset=145.9 target=154.6 sung=236.3             progress=0.621
offset=150.7 target=159.4 sung=241.2             progress=0.635
offset=235.4 target=241.6 sung=323.4             progress=0.820
offset=239.6 target=245.1 sung=326.9             progress=0.836
offset=270.4 target=276.7 sung=358.5             progress=0.962
offset=274.5 target=278.1 sung=363.6             progress=0.981
静止态：offset=78.9 target=84.5 sung=340.1 max=84.9 / offset=83.0 target=84.9 sung=345.6
```

逐项核对：`target = sung − 0.9 × room`（236.3 − 81.7 = 154.6 ✓；340.1 − 255.6 = 84.5 ✓）；`max = text − room`（368.9 − 90.8 = 278.1 ✓）；`progress` 到 0.981 时 offset 触到 `max` 被夹住 ✓；offset 始终滞后 target（指数平滑）✓；`room` 随悬停让位实时切换（90.8 ↔ 284）✓。

**踩坑（值得记住）**：诊断去重字段初值写成 `double.NaN` 时，`Math.Abs(x - NaN) > 4` **恒假**，诊断行一条都不输出——grep 空空如也，差点据此误判「P2 没生效」。改为 `double.NegativeInfinity` 并加注释后正常。

### 10.4 P3a：高亮边缘渐变擦除

高亮那一遍不再是「硬边裁剪 + 同色重绘」，而是把冻结的 `LinearGradientBrush` 当 `OpacityMask`：`StartPoint = (left + sungWidth − fade, 0)`、`EndPoint = (left + sungWidth, 0)`、色标白 → 透明，`fade = Math.Min(FontSizeValue * HighlightFadeRatio, sungWidth)`（字号 16 ⇒ 约 7.2 DIP 的柔边）。裁切区同步改为 `scrolling ? new Rect(ContentInset, 0, room, ActualHeight) : new Rect(0, 0, ActualWidth, ActualHeight)`，避免滚动时把已滚出去的头部又画到左簇底下。

**诚实说明**：这条路径每帧都会走（`fade > 0.5` 即构造遮罩），高亮渲染正常、帧率无回归，但**没拿到「渐变宽度 ≈ 7.2 DIP」的像素级证据**——`%TEMP%\tbl-ramp.ps1` 的采样窗是按静止态 `room` 算的，而抓图时 inset 还在动画中（该帧 `room=177.6`），窗口错位；采样行 y=17 在 x > 313 px 之后已无墨迹。未再继续深挖。

### 10.5 P3b：MiSans 子集（已完成并接入）

- 源：`D:\Users\Qianmory\AppData\Local\Programs\Soda Music\3.7.0\resources\fonts\MiSansVF.woff2`（11,907,336 B，可变字体，单轴 `wght` 100/400/900）。
- 流程：`fontTools`（`brotli` 原缺，`python -m pip install brotli` 后可用）→ 解出 `MiSans-full.ttf` 11,910,584 B → **`wght` 钉到 600** 得静态实例（本条的 `FontWeight` 默认 `SemiBold`），并改 name 表：nameID1 = `MiSans`、2 = `SemiBold`、4 = `MiSans SemiBold`、6 = `MiSans-SemiBold`，`OS/2.usWeightClass = 600` → 子集（ASCII + Latin-1 + 0x2000–0x2070 + 0x3000–0x3100 + 全角 0xFF00–0xFF60 + GB2312 汉字，共 7909 字）⇒ **`src\TaskbarLyrics.App\Fonts\MiSans-subset.ttf` 1,925,892 B**（未子集的静态实例 4,486,452 B）。
- 打包：`TaskbarLyrics.App.csproj` 里同时登记 `<Resource>`（嵌进程序集）与 `<Content CopyToOutputDirectory="PreserveNewest">`（拷到 `Fonts\` 子目录）；后者才是实际被加载的那份，前者留给宿主自包含场景。两项都带 `Condition="Exists('…\Fonts\MiSans-subset.ttf')"`，因此**仓库里没有字体文件时构建照常**。
- 接入：新增 `src\TaskbarLyrics.App\Configuration\BundledFonts.cs`。设置里存的是**显示名** `MiSans（内置·汽水同款）`，`Resolve()` 把它换成真正可用的字体源：候选顺序为「输出目录里的绝对 `file:///…/Fonts/MiSans-subset.ttf#MiSans`」→ 四条 `pack://` 写法 → 全失败则回退 `Microsoft YaHei UI`（宁可换系统字体，也不用一个会渲染成任意默认字形的 URI）；探测结果缓存（`Resolve` 也会被诊断行调用，且加载字体有 I/O）。`OverlayWindow.ApplySettingsToVisuals` 里 `line.FontFamilyName = BundledFonts.Resolve(_settings.FontFamily);`，设置窗字体下拉框第一项插入该显示名。
- **真机证据**（`%TEMP%\tbl-misans.json` 把 `FontFamily` 换成 `MiSans`，`TBL_DIAG=1`）：
  - `[overlay] ApplySettingsToVisuals … font=MiSans resolved=file:///E:/Qianmory/Desktop/DesktopMusic/src/TaskbarLyrics.App/bin/Debug/net8.0-windows10.0.19041.0/Fonts/MiSans-subset.ttf#MiSans/16`
  - `[karaoke] font source=file:///…/Fonts/MiSans-subset.ttf#MiSans face=MiSans weight=SemiBold chars=13 width=196.7` ⇒ **字形真的来自内置 MiSans**（`face` 不是回退族）。
  - 度量确实换了字体：同一段 13 字文本，微软雅黑 `width=185.5` vs MiSans `196.7`。
- 授权与再分发（**已按此处理**）：字体自带授权，不是本项目可以转发的资产，而这份子集的来源是另一个应用的安装目录。因此**仓库不提交任何字体文件**：`.gitignore` 屏蔽 `src/TaskbarLyrics.App/Fonts/*.ttf|otf|woff2`，目录里只保留说明文件 `Fonts\README.md`（自备 TTF 的方法 + 本机子集化脚本片段）。上面的路径与体积只描述**本机自用**的现状；`BundledFonts` 在文件缺席时回退系统字体，功能不会坏。

### 10.6 P3c：完整小组件形态 —— 未做

现条已具备封面 / 歌名 / 歌手 / 播放键 / 进度，缺的是汽水那种「整块面板 + 圆角 + 边框 + 主题色 + idle 收纳动画」的整体形态，属结构性改造，本轮未动。

### 10.7 本轮验收（四套全绿）

- `& tools\acceptance.ps1` → **19 passed, 0 failed**（engine self-test 101、settings self-test 29、overlay rect `(193,1381)-(768,1439)` 575×58、distinct colours 388、`adaptive palette paints legible ink (bar luma=219 ink=38,41,40 …)`、lyrics `lines=49`、`source is a real provider (QqMusic)` + `score=100`）。**字体改动之后重跑**。
- `& tools\verify-p0-look.ps1 -HoverTest` → **8/8 PASS**（第一次只有 7/8，唯一 FAIL 是脚本读日志尾部的 `controls hover-revealed (shown=…)` 时读到了**上一个 app 实例留下的陈旧行**；像素侧 `ink in left band: 0 px` 已证明静止时控件确实隐藏。删掉 `%TEMP%\taskbar-lyrics-diag.log` 后重跑即 8/8。**脚本这个脆弱点尚未修**：应只读本次运行新增的日志行）。
- `& tools\verify-hover-shift.ps1` → **全 PASS**（字体改动之后重跑）：`screen 2560x1440 px, dpi 120 -> scale 1.25`、`overlay rect (193,1381)-(768,1439) 575x58 px = 460.0x46.4 DIP`、静止墨迹中心 284.1 vs 条中心 287.5（偏 3.4 px）、悬停 `inset=262.0 DIP` ⇒ 房间 338..565 px 中心 453.8 vs 451.5（偏 2.3 px）、`PASS animated: 16 distinct distances on the way back`、坐标空间交叉校验 `460.0 DIP from 575 px matches the app's own W=460.0 DIP`。打印行 `ink width 289 px at rest vs 226 px on hover` 是**设计行为**：这一帧的当前行比悬停让位后的房间更宽，于是它转为跑马灯 + 跟唱（而不是把字形硬切掉）。
- `& tools\verify-cluster-modes.ps1` → **全 PASS**（字体改动之后重跑）：常显 `inset=262.0 cluster=256.0`；三信息开关全关 `inset=116.4 cluster=110.4 info=0.0 trans=110.4`；`--settings` 打开设置窗 0 条 fault、overlay 仍在绘制（证明字体下拉框改动没炸）。
- 帧率无回归：`[perf] render tick` fps 55–60、p50 0.026–0.055 ms、p95 0.063–0.122 ms、gen0 1–2。
- 脚本使用契约：`verify-hover-shift.ps1` / `verify-cluster-modes.ps1` 都要求 **app 已经在跑**（没跑分别报 `FAIL: TaskbarLyrics is not running` / `FAIL: overlay window not found`）；两者连跑会撞单实例互斥，中间要 `Get-Process TaskbarLyrics | Stop-Process -Force` + 删日志 + 重新启动等待约 10 s。`acceptance.ps1` 自己启停实例。

### 10.8 本轮踩坑（新增）

1. **诊断去重哨兵值不能用 `NaN`**：`Math.Abs(x - NaN) > 4` 恒假，诊断静默消失（P2 版就是被这个骗过）。
2. **探针算「期望边界」不要用缓存值**：用静止态 `room` 算出的采样窗，在 inset 仍在动画时会整体错位——边界一律从日志的当前 `inset` 现算。
3. `%TEMP%\taskbar-lyrics-diag.log` 被 app 独占，`ReadAllLines` 抛「正由另一进程使用」；改用 `New-Object System.IO.FileStream($p, Open, Read, ReadWrite)` 再 `StreamReader.ReadToEnd()` 并加重试。
4. PowerShell 里 `"$name: …"` 会被当成作用域/驱动器解析而报错，要写 `${name}`。
5. 跑 `verify-p0-look.ps1` 前先删旧日志，否则可能读到上一次运行的结论行。
6. **「众数亮度」不能当已知色用**：`tbl-plate2.ps1` 第一版把窗口内出现最多的亮度当底盘色，结果选中任务栏底色本身（背景像素占多数），跨度毫无意义；正确做法是先在已知盘内取样点读色，再按 ±5 luma 求跨度。
7. **`pack://` 字体 URI 在本机解析不出字形**：资源确实嵌进了程序集（dll 里能找到 WPF 规范化后的键 `fonts/misans-subset.ttf`），但代码里四种 pack 写法（含 `#MiSans` 片段、含小写路径）`Typeface.TryGetGlyphTypeface` 全返回 false。改走**绝对 `file:///…Fonts\MiSans-subset.ttf#MiSans` + 随 exe 拷贝的 ttf** 立刻成功。⇒ 自定义字体一律「候选列表 + 实测可用性 + 兜底系统字体」，不要假定 `#FamilyName` 片段一定生效。
8. **证明「字体真的生效」的唯一硬证据是 `face=`**：新增的 `[karaoke] font source=… face=… weight=… chars=… width=…` 取自 `TryGetGlyphTypeface`，`face=MiSans` 才算用上；`resolved=` 只说明请求了什么。可用同长度文本的 `width` 交叉验证（微软雅黑 185.5 vs MiSans 196.7）。
9. **WPF 项目的隐式 using 里没有 `System.IO`**：`Path`/`File` 直接报 `error CS0103`；`Typeface` 则要 `System.Windows` + `System.Windows.Media`（否则 `error CS0246`）。
10. **别用宽表格读文件长度**：`Select-Object FullName, Length` 会把 1,925,892 折行显示成 `892`，看着像文件被截断；直接 `(Get-Item $p).Length`，必要时用 MD5 三方比对（源 / 输出 / `%TEMP%` 副本 `15802028DC78EF4F603A27C92D0CE98E`）。

### 10.9 未做 / 后续

- P3c 完整小组件形态。
- `verify-p0-look.ps1` 的「陈旧日志行」脆弱点。
- P3a 渐变宽度的像素级验证。
- MiSans 的随包再分发授权：**已按「不再分发」处理**——`.gitignore` 屏蔽字体文件、仓库只留 `Fonts\README.md`、csproj 的字体项带 `Exists()` 条件（见 §10.5 末条）。
- 本轮的面板开关圆盘改造见 §10.10。

### 10.10 导航窗格折叠开关：方形底板 → 正圆（任务一，目标更正后）

- **目标更正**：任务一原描述是「按钮点击框底部那块深色区域改成圆形」，参照图（213×47 px）解读后确认指的是**设置窗导航栏右侧的折叠开关**（悬停时变深色的那块），不是叠加条的播放/暂停底板。参照图像素读数：`#DADADA` 6825 px（窗格底）、`#CDCDCD` 1251 px（那块悬停底板）、`#1A1A1A` 405 px（字形）、`#F3F3F3` 235 px（内容区）；底板约 x 155..200 px、内含约 24×19 px 的面板图形 —— 正是窗格头部「应用图标 + 任务栏歌词 + 折叠开关」这一行。
- **缺陷**：`SettingsWindow.xaml` 的 `CaptionButton` 样式（:275）模板只有 `<Border x:Name="Chrome" Background="Transparent">`，**没有 `CornerRadius`**，悬停底色（`#0F000000`，按下 `#1A000000`）是直角矩形；`NavToggleButton` 直接用这个样式、尺寸 36×28。
- **修法**：新增 `PaneToggleButton`（`BasedOn` `CaptionButton`），模板改成 `<Border x:Name="Chrome" CornerRadius="14" …>`，尺寸 `Width/Height = 28`；`NavToggleButton` 改用它。**圆的关键是盒子为正方形且半径 = 半边长**：同样半径套在 36×28 上只会得到胶囊。
- **真机像素证据**（`tools\verify-toggle-disc.ps1`，125% 缩放、设置窗 1125×800 px）：悬停底板 `35×35 px` = **28.0 × 28.0 DIP**；逐行跨度 `9 15 19 21 23 25 27 29 31 31 33 33 33 35×9 33 33 33 31 31 29 27 25 23 21 19 15 9` —— 首末行为 9 px（圆的收口）、**平均弦长 27.8 px vs π/4 × 35 = 27.5 px**（矩形会是 35 px）⇒ 是圆不是矩形也不是胶囊；静止时只有字形轮廓（371 tinted px）而悬停时 1026 px。
- **判据教训**：不能用「接近最大跨度的行数」区分圆与矩形 —— 35 px 的正圆本来就有约 11 行落在 ≥95% 直径内；要比较**首末行宽度**与**平均弦长（π/4）**。
- 播放/暂停键保持 `TransportButton` 30×30 正圆不变（用户未要求改回汽水的 48×32 胶囊）。
- `tools\verify-toggle-disc.ps1` 使用契约：要求 app **已带 `--settings` 运行**；脚本会把设置窗临时置顶（`SetWindowPos(HWND_TOPMOST)`）再截屏、结束前恢复（`SetForegroundWindow` 在非前台进程里会被拒，所以用前者）；它自动处理「启动即最小化」（`GetWindowRect` 落在 -32000 哨兵、被 DPI 缩放成 -25600）并 `ShowWindow(SW_RESTORE)`；窗口被遮挡时报 `settings window appears occluded`，而不是给出莫名其妙的 FAIL。
- 本轮改动已提交并公开上传，见 §11。

## 11. 公开上传：提交 / 变基 / 授权清理

- 目标（用户原话要点）：**上传工程，不要涉及版权违法内容**。
- **授权清理（不再分发第三方字体）**：MiSans 子集取自汽水音乐安装包，不随仓库分发 ——
  - `.gitignore` 增加 `src/TaskbarLyrics.App/Fonts/*.ttf`、`*.otf`、`*.woff2`；
  - `src\TaskbarLyrics.App\TaskbarLyrics.App.csproj` 的两条字体项（`<Resource>` 与 `<Content>`）都加 `Condition="Exists('$(MSBuildProjectDirectory)\Fonts\MiSans-subset.ttf')"`；
  - 仓库只保留 `src\TaskbarLyrics.App\Fonts\README.md`：说明目录为空是故意的、自备 ttf 的方法、以及完整的 fontTools 子集化脚本（`instancer.instantiateVariableFont(font, {"wght": 600})` + `subset.main([...])`）；
  - `README.md` 两处措辞改成「仓库不附带字体文件，需自备」。
- **字体缺席也必须能构建**（否则新克隆直接红）：把 `MiSans-subset.ttf` 改名后 `dotnet build "src\TaskbarLyrics.App\TaskbarLyrics.App.csproj" --no-restore -c Debug -v q` → **0 警告 0 错误**；放回后同样 0 警告 0 错误 ⇒ `Exists()` 条件生效。
- 秘密扫描（对将提交的文件与已跟踪文件）：唯一涉及凭据的是 `tools\push-api.ps1`，它在运行时从 Windows 凭据管理器读取 token、只打印长度，**无硬编码密钥**，保留。
- 提交：本地 `af17c98`，消息写在 `artifacts\commit-msg.txt`（`artifacts/*` 已被 gitignore）并用 `git commit -F` 提交 —— 本机是 **Windows PowerShell 5.1，不支持 heredoc**（`<<'MSG'` 直接解析错误）。
- **变基**：远端 `main` 已有同主题提交 `1c77d00`，与本地 `e8d87b3` **逐字内容相同、仅行尾不同**（`git diff --ignore-all-space e8d87b3 1c77d00 -- <file>` 为空；仓库 `core.autocrlf=true`，`git ls-files --eol` 显示 `i/mixed`）。`git rebase origin/main` 先撞这个重复提交，`git rebase --skip` 丢掉它、只重放本轮提交。
- **冲突解法**（仅 `src\TaskbarLyrics.App\Interop\FluentChrome.cs` 与 `src\TaskbarLyrics.App\Views\SettingsWindow.xaml`）：`git checkout af17c98 -- <两个文件>`，然后 `git add` + `GIT_EDITOR=true git rebase --continue`。**不能简单采用远端版本**：本提交对 `FluentChrome.cs` 有真实非空白改动（Mica 与不透明回退的注释和分支，`git diff --ignore-all-space` 可见）。
- **推送**：`git push origin main` 先报 `fatal: unable to access 'https://github.com/DoingStone/DesktopMusic.git/': Recv failure: Connection was reset`（本机没有配置代理）。加参数重试，第二次成功：
  `git -c http.version=HTTP/1.1 -c http.postBuffer=524288000 -c http.lowSpeedLimit=1000 -c http.lowSpeedTime=60 push origin main`
- 结果：`origin/main = c4fb32bdb03430b478715c9002bdf26bb7bfc2dd`；上传树 **112 个文件**，`.ttf/.otf/.woff2/.node/.asar/.dll/.exe` **零命中**；`Configuration\BundledFonts.cs`、`Controls\TransportButton.cs`、`Fonts\README.md`、`tools\verify-toggle-disc.ps1`、`tools\verify-hover-shift.ps1`、`tools\verify-cluster-modes.ps1` 均在树内。
- 变基后复核：`SettingsWindow.xaml` :311 `PaneToggleButton` 样式、:713 引用、:935/:938/:941 三个信息开关都在；构建 0 警告 0 错误。

## 12. 「可拖动」开关的两个缺陷 + 自由拖动 / 脱离任务栏 / 吸附

- **需求（用户原话要点）**：当前问题 1「点击关闭『可拖动』后，鼠标移开时歌词不移动、按钮也不消失」；问题 2「无法随意拖动歌词栏的位置，包括无法将其脱离任务栏」。期望功能 1「歌词栏可随意脱离任务栏，自由拖拽到任意位置」；期望功能 2「支持吸附（对齐）功能」。
- **问题 1 根因**：`OverlayWindow.xaml.cs` 的 `ApplyControlRevealMode()` 把悬停显隐和「可拖动」绑在了一起（`_hoverReveal = HoverRevealControls && _settings.Interactive && …`）。`AppSettings.Interactive` 就是 `!Locked`（`AppSettings.cs:154-169`），设置窗「歌词可拖动 / 可点击」写的正是它 ⇒ 关掉以后 `_hoverReveal=false`，代码走「常显」分支（把 `TransportPanel`/`InfoPanel`/`DragHandle` 的 Opacity 全重置为 1），而且 `_pointerTimer.Stop()` 让指针轮询彻底停下 ⇒ 鼠标移开既不隐藏也不归位，与用户描述逐字对应。
- **被推翻的旧前提**：改动前的注释与 `AppSettings.cs` 的 XML 文档都写着「点击穿透时窗口收不到鼠标消息，所以控件只能常显」。实际悬停判定根本不依赖窗口消息 —— `PollPointer()`（:1061）调 `IsCursorOverStrip()`（:1073），后者用 `NativeMethods.GetCursorPos` 取全局光标坐标，再与 `Left/Top/ActualWidth/ActualHeight × _taskbar.Scale`（±4 px slack）比较 ⇒ **点击穿透时同样有效**。修法就是删掉那一个 `&& _settings.Interactive` 项并更正注释；`DragHandle.Visibility` 仍只跟随 `Interactive`（不能拖的时候不该亮出拖动把手）。
- **问题 2 根因**：拖动只在 `_settings.Interactive` 时才开始，`OnMouseMove` 只算水平位移，而 `PlaceOverWindow()` 每次都把窗口夹回任务栏带内 ⇒ 结构和垂直方向都不可能离开任务栏。
- **新增设置**（`AppSettings.cs`，紧跟 `PlaceAboveTaskbar` 之后）：`FreePosition`（bool，默认 false）、`FreeX`/`FreeY`（double DIP，仅 `FreePosition` 为真时生效）、`SnapToEdges`（bool，默认 true）。入坞状态仍用原来的 `OffsetX/OffsetY`，两套位置互不干扰 ⇒ 关掉自由位置就回到原来的任务栏位置。
- **自由落位**：`PlaceOverWindow()` 算出宽度后分叉 `if (_settings.FreePosition) { PlaceFree(width); return; }`。`PlaceFree` 用监听器边界（`_taskbar.MonitorBounds` × `Scale`）而不是任务栏带：高度取 `Height>0 ? Height : max(16, bandHeight−2)`，再把 `FreeX/FreeY` 夹进 `[monitorLeft+8, monitorRight−width−8] × [monitorTop+8, monitorBottom−height−8]` 并**回写**设置 ⇒ 条带永远不会被拖出屏幕丢不掉；Z 序用 `SetTopmost`（不能用 `SetAbove(taskbar)`，否则普通窗口会盖住它）。
- **拖动语义**：`OnMouseMove` 同时算 dx/dy（都是 DIP，除以 `scale`），3 DIP 抖动阈值；只要条带中心离开任务栏带（`OverlapsBand` = 中心在带内，或位于带上方 0–24 DIP 的「贴着任务栏」区间）就切到自由位置（`EnterFreePosition`），此后写 `FreeX/FreeY`；松手时若中心又回到带内，`DockToTaskbar()` 把 `OffsetX` 反算成 `Left + ActualWidth − anchorRight`（`anchorRight` 优先取托盘左缘 `TaskbarLocator.GetTrayLeftDevicePixels()`，NaN 时退回任务栏右缘）⇒ 自由位置与入坞可以来回切换而不丢原来的水平位置。
- **吸附**：`Snap(ref left, ref top, width, height)` 两个轴各求一组候选线、**两边都能吸**（左边也试、右边也试），取 `SnapDistance = 12` DIP 内最近的一条。X 候选：监听器左缘+8、右缘−宽−8、水平居中、入坞列（`anchoredRight + OffsetX − width`）；Y 候选：监听器上缘+8、下缘−高−8、垂直居中、任务栏带内居中、带上方（`bandTop − height − AboveTaskbarGap`）。吸附在拖动过程中实时生效 ⇒ 松手前就能看到对齐。
- **自适应配色**：`SampleBackdropLuminance()` 原来只在任务栏带内取样（不在带内的点直接 `continue`），条带一浮起来就取不到背景 ⇒ 自由位置时改为按监听器边界判定，浮在桌面或窗口上也照样能自动黑白配色。
- **设置窗**：「常规 → 交互」新增「脱离任务栏（自由位置，可拖到屏幕任意处）」（`FreePositionCheck`）与「拖动时吸附对齐（屏幕边缘、居中与任务栏行）」（`SnapEdgesCheck`）；「尺寸与位置 → 微调位置」新增「吸附回任务栏」按钮（`OnDockToTaskbar`），四个方向微调按钮在自由位置时改的是 `FreeX/FreeY`（否则它们看起来像坏的）；「恢复默认位置与宽度」与托盘「恢复默认位置」也一并清掉自由位置状态，避免条带被拖到角落后再也回不来。拖动说明文案同步更新（关闭可拖动后仍会隐藏按钮并把歌词移回正中）。
- **实测**（`tools\verify-free-drag.ps1`，2560×1440 @125%，条带 575×58 px = 460×46.4 DIP，任务栏带 1380..1440 px）：
  - 用例 1（`Locked=true`，点击穿透）：悬停 `controls revealed` + `inset=116.4 DIP`（让位生效）；移开后 `controls hidden`（日志偏移 3541 → 4290）且 `inset` 回到 `0.0 DIP` ⇒ 问题 1 修复。
  - 用例 2（`Locked=false`）：起始 `top=1381 bottom=1439`（带内）→ 拖向右上角：`drag left the taskbar: free position` + 14 条跟随采样 → `top=10 bottom=68`（已离开带）→ **上边缘吸附到 10 px = 8 DIP**、**右边缘吸附到 2550 px = 屏宽−8 DIP**（拖放目标故意偏 7 DIP，落点仍在吸附线上 ⇒ 是真吸附而不是「刚好放对」）→ 拖回带上松手：`docked back to the taskbar`，矩形回到 `top=1381 bottom=1439` ⇒ 期望 1、2 达成。
- **回归（全部通过）**：`tools\acceptance.ps1` 19/19（引擎 self-test 101、设置 self-test 29 —— 新增设置项没破坏它、overlay 575×58、自适应配色可读）；`tools\verify-cluster-modes.ps1` 三例全 PASS（钉住常显时 `inset` 仍等于 `cluster + 6`、信息开关全关时 `cluster=110.4`、设置窗 0 条 fault）；`tools\verify-p0-look.ps1 -HoverTest` 8/8（静止左带 0 px、悬停左带 519 px、透视 96.4%）；`tools\verify-hover-shift.ps1` 全 PASS（静止墨迹质心 277.0 vs 条中心 287.5、悬停让位后墨迹 364..541 px 落在 `inset 266.8 DIP` 之后、**悬停与静止墨迹宽度同为 177 px** ⇒ 没有裁字、回程 16 个不同距离 ⇒ 是动画不是瞬移）。
- **踩坑**：
  1. `GetDeviceCaps` 在 **gdi32.dll**，不是 user32（`GetDC`/`ReleaseDC` 才在 user32）；写错只会在运行期炸 `EntryPointNotFoundException: 无法在 DLL“user32.dll”中找到名为“GetDeviceCaps”的入口点`，`Add-Type` 编译期不报。
  2. 断言要找对标记：`[overlay] controls hover-revealed (shown=…)` 是**应用设置时**写的模式行（启动时就是 `shown=False`，会让「离开后隐藏」的断言假通过），真正的悬停迁移标记是 `[overlay] controls revealed` / `controls hidden`（写在 `SetControlsShown` 里）。
  3. 日志里 `lyric inset=` 有很多条，必须取**最后一条**（`-match '(?s).*inset=([\d.]+)'` 的贪婪匹配）；取第一条会拿到启动时的 `inset=0.0`，报出「悬停没让位」的假 FAIL。
  4. `SetCursorPos` 不保证给被捕获的窗口投递 `WM_MOUSEMOVE`：每次移动后再补一对 `mouse_event(MOUSEEVENTF_MOVE, 1, 0)` 与 `(−1, 0)`（净位移为零），拖动才会被 `OnMouseMove` 看到。
  5. 往**下**拖到屏幕底部测吸附会失败：任务栏就在底部，条带中心仍在带内 ⇒ 松手会被判为「放回任务栏」而重新入坞。要测自由位置与吸附必须往**上**拖。
- **未做**：跨显示器拖动（`MonitorFromWindow` 换屏后重新落位）与拖动时的抓取点偏移修正未做；吸附没有视觉引导线（汽水音乐也没有，故不认为需要）。

## 13. 「收起后没有居中」：设置窗图标栏里的折叠按钮右偏 10 DIP

- **需求（用户原话要点）**：「悬浮栏/歌词栏收起（折叠）后没有居中」，要求 1) 定位「收起」后未居中的原因 2) 修复后收起状态保持居中；附图 129×60 px（sha256 `cde6a2104ebf1ddd09b8e83001d3177a672a184ae3d7ccc0dd30da91da78f662`）。
- **先排除悬浮条**：隔离启动（`TBL_SETTINGS` 指向用户配置副本，仅改 `OffsetX=0`）并把光标移开，抓 `GetWindowRect` 矩形 `1662,1381 575x58`；墨迹 663 px、bbox `x 481..564`（全在右端）、左侧 200 列零墨。日志 `[overlay] Render … left=1330 top=1105`（日志是 DIP，×1.25 = 抓拍坐标，二者一致）。这是**长行跑马灯滚到末尾**：`scrolling` 为真时 `LeftFor = ContentInset − _scrollOffset`，文本右端钉在内容框右缘（452 DIP），本来就不居中 ⇒ 与本缺陷无关。
- **真因**：本程序里「收起」只有一处 —— 设置窗左侧栏折叠成 64 DIP 图标栏（`AppSettings.NavCollapsed`，状态文案在 `SettingsWindow.xaml.cs:496`）。`NavToggleButton` 原本是 `HorizontalAlignment="Right"` + `Margin="0,0,8,0"`：展开时它与左边的 `TitleIdentity` 一左一右是刻意的配平；折叠后 `TitleIdentity` 被隐藏（`ApplyNavCollapsed` 里 `TitleIdentity.Visibility = Collapsed`），按钮成了 32 DIP 头部里唯一的内容，28 DIP 的按钮落在 36..64 DIP ⇒ 中心 42 DIP，而轨道中心只有 32 DIP ⇒ **右偏 10 DIP**。像素取证与推导一致（图标中心 52.5 px vs 轨道中心 40 px；轨道自身 1 DIP 右边框落在 x=78.75..80 px）。
- **修法**（`SettingsWindow.xaml.cs` 的 `ApplyNavCollapsed`）：右边距由 `RailWidth` 推出 —— `collapsed ? Math.Max(0, (RailWidth - toggleWidth) / 2) : 8`（`toggleWidth` 取 `Width`，为 NaN 时退回 `ActualWidth`）⇒ 收起时 18 DIP、展开时仍是 8 DIP，两个状态由同一个常量推导，不会再漂移；原注释「toggle 在头部任何宽度都自己靠右」一并改掉。
- **验证**：新增 `tools\verify-nav-rail-center.ps1`。几何取自 **UI Automation**（`AutomationId=NavToggleButton` 的 `BoundingRectangle`，物理像素、精确、不受窗口遮挡影响），两例各用 `TBL_SETTINGS` 隔离启动（`NavCollapsed=true` / `NavCollapsed=false, NavWidth=232`）。**6/6 PASS**：
  - 收起：窗口 left 718 px，按钮 `740.0..775.0` px ⇒ 中心 757.5 vs 轨道中心 758.0 = **0.5 px 偏差**（修前中心 770.5 ⇒ 12.5 px 偏差）；按钮 35.0×35.0 px = 28 DIP；19 DIP 图标占 `745.6..769.4`，两侧都留白。
  - 展开：按钮右缘 998.0 px = 窗格右缘 1008 − 8 DIP(10 px) **精确相等**，中心 980.5 = 期望 980.5；与收起态中心相距 223 px ⇒ 轨道的居中偏移没有带进展开态。
- **踩坑**：
  1. 截图像素法先试过：收起态的底色众数会被窗口左上圆角（露出的桌面像素）和轨道自身 1 DIP 的右边框污染，量出「墨宽 76 px」的假 FAIL ⇒ 改用 UIA 后不再依赖任何像素启发式。
  2. 设置窗启动即最小化，`GetWindowRect` 会返回 −32000 哨兵，而最小化时 `BoundingRectangle` 是空矩形 ⇒ 必须先 `ShowWindow(hwnd, 9)` 再读几何。
  3. 遗留（环境相关，非本缺陷）：`tools\verify-toggle-disc.ps1` 的前置步骤要在设置窗里找「窗格右缘」，它要求窗格与内容**两种色调可区分**；后台会话里设置窗拿不到前台，Mica 把整窗压成同一色调（实测 x=6 与内容列都读到 243），于是该前置判定失败并 `exit 1`。它检验的是**展开态**按钮悬停底板是否为圆（`PaneToggleButton` 模板，本轮一个字都没改），今天早些时候在同一台机器上是 6/6；本轮的等价几何断言由 `verify-nav-rail-center.ps1` 的展开态用例承担（把等待从 400 ms 加长到 1500 ms 也不改变该结果）。

## 14. README 更新与 Release v1.1.0（发布流程 + 无 `gh` CLI 的 GitHub API 通道）

- **需求（用户原话）**：「更新readme和Releases」。
- **先发现版权风险**：`scripts\publish.ps1` 产出的 `publish\TaskbarLyrics\` 与 `publish\TaskbarLyrics-win-x64.zip` 里都带着 `Fonts\MiSans-subset.ttf`（1,925,892 B）⇒ 直接上传 zip 等于**重新分发 MiSans 字体**，与「上传工程不要涉及版权违法内容」的约束冲突。改法：`publish.ps1` 新增 `[switch]$IncludeFonts`，默认把 `Fonts\*` 从 ZIP 里剔除并打印 `Fonts  : excluded from the ZIP -> Fonts\MiSans-subset.ttf  (-IncludeFonts to keep)`；本机 `publish\TaskbarLyrics\Fonts\` 仍保留字体，运行不受影响。重建结果：ZIP **8 个条目 / 7.56 MB / font entries: 0**。
- **README.md 的更新**：顶部新增「下载」段（指向 `releases/latest` 的 `TaskbarLyrics-win-x64.zip`、写明需要 .NET 8 Desktop Runtime、**zip 不含字体**）；「验证状态」整段换成本轮实测（引擎自检 101 / 设置读写 29 / 实读 `QQMusic.exe` / 歌词 51 行匹配分 100.0 / 叠加窗 `(234,1381)-(809,1439)` 575×58 / 297 色）并加一张五套交互脚本的表（free-drag 12/12、nav-rail-center 6/6、hover-shift 全 PASS、cluster-modes 全 PASS、p0-look 8/8）；体积数字改为「文件夹约 28 MB、ZIP 约 7 MB（默认不含字体）」；新增「更新日志」段（v1.1.0 / v1.0.0）；「已知限制」补两条（跨显示器拖动只做夹取不重新落位、吸附没有对齐参考线）。
- **没有 `gh` CLI 时的通道**：新增 `tools\gh-api.ps1`（从 Windows 凭据管理器 `git:https://github.com` 读 token，token 以 UTF-16LE 存放，需要按 `\n` 或 `:` 切分；`Get-GhToken` / `Get-GhHeaders` / `Invoke-GhApi`）与 `tools\gh-release.ps1`（按 tag 建 Release、可 `-Force` 替换、按文件名替换同名资产、用 `-Raw` 读上传响应）。凭据只在运行时读，仓库里没有任何密钥。
- **Release v1.1.0 结果**：`id=394335945`，标题 `v1.1.0 · 自由拖动与吸附`，tag `v1.1.0`（指向 `main` = `6ae6bda`），非 draft / 非 prerelease，资产 `TaskbarLyrics-win-x64.zip` 7.56 MB：`https://github.com/DoingStone/DesktopMusic/releases/download/v1.1.0/TaskbarLyrics-win-x64.zip`。线上资产已下载复核：8 个条目、`font entries: 0`。
- **踩坑（本节最值钱的部分）**：
  1. **Windows PowerShell 5.1 会把 `byte[]` 请求体字符串化**：`Invoke-WebRequest -Body $bytes`（7.56 MB 的 zip）上传后 GitHub 报的资产大小是 **27.23 MB**（坏资产）；同一条路径创建 Release 时直接返回 **422**：`{"message":"Invalid request.\n\nFor 'links/0/schema', 123 is not an object.","documentation_url":"https://docs.github.com/rest/releases/releases#create-a-release","status":"422"}`。⇒ 所有请求体改为「先写临时文件再 `-InFile`」：JSON 用 `[System.IO.File]::WriteAllText($f, $json, (New-Object System.Text.UTF8Encoding($false)))`（**不能带 BOM**），二进制直接用 `-InFile`。改完后同一份 JSON 的 POST 立刻返回 201。
  2. **`-InFile` 时必须自己设 `Content-Type`**：不带 body 的请求里 `Invoke-WebRequest` 默认发 `application/x-www-form-urlencoded`，GitHub 拒绝：`content_type can't be application/x-www-form-urlencoded`。修法是在 `-InFile` 分支里同样写 `$headers['Content-Type'] = $ContentType`；修完后用一个 38 B 的探针文件上传验证「上传字节数 = 本地字节数」。
  3. **.NET 默认走 WinINET 系统代理**：本机 `HKCU:\...\Internet Settings` 是 `ProxyEnable=1` / `ProxyServer=127.0.0.1:7890`，而那个代理**连不上 GitHub**（git 走它报 `schannel: failed to receive handshake`，`Invoke-WebRequest -Proxy http://127.0.0.1:7890` 报「无法连接到远程服务器」）。`Invoke-GhApi` 里加一行 `[System.Net.WebRequest]::DefaultWebProxy = New-Object System.Net.WebProxy` 直连即可；`curl.exe` 直连同样可用，且被用来交叉验证下载。
  4. `Add-Type -AssemblyName System.IO.Compression.FileSystem` 不够：`[System.IO.Compression.ZipArchiveMode]` 在 `System.IO.Compression` 里，两个程序集都要加载，否则报「找不到类型」。
  5. `git push` 在本机不稳：先后出现 `Failed to connect to github.com:443 after ~21 s: Could not connect to server` 与 `schannel: server closed abruptly (missing close_notify)`；用 `git -c http.version=HTTP/1.1 -c http.postBuffer=524288000 push origin main` 重试后成功（同一时段 `curl.exe` 直连 `api.github.com` 正常 ⇒ 是链路抖动，不是配置问题）。
- **提交**：`6ae6bda`「Docs: refresh README for v1.1.0 and keep fonts out of the release ZIP」（4 files, +271/−9）；随后把 `-InFile` 修复提交到 `tools\gh-api.ps1` / `tools\gh-release.ps1`。
