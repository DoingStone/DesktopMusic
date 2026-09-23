# 逐字歌词同步（word-level lyric sync）

本文记录让高亮真正跟随演唱所需的三个部件、以及实现过程中实测到的坑。

## 为什么需要逐字时间戳

LRC 只给出**每行**的开始时间，行内高亮只能靠猜（本项目原先按文字密度挑一条缓出曲线）。
结果必然：慢歌/长音会"提前跑完然后停住"。

逐字时间戳是**绝对时间**，所以：

| 场景 | 为什么自动正确 |
|---|---|
| 唱得快 | 每个字有自己的时间，切换跟着数据走 |
| 唱得慢 / 长音 | 长音就是一个 duration 很大的字，高亮停在里面 |
| 加速 / 减速 | 时间戳是绝对的，与节奏无关 |
| 停顿 | 停顿就是字与字之间的时间间隔，高亮自然停住 |

**变速和停顿从来不是难点**，难点只有一个：行内没有位置信息。

## 数据源：AMLL TTML DB

`amll-dev/amll-ttml-db`（CC0），约 69500 个文件、598 MB —— **不要 clone**。

- 路径：`qq-lyrics/<QQ歌曲mid>.ttml`、`ncm-lyrics/<网易云id>.ttml`
- QQ 的 mid 正是 `QqMusicProvider` 已经返回的标识，**不需要索引或名字匹配**
- 格式：`<p begin end>` 是行，`<span begin end>` 是字
- `<span ttm:role="x-bg">` 是背景和声层，**必须跳过**（它含嵌套 span；只取 `<p>` 的直接子元素，否则文字会重复、扫描错位）
- 元数据在 `<amll:meta key=... value=...>`：`musicName` / `artists` / `qqMusicId` / `ncmMusicId` / `isrc`

**覆盖率有限**：实测《夜曲》周杰伦（mid `001zMQr71F1Qo8`）**未被收录**（404），
《装糊涂》许嵩（`0000Dso20pNy35`）在库中。必须保留行级回退。

## CDN 可达性：一个会伪装成"没匹配"的失败

从**程序自己的 `HttpClient`** 实测（不是 curl）：

| 主机 | 结果 |
|---|---|
| `cdn.jsdelivr.net` | **间歇失败**（限流）；同一 URL 用 curl 是 200 |
| `fastly.jsdelivr.net` | 可用 |
| `gcore.jsdelivr.net` | 失败 |
| `cdn.statically.io` | 失败 |
| `api.github.com/repos/.../contents/<path>` | 可用（JSON，base64 内容） |

**被拒绝与"歌没收录"从调用方看完全一样**，所以单主机实现会静默失效。
`AmllTtmlProvider.Mirrors` 是三级回退链（fastly → cdn → GitHub contents API），
`ExtractTtml` 同时接受原始 XML 和 API 的 base64 JSON 包装。

环境注记：`raw.githubusercontent.com` 与 `codeload.github.com` 在此环境超时；
PowerShell 5.1 的 `Invoke-WebRequest` 对 jsDelivr 有 TLS 问题，用 `curl.exe`。

## 数据源二：网易云 YRC（覆盖率远大于 AMLL）

**逐字时间一直都有，只是我们没要。** 网易云的歌词接口：

| 请求参数 | `klyric` | `yrc` |
|---|---|---|
| `lv=-1&kv=-1&tv=-1` | 0 | **字段不存在** |
| `lv=-1&kv=-1&tv=-1&yv=-1` | 0 | **6518 字符** ✓ |

之前只请求 `lrc`/`tlyric`，所以永远拿不到逐字；而试过的 `klyric`（旧版逐字字段）本就是空的
—— **把"klyric 为空"误判成了"网易云没有逐字"**。

格式（注意与 QRC 的差别）：

```
[17630,3810](17630,210,0)字(17840,360,0)字(18200,130,0)字
 │              └── 逐字是【三个】字段（起始ms, 时长ms, 标志）
 └── 行是 [起始ms, 时长ms]，不是 LRC 的 [mm:ss.xx]
```

因为字段数不同，`LrcParser` 的 QRC 正则**匹配不上**，所以 `YrcParser` 是独立实现而非改正则。

实测《装糊涂》许嵩：41 行 / **401 个逐字** / 0 越界。与 AMLL 同曲对照，逐字时间基本吻合
（除 17.630 vs 17.669），说明两个来源都可靠。

## 解析器：逐字优先于分数

`LyricResolver` 原来按分数降序取**第一个非空**结果。加了 yrc 之后这会出问题：

- 网易云搜索分 **100**，AMLL 是 **96**
- 当 AMLL 有逐字、而网易云这首歌**恰好没有 yrc** 时，行级结果会压过逐字结果，**白白丢掉更好的高亮**

改为：**逐字结果直接采纳；行级结果先存为回退，继续在剩余候选里找逐字**，
上限 `FallbackSearchLimit = 4` 次抓取（否则没有逐字源时只会徒增延迟）。

## 时钟：`PlaybackClock`

SMTC 约**每秒**才给一次位置，两次之间靠外推。两个缺陷：

1. **速率不可信** —— 不要用 `TimelineProperties.PlaybackRate`（改了倍速也常是 1.0）。
   改为用两次**不同**采样的 `Δ位置 / Δ(PositionUpdatedAt)` 反推，仅在锚点间隔 ≥250ms、
   速率落在 0.25..4.0 时采纳；否则回退到上报速率，再回退到 1.0。
2. **校正是跳变** —— 新采样与预测差 ~80ms 时会瞬跳，肉眼可见。
   现在把差值作为偏移量在 **300ms** 内线性衰减到 0，轮询瞬间值连续。
   偏差 ≥ **1.75 秒**判定为拖动/换歌，立即对齐并丢弃实测速率。

`PlaybackSnapshot.ExtrapolatedPosition` **刻意保持不变** —— CLI 和其断言依赖它的确切行为。

## 修复：`LyricDocument.FillEndTimes` 截断逐字

原实现**无条件**把每行的 `End` 覆盖为下一行的 `Start`。对 LRC 正确（行本无结束时间），
但有逐字时间后是错的：**对唱时下一行可能在本行结束前就开始**，本行被截断，
尾部逐字落到行外 —— 渲染器**永远不会点亮它们**。真实文件实测丢 3 个逐字。

现在有逐字的行保留自己的 `End`（`p@end`，且不低于最后一个逐字的结束）。

## 验证方法（可复用）

`SettingsSelfTest.VerifySweepFollowsThePosition` 在无头环境跑**真实管道**：
内联 TTML → `TtmlParser.Parse` → `OverlayWindow.ComputeProgress`（为此改为 `internal`）
→ `KaraokeLine` → `RenderTargetBitmap` → 测量最右侧 DeepSkyBlue 像素。

四种场景用具体数字断言：

| 场景 | 断言 |
|---|---|
| 长音 2000ms | t=1000ms → 边界 44%（约一半） |
| 快歌 8×100ms | t=350ms → 43%（期望 37.5%） |
| 变速（长音后接快字） | t=1100ms → 17%（均匀分布会说 55%）；t=1650ms → 37%（均匀会说 82%） |
| 停顿 400ms 后隔 800ms | 间隙中保持 48%（两个字里的一个），不向前爬 |

### 写这个测试时踩过的两个坑（都在测试里，不在产品里）

1. **比例必须相对文本自身的墨迹范围**，不是控件宽度。行是**居中**的，
   用绝对 x 作分母会把四分之一扫过的行报成 **76%** —— 一个看起来很合理的数字，能掩盖真回归。
2. `$"{ms/1000.0:000}"` 是**整数**格式：2000ms 会格式化成 `002`，
   于是时钟值变成 `00:00.002`（2 毫秒）。用 `TimeSpan.ToString(@"mm\:ss\.fff")`。

## 构建注意

- `dotnet build` 打 `.sln` 会在 **Restore** 目标失败（NuGet 不可达）；按项目加 `--no-restore` 构建
- 应用运行时会锁住 `publish/TaskbarLyrics/TaskbarLyrics.dll`，`dotnet publish` 报 MSB3027/MSB3021
  —— 先停掉 `TaskbarLyrics` 进程

## 尚未验证

**没有在真实播放中验证过。** 此环境无媒体会话，所以应用接线只经过编译与单元/像素测试，
未观察过真实歌曲播放时高亮跟随的效果。时钟的调参值（300ms 窗口、1.75 秒判定）
是推理所得，未针对真实 SMTC 抖动调优。
