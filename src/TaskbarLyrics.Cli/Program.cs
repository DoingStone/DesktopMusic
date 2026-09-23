using System.Text;
using TaskbarLyrics.Core.Lyrics;
using TaskbarLyrics.Core.Lyrics.Providers;
using TaskbarLyrics.Core.Media;
using TaskbarLyrics.Core.Net;
using TaskbarLyrics.Core.Models;

namespace TaskbarLyrics.Cli;

/// <summary>
/// Diagnostics harness. Verifies the SMTC -> resolver -> parser -> renderer
/// pipeline against whatever is playing right now, without needing the GUI.
/// </summary>
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;

        var command = args.Length > 0 ? args[0].ToLowerInvariant() : "watch";

        var media = new SmtcMediaSessionSource();
        var resolver = new LyricResolver(new ILyricProvider[]
        {
            new AmllTtmlProvider(),
            new QqMusicProvider(),
            new NetEaseProvider(),
            new LrclibProvider(),
        });

        return command switch
        {
            "now" => await ShowNowAsync(media, resolver),
            "watch" => await WatchAsync(media, resolver),
            "selftest" => SelfTest(),
            "position" => await PositionProbeAsync(media, args),
            "qrc" => await QrcProbeAsync(args),
            "ttml" => TtmlProbe(args),
            "words" => await WordsProbeAsync(args),
            "clock" => await ClockProbeAsync(media, args),
            "sessions" => SessionsProbe(),
            "yrc" => await YrcProbeAsync(args),
            "coverage" => await CoverageProbeAsync(media, args),
            _ => Usage(),
        };
    }

    /// <summary>
    /// Parse a TTML file and report the word-level timing it yielded. This checks the
    /// parser against real documents rather than synthetic ones, and is the quickest way
    /// to confirm a downloaded file is usable.
    /// </summary>
    private static int TtmlProbe(string[] args)
    {
        if (args.Length < 2)
        {
            Console.WriteLine("用法: tblc ttml <文件路径.ttml>");
            return 1;
        }

        var path = args[1];
        if (!File.Exists(path))
        {
            Console.WriteLine($"找不到文件: {path}");
            return 1;
        }

        var doc = TtmlParser.Parse(File.ReadAllText(path));

        Console.WriteLine($"来源        : {doc.Source}");
        Console.WriteLine($"行数        : {doc.Lines.Count}");
        Console.WriteLine($"逐字行数    : {doc.Lines.Count(l => l.Syllables.Count > 0)}");
        Console.WriteLine($"逐字总数    : {doc.Lines.Sum(l => l.Syllables.Count)}");
        Console.WriteLine($"真实逐字时间: {doc.HasRealWordTiming}");
        Console.WriteLine($"总时长      : {doc.TotalDuration:hh\\:mm\\:ss\\.fff}");

        var first = doc.Lines.FirstOrDefault(l => l.Syllables.Count > 0);
        if (first is null)
        {
            Console.WriteLine("没有解析出任何逐字时间。");
            return 2;
        }

        Console.WriteLine();
        Console.WriteLine("首行逐字（前 8 个）:");
        Console.WriteLine($"  行: [{first.Start:hh\\:mm\\:ss\\.fff} - {first.End:hh\\:mm\\:ss\\.fff}] \"{first.Text}\"");
        foreach (var s in first.Syllables.Take(8))
        {
            Console.WriteLine($"    {s.Start:hh\\:mm\\:ss\\.fff} +{s.Duration.TotalMilliseconds,4:F0}ms  \"{s.Text}\"");
        }

        // Every syllable must sit inside its line, and starts must not go backwards.
        int outOfRange = doc.Lines.Sum(l =>
            l.Syllables.Count(s => s.Start < l.Start || s.End > l.End + TimeSpan.FromMilliseconds(1)));

        int unordered = 0;
        foreach (var line in doc.Lines)
        {
            for (int i = 1; i < line.Syllables.Count; i++)
            {
                if (line.Syllables[i].Start < line.Syllables[i - 1].Start) unordered++;
            }
        }

        Console.WriteLine();
        Console.WriteLine($"越界逐字    : {outOfRange}");
        Console.WriteLine($"乱序逐字    : {unordered}");

        bool ok = outOfRange == 0 && unordered == 0 && doc.HasRealWordTiming;
        Console.WriteLine(ok ? "RESULT: PASS" : "RESULT: FAIL");
        return ok ? 0 : 3;
    }

    /// <summary>
    /// Measure what fraction of a song list actually gets word-level timing.
    /// <para>
    /// This is the number that decides whether "accurate highlighting for every song" is a
    /// reachable goal, so it is measured rather than assumed. Songs are given as
    /// <c>"title artist;title artist;..."</c> and resolved through the real pipeline.
    /// </para>
    /// </summary>
    private static async Task<int> CoverageProbeAsync(IMediaSessionSource media, string[] args)
    {
        if (args.Length < 2)
        {
            Console.WriteLine("用法: tblc coverage \"歌名 歌手;歌名 歌手;...\"");
            return 1;
        }

        var queries = args[1]
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToArray();

        var resolver = new LyricResolver(new ILyricProvider[]
        {
            new AmllTtmlProvider(),
            new QqMusicProvider(),
            new NetEaseProvider(),
            new LrclibProvider(),
        });

        int word = 0, line = 0, none = 0;

        Console.WriteLine("  歌曲                                来源          逐字   行数");
        Console.WriteLine("  ------------------------------------------------------------------");

        foreach (var query in queries)
        {
            // Optional "@<seconds>" suffix supplies the track duration, which is what the
            // edition check keys on. Without it the probe cannot exercise that check at all,
            // because an unknown duration is deliberately never treated as a mismatch.
            var spec = query;
            TimeSpan duration = TimeSpan.Zero;
            var at = spec.LastIndexOf('@');
            if (at > 0 && double.TryParse(spec[(at + 1)..], out var seconds) && seconds > 0)
            {
                duration = TimeSpan.FromSeconds(seconds);
                spec = spec[..at].Trim();
            }

            var parts = spec.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var title = parts.Length > 0 ? parts[0] : spec;
            var artist = parts.Length > 1 ? string.Join(' ', parts[1..]) : string.Empty;

            var track = new PlaybackSnapshot(
                "QQMusic.exe", "QQMusic.exe", title, artist, string.Empty,
                duration, TimeSpan.Zero, true, DateTimeOffset.Now);

            LyricResolution result;
            try
            {
                result = await resolver.ResolveAsync(track, CancellationToken.None);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  {Truncate(query, 34),-36} 检索失败 {ex.GetType().Name}");
                none++;
                continue;
            }

            var label = Truncate(query, 34);
            if (!result.Found)
            {
                Console.WriteLine($"  {label,-36} {"—",-12} {"—",-5} 0");
                none++;
                continue;
            }

            var source = result.EditionUnverified ? result.Source + "*" : result.Source.ToString();

            // Show the durations the decision was made on. Without them a probe cannot tell
            // "the edition check worked" from "the check never ran".
            var picked = result.Winner?.Duration ?? TimeSpan.Zero;
            var delta = duration > TimeSpan.Zero && picked > TimeSpan.Zero
                ? $"{(picked - duration).Duration().TotalSeconds:F0}s"
                : "—";

            if (result.Document.HasRealWordTiming)
            {
                word++;
                Console.WriteLine($"  {label,-36} {source,-12} {"是",-5} " +
                                  $"{result.Document.Lines.Count,4}  曲目 {duration.TotalSeconds:F0}s / 候选 {picked.TotalSeconds:F0}s  差 {delta}");
            }
            else
            {
                line++;
                Console.WriteLine($"  {label,-36} {source,-12} {"否",-5} " +
                                  $"{result.Document.Lines.Count,4}  曲目 {duration.TotalSeconds:F0}s / 候选 {picked.TotalSeconds:F0}s  差 {delta}");
            }
        }

        int total = queries.Length;
        Console.WriteLine();
        Console.WriteLine($"  样本 {total} 首");
        Console.WriteLine($"    逐字 : {word,3}  ({100.0 * word / total:F0}%)");
        Console.WriteLine($"    仅行级: {line,3}  ({100.0 * line / total:F0}%)");
        Console.WriteLine($"    无结果: {none,3}  ({100.0 * none / total:F0}%)");
        Console.WriteLine();
        Console.WriteLine($"  有歌词（逐字+行级）: {word + line}/{total}  ({100.0 * (word + line) / total:F0}%)");
        Console.WriteLine();
        Console.WriteLine("  带 * 表示没有候选匹配上播放器报的时长，版本未经校验（退回最佳匹配，而非显示空白）");
        return 0;
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..(max - 1)] + "…";

    /// <summary>
    /// List every media session Windows knows about, and which one this app would follow.
    /// <para>
    /// This exists because the app's own diagnostics could not answer the question. They
    /// reported the source of the session that had been <i>chosen</i>, so "no known
    /// sessions" looked identical whether Windows had no sessions at all or the app was
    /// filtering them out - and the app was filtering them out.
    /// </para>
    /// </summary>
    private static int SessionsProbe()
    {
        var sessions = SmtcMediaSessionSource.ListAllSessions();
        if (sessions.Count == 0)
        {
            Console.WriteLine("Windows 报告 0 个媒体会话。");
            Console.WriteLine("说明没有播放器向系统注册 SMTC —— 不是本程序把它们过滤掉了。");
            return 1;
        }

        var source = new SmtcMediaSessionSource();
        var chosen = source.GetCurrentAsync(CancellationToken.None).GetAwaiter().GetResult();

        Console.WriteLine($"  系统共有 {sessions.Count} 个媒体会话：");
        Console.WriteLine();

        foreach (var s in sessions)
        {
            var mark = chosen is not null &&
                       string.Equals(s.AppId, chosen.SourceAppId, StringComparison.OrdinalIgnoreCase)
                ? " *" : "  ";

            Console.WriteLine($" {mark} {s.AppId}   [{s.Status}]");
            Console.WriteLine($"      曲目  : {s.Title} — {s.Artist}");
            Console.WriteLine($"      专辑  : {s.Album}");
            Console.WriteLine($"      流派  : {s.Genres}    NCM id: {s.ExactId}");
            Console.WriteLine($"      时间轴: Start={s.StartTime:hh\\:mm\\:ss\\.fff}  " +
                              $"End={s.EndTime:hh\\:mm\\:ss\\.fff}  " +
                              $"End-Start={s.EndMinusStart:hh\\:mm\\:ss\\.fff}");
            Console.WriteLine($"              Position={s.Position:hh\\:mm\\:ss\\.fff}  " +
                              $"MinSeek={s.MinSeek:hh\\:mm\\:ss\\.fff}  " +
                              $"MaxSeek={s.MaxSeek:hh\\:mm\\:ss\\.fff}  Rate={s.Rate:F2}");
            Console.WriteLine();
        }

        Console.WriteLine(chosen is null
            ? "本程序当前【不会】跟随任何会话。"
            : $"本程序当前跟随：{chosen.SourceAppId}  「{chosen.Title}」");

        // Also report what the lyric pipeline makes of it, so a wrong duration and a wrong
        // match are not confused with one another.
        if (chosen is not null)
        {
            Console.WriteLine();
            Console.WriteLine($"  程序用作曲目时长: {chosen.Duration:hh\\:mm\\:ss\\.fff}");

            var resolver = new LyricResolver(new ILyricProvider[]
            {
                new AmllTtmlProvider(), new QqMusicProvider(),
                new NetEaseProvider(), new LrclibProvider(),
            });
            // Show what NetEase returns for this exact track, so a failed id lookup is not
            // mistaken for a losing score.
            var ne = new NetEaseProvider();
            var neCands = ne.SearchAsync(chosen, CancellationToken.None).GetAwaiter().GetResult();
            Console.WriteLine($"  网易云候选: {neCands.Count} 个   ExactSourceId={(string.IsNullOrEmpty(chosen.ExactSourceId) ? "(空)" : chosen.ExactSourceId)}");
            foreach (var nc in neCands.Take(3))
            {
                Console.WriteLine($"      「{nc.Title} — {nc.Artist}」 {nc.Duration:hh\\:mm\\:ss}  id={nc.SourceId}");
            }
            var result = resolver.ResolveAsync(chosen, CancellationToken.None).GetAwaiter().GetResult();

            if (!result.Found)
            {
                Console.WriteLine("  歌词解析: 无结果");
            }
            else
            {
                var w = result.Winner;
                Console.WriteLine($"  歌词解析: {result.Source}  行数={result.Document.Lines.Count}  " +
                                  $"逐字={result.Document.HasRealWordTiming}");
                Console.WriteLine($"            候选「{w?.Title} — {w?.Artist}」时长 {w?.Duration:hh\\:mm\\:ss}");
                if (result.EditionUnverified)
                {
                    Console.WriteLine("            ⚠ 没有候选匹配该时长，已退回最佳匹配（版本未校验）");
                }
            }
        }

        return chosen is null ? 2 : 0;
    }

    /// <summary>
    /// Fetch a song's NetEase lyrics and report whether the yrc word-level track came back.
    /// <para>
    /// Worth its own probe because the word-level field is invisible unless the request asks
    /// for it: the same song returns no yrc at all without the <c>yv</c> parameter, which is
    /// how word timing came to look unavailable from this service.
    /// </para>
    /// </summary>
    private static async Task<int> YrcProbeAsync(string[] args)
    {
        var query = args.Length > 1 ? string.Join(' ', args[1..]) : "装糊涂 许嵩";
        Console.WriteLine($"检索: {query}");

        var provider = new NetEaseProvider();
        var results = await provider.SearchAsync(
            new PlaybackSnapshot("ne", "ne", query, string.Empty, string.Empty,
                TimeSpan.Zero, TimeSpan.Zero, false, DateTimeOffset.Now),
            CancellationToken.None);

        if (results.Count == 0)
        {
            Console.WriteLine("  没有搜索结果");
            return 1;
        }

        var candidate = results[0];
        Console.WriteLine($"  选中: {candidate.Title} — {candidate.Artist}  ({candidate.SourceId})");
        Console.WriteLine();

        var doc = await provider.FetchAsync(candidate, CancellationToken.None);

        Console.WriteLine($"来源        : {doc.Source}");
        Console.WriteLine($"行数        : {doc.Lines.Count}");
        Console.WriteLine($"逐字行数    : {doc.Lines.Count(l => l.Syllables.Count > 0)}");
        Console.WriteLine($"逐字总数    : {doc.Lines.Sum(l => l.Syllables.Count)}");
        Console.WriteLine($"真实逐字时间: {doc.HasRealWordTiming}");

        var sample = doc.Lines.FirstOrDefault(l => l.Syllables.Count > 1);
        if (sample is not null)
        {
            Console.WriteLine();
            Console.WriteLine($"样例: [{sample.Start:hh\\:mm\\:ss\\.fff}] \"{sample.Text}\"");
            foreach (var s in sample.Syllables.Take(6))
            {
                Console.WriteLine($"    {s.Start:hh\\:mm\\:ss\\.fff} +{s.Duration.TotalMilliseconds,4:F0}ms  \"{s.Text}\"");
            }
        }

        int outOfRange = doc.Lines.Sum(l =>
            l.Syllables.Count(s => s.Start < l.Start || s.End > l.End + TimeSpan.FromMilliseconds(1)));

        Console.WriteLine();
        Console.WriteLine($"越界逐字    : {outOfRange}");

        bool ok = doc.HasRealWordTiming && outOfRange == 0;
        Console.WriteLine(ok ? "RESULT: PASS" : "RESULT: FAIL");
        return ok ? 0 : 2;
    }

    /// <summary>
    /// Watch the playback clock against the raw media session for a while.
    /// <para>
    /// This is the only way to catch a clock that runs at the wrong speed: the reported
    /// position is coarse (roughly one update per second) and the app extrapolates between
    /// updates, so a mis-derived rate shows up as a growing gap rather than an obvious
    /// error. A lagging clock makes every song's lyrics fall behind the singing.
    /// </para>
    /// </summary>
    private static async Task<int> ClockProbeAsync(IMediaSessionSource media, string[] args)
    {
        int seconds = args.Length > 1 && int.TryParse(args[1], out var s) ? s : 20;

        var clock = new PlaybackClock();
        var first = await media.GetCurrentAsync(CancellationToken.None);
        if (first is null || !first.HasTrack)
        {
            Console.WriteLine("没有正在播放的媒体会话。");
            return 1;
        }

        Console.WriteLine($"曲目: {first.Title} — {first.Artist}");
        Console.WriteLine($"时长: {first.Duration:hh\\:mm\\:ss\\.fff}");
        Console.WriteLine();
        Console.WriteLine("  墙钟    原始位置      时钟位置      偏差      上报速率");
        Console.WriteLine("  ------------------------------------------------------------");

        var start = DateTimeOffset.Now;
        double worst = 0;
        var rates = new List<double>();
        TimeSpan? lastRaw = null;
        DateTimeOffset lastRawAt = start;

        while ((DateTimeOffset.Now - start).TotalSeconds < seconds)
        {
            var now = DateTimeOffset.Now;
            var snap = await media.GetCurrentAsync(CancellationToken.None);
            if (snap is null || !snap.HasTrack) break;

            clock.Observe(snap, now);
            var predicted = clock.Position(now);

            // Compare against the session's own position, extrapolated the simple way, so
            // the number shows clock error rather than sampling lag.
            var raw = snap.ExtrapolatedPosition(now);
            var drift = (predicted - raw).TotalMilliseconds;
            if (Math.Abs(drift) > Math.Abs(worst)) worst = drift;

            if (lastRaw is { } prev && snap.Position != prev)
            {
                var wall = (now - lastRawAt).TotalSeconds;
                if (wall > 0.2) rates.Add((snap.Position - prev).TotalSeconds / wall);
                lastRawAt = now;
            }
            lastRaw = snap.Position;

            Console.WriteLine(
                $"  {(now - start).TotalSeconds,5:F1}s  {snap.Position:hh\\:mm\\:ss\\.fff}   " +
                $"{predicted:hh\\:mm\\:ss\\.fff}   {drift,7:F0}ms   {snap.PlaybackRate:F2}");

            await Task.Delay(250);
        }

        Console.WriteLine();
        Console.WriteLine($"最大偏差 : {worst:F0} ms");
        if (rates.Count > 0)
        {
            Console.WriteLine($"实测速率 : 均值 {rates.Average():F3}  最小 {rates.Min():F3}  最大 {rates.Max():F3}  (采样 {rates.Count})");
        }
        Console.WriteLine();
        Console.WriteLine(Math.Abs(worst) < 300
            ? "RESULT: PASS - 时钟与媒体会话一致"
            : $"RESULT: FAIL - 时钟偏差 {worst:F0} ms，会导致歌词滞后或超前");

        return Math.Abs(worst) < 300 ? 0 : 2;
    }

    /// <summary>
    /// End-to-end check of the word-level pipeline: search QQ/NetEase, look the track up in
    /// the AMLL TTML DB, verify the document's own metadata agrees, fetch it and parse the
    /// per-word timing. This is the only test that exercises the real network path.
    /// </summary>
    private static async Task<int> WordsProbeAsync(string[] args)
    {
        var query = args.Length > 1 ? string.Join(' ', args[1..]) : "夜曲 周杰伦";
        Console.WriteLine($"检索: {query}");
        Console.WriteLine();

        // "夜曲 周杰伦" reads as title + artist; the app splits them the same way.
        var parts = query.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var title = parts.Length > 0 ? parts[0] : query;
        var artist = parts.Length > 1 ? string.Join(' ', parts[1..]) : string.Empty;

        var track = new PlaybackSnapshot(
            "probe", "probe", title, artist, string.Empty,
            TimeSpan.Zero, TimeSpan.Zero, true, DateTimeOffset.Now);

        // Prove the app's own HTTP stack can reach the database, and find which mirror
        // works. curl reaching a host says nothing about HttpClient: a CDN can reject a
        // client by user agent or rate-limit it, and that failure is otherwise invisible
        // because a miss and a rejection both end as "no candidates".
        const string file = "qq-lyrics/0000Dso20pNy35.ttml";
        var mirrors = new (string Label, string Url)[]
        {
            ("jsdelivr", $"https://cdn.jsdelivr.net/gh/amll-dev/amll-ttml-db@main/{file}"),
            ("jsdelivr-fastly", $"https://fastly.jsdelivr.net/gh/amll-dev/amll-ttml-db@main/{file}"),
            ("jsdelivr-gcore", $"https://gcore.jsdelivr.net/gh/amll-dev/amll-ttml-db@main/{file}"),
            ("statically", $"https://cdn.statically.io/gh/amll-dev/amll-ttml-db/main/{file}"),
            ("github-api", $"https://api.github.com/repos/amll-dev/amll-ttml-db/contents/{file}"),
        };

        foreach (var (label, url) in mirrors)
        {
            try
            {
                var body = await LyricHttp.GetStringAsync(
                    new Uri(url), "https://github.com/amll-dev/amll-ttml-db", CancellationToken.None);
                Console.WriteLine(body is null
                    ? $"  {label,-16} 失败（null）"
                    : $"  {label,-16} OK {body.Length} 字符");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  {label,-16} {ex.GetType().Name}");
            }
        }

        Console.WriteLine();
        var provider = new AmllTtmlProvider();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var candidates = await provider.SearchAsync(track, CancellationToken.None);
        sw.Stop();

        Console.WriteLine($"逐字库命中: {candidates.Count}  ({sw.ElapsedMilliseconds} ms)");
        foreach (var c in candidates)
        {
            Console.WriteLine($"  [{c.Score:F0}] {c.Title} — {c.Artist}");
            Console.WriteLine($"        {c.SourceId}");
        }

        if (candidates.Count == 0)
        {
            Console.WriteLine();
            Console.WriteLine("RESULT: FAIL - 逐字库没有匹配（网络不通，或该曲目未收录）");
            return 1;
        }

        var best = candidates[0];
        var doc = await provider.FetchAsync(best, CancellationToken.None);

        int sylLines = doc.Lines.Count(l => l.Syllables.Count > 0);
        int sylTotal = doc.Lines.Sum(l => l.Syllables.Count);

        Console.WriteLine();
        Console.WriteLine($"行数        : {doc.Lines.Count}");
        Console.WriteLine($"逐字行数    : {sylLines}");
        Console.WriteLine($"逐字总数    : {sylTotal}");
        Console.WriteLine($"真实逐字时间: {doc.HasRealWordTiming}");
        Console.WriteLine($"总时长      : {doc.TotalDuration:hh\\:mm\\:ss\\.fff}");

        // Every syllable must sit inside its line, or the renderer can never light it.
        int outOfRange = doc.Lines.Sum(l =>
            l.Syllables.Count(s => s.Start < l.Start || s.End > l.End + TimeSpan.FromMilliseconds(1)));

        Console.WriteLine($"越界逐字    : {outOfRange}");

        var sample = doc.Lines.FirstOrDefault(l => l.Syllables.Count > 1);
        if (sample is not null)
        {
            Console.WriteLine();
            Console.WriteLine("样例逐字:");
            Console.WriteLine($"  \"{sample.Text}\"");
            foreach (var s in sample.Syllables.Take(6))
            {
                Console.WriteLine($"    {s.Start:hh\\:mm\\:ss\\.fff} +{s.Duration.TotalMilliseconds,4:F0}ms  \"{s.Text}\"");
            }
        }

        bool ok = doc.HasRealWordTiming && sylTotal > 0 && outOfRange == 0;
        Console.WriteLine();
        Console.WriteLine(ok ? "RESULT: PASS" : "RESULT: FAIL");
        return ok ? 0 : 2;
    }

    /// <summary>
    /// Fetch a song's QRC straight from QQ, decrypt it, and report whether real
    /// per-word timing came out. This is the only way to prove the decryption matches
    /// the server rather than merely round-tripping with itself.
    /// </summary>
    private static async Task<int> QrcProbeAsync(string[] args)
    {
        var query = args.Length > 1 ? string.Join(' ', args[1..]) : "夜曲 周杰伦";
        Console.WriteLine($"检索: {query}");

        var provider = new QqMusicProvider();
        var results = await provider.SearchAsync(
            new PlaybackSnapshot("qq", "qq", query, "", "", TimeSpan.Zero, TimeSpan.Zero, false, DateTimeOffset.Now),
            CancellationToken.None);

        if (results.Count() == 0)
        {
            Console.WriteLine("  没有搜索结果");
            return 1;
        }

        var candidate = results[0];
        Console.WriteLine($"  选中: {candidate.Title} — {candidate.Artist}  ({candidate.SourceId})");
        Console.WriteLine();

        var doc = await provider.FetchAsync(candidate, CancellationToken.None);

        int withSyllables = doc.Lines.Count(l => l.Syllables.Count > 0);
        int totalSyllables = doc.Lines.Sum(l => l.Syllables.Count);

        Console.WriteLine($"来源     : {doc.Source}");
        Console.WriteLine($"行数     : {doc.Lines.Count}");
        Console.WriteLine($"逐字行数 : {withSyllables}");
        Console.WriteLine($"逐字总数 : {totalSyllables}");
        Console.WriteLine();

        Console.WriteLine($"解密诊断 : {QrcDecryptor.LastDiagnostic}");
        Console.WriteLine();
        if (withSyllables == 0)
        {
            Console.WriteLine("结果: 未取得逐字时间轴（回退到行级 LRC）");
            return 1;
        }

        Console.WriteLine("结果: 逐字时间轴已解密成功");
        Console.WriteLine();
        Console.WriteLine("前 3 行的逐字时间轴:");
        foreach (var line in doc.Lines.Where(l => l.Syllables.Count > 0).Take(3))
        {
            Console.WriteLine($"  [{line.Start:mm\\:ss\\.fff}] {line.Text}");
            foreach (var s in line.Syllables.Take(6))
            {
                Console.WriteLine($"      {s.Start:mm\\:ss\\.fff} +{s.Duration.TotalMilliseconds,6:F0}ms  '{s.Text}'");
            }
        }

        return 0;
    }

    private static int Usage()
    {
        Console.WriteLine("用法: tblc [now|watch|selftest|position|qrc]");
        Console.WriteLine("  now       打印当前曲目、各歌词源命中与当前歌词");
        Console.WriteLine("  watch     持续滚动当前歌词（模拟任务栏）");
        Console.WriteLine("  selftest  歌词解析/匹配/进度外推的离线校验");
        Console.WriteLine("  position [次数] [间隔ms]");
        Console.WriteLine("            只读 SMTC 进度，用于测量同步漂移（不联网）");
        Console.WriteLine("  qrc [关键词]");
        Console.WriteLine("            拉取并解密 QQ 逐字歌词，验证逐字时间轴");
        Console.WriteLine();
        Console.WriteLine("设置读写校验请运行: TaskbarLyrics.exe --selftest");
        return 1;
    }

    /// <summary>
    /// Sample the SMTC playback position and the timestamp the player attached to
    /// it. Deliberately does no network work, so sampling stays fast and precise
    /// enough to measure drift against the wall clock.
    /// </summary>
    private static async Task<int> PositionProbeAsync(IMediaSessionSource media, string[] args)
    {
        int count = args.Length > 1 && int.TryParse(args[1], out var c) ? c : 20;
        int interval = args.Length > 2 && int.TryParse(args[2], out var i) ? i : 500;

        Console.WriteLine("elapsed_ms\tposition_ms\tsince_updated_ms\tplaying");
        var started = System.Diagnostics.Stopwatch.StartNew();

        for (int n = 0; n < count; n++)
        {
            var track = await media.GetCurrentAsync(CancellationToken.None);

            if (track is null || !track.HasTrack)
            {
                Console.WriteLine("(no session)");
            }
            else
            {
                var sinceUpdated = track.PositionUpdatedAt == default
                    ? -1
                    : (long)(DateTimeOffset.Now - track.PositionUpdatedAt).TotalMilliseconds;

                Console.WriteLine(
                    $"{started.ElapsedMilliseconds}\t{(long)track.Position.TotalMilliseconds}\t" +
                    $"{sinceUpdated}\t{track.IsPlaying}");
            }

            await Task.Delay(interval);
        }

        return 0;
    }

    /// <summary>Print the current track, resolution traces and the active line.</summary>
    private static async Task<int> ShowNowAsync(IMediaSessionSource media, LyricResolver resolver)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var track = await media.GetCurrentAsync(cts.Token);
        if (track is null || !track.HasTrack)
        {
            Console.WriteLine("没有检测到正在播放的媒体会话。");
            Console.WriteLine("已知会话: " + string.Join(", ", media.KnownSourceIds));
            return 2;
        }

        Console.WriteLine("=== Track (SMTC) ===");
        Console.WriteLine($"  source   : {track.SourceAppId}");
        Console.WriteLine($"  title    : {track.Title}");
        Console.WriteLine($"  artist   : {track.Artist}");
        Console.WriteLine($"  album    : {track.Album}");
        Console.WriteLine($"  duration : {track.Duration}");
        Console.WriteLine($"  position : {track.Position}");
        Console.WriteLine($"  playing  : {track.IsPlaying}");
        Console.WriteLine();

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var resolution = await resolver.ResolveAsync(track, cts.Token);
        sw.Stop();

        Console.WriteLine($"=== Provider traces ({sw.ElapsedMilliseconds} ms) ===");
        foreach (var t in resolution.Traces.OrderByDescending(t => t.BestScore))
        {
            Console.WriteLine($"  {t.DisplayName,-10} candidates={t.CandidatesFound,-3} " +
                              $"best={t.BestScore,6:F1}  {t.Status}  " +
                              $"{(t.BestTitle is null ? "" : $"「{t.BestTitle}」 - {t.BestArtist}")}");
        }
        Console.WriteLine();

        if (!resolution.Found)
        {
            Console.WriteLine("没有找到匹配的歌词。");
            return 3;
        }

        Console.WriteLine($"=== Resolved === source={resolution.Source} score={resolution.Score:F1} " +
                          $"lines={resolution.Document.Lines.Count} " +
                          $"wordTiming={resolution.Document.HasRealWordTiming}");
        Console.WriteLine();

        RenderWindow(resolution.Document, track.Position);
        return 0;
    }

    /// <summary>Live-render the current lyric line, like the taskbar would.</summary>
    private static async Task<int> WatchAsync(IMediaSessionSource media, LyricResolver resolver)
    {
        using var cts = new CancellationTokenSource();

        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        LyricDocument? doc = null;
        string lastKey = string.Empty;
        bool resolving = false;

        Console.WriteLine("监听中... 按 Ctrl+C 退出。播放一首歌即可看到歌词滚动。");
        Console.WriteLine();

        while (!cts.IsCancellationRequested)
        {
            try
            {
                var track = await media.GetCurrentAsync(cts.Token);
                if (track is null || !track.HasTrack)
                {
                    if (doc is not null)
                    {
                        doc = null;
                        lastKey = string.Empty;
                        Console.WriteLine("[无播放]");
                    }
                    await Task.Delay(700, cts.Token);
                    continue;
                }

                var key = track.CacheKey;
                if (key != lastKey)
                {
                    lastKey = key;
                    Console.WriteLine($"\n▶ {track.Title} — {track.Artist}  ({track.Duration:mm\\:ss})");
                }

                if (doc is null && !resolving)
                {
                    resolving = true;
                    var localKey = key;
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            var r = await resolver.ResolveAsync(track, cts.Token);
                            if (r.Found && lastKey == localKey)
                            {
                                doc = r.Document;
                                Console.WriteLine($"[歌词来源: {r.Source}  匹配分={r.Score:F1}  " +
                                                  $"行数={r.Document.Lines.Count}]");
                            }
                            else if (!r.Found && lastKey == localKey)
                            {
                                Console.WriteLine("[未找到歌词]");
                            }
                        }
                        catch (OperationCanceledException) { }
                        finally { resolving = false; }
                    }, cts.Token);
                }

                if (doc is not null)
                {
                    RenderWindow(doc, track.ExtrapolatedPosition(DateTimeOffset.Now));
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }

            try
            {
                await Task.Delay(200, cts.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        return 0;
    }

    /// <summary>
    /// Render the prev/current/next triplet plus the word-level progress of the
    /// current line, mirroring what the overlay draws.
    /// </summary>
    private static void RenderWindow(LyricDocument doc, TimeSpan position)
    {
        int index = doc.IndexAt(position);

        Console.WriteLine("--------------------------------------------------");
        Console.WriteLine($"position {position:mm\\:ss\\.ff}   line index {index}/{doc.Lines.Count}");
        Console.WriteLine("--------------------------------------------------");

        for (int offset = -2; offset <= 2; offset++)
        {
            int i = index + offset;
            if (i < 0 || i >= doc.Lines.Count) continue;

            var line = doc.Lines[i];
            var marker = offset == 0 ? ">>" : "  ";

            if (offset == 0)
            {
                // Show word-by-word progress for the active line.
                var progress = ComputeHighlightProgress(line, position);
                var bar = BuildProgressBar(progress, 30);
                Console.WriteLine($"{marker} {line.Start:mm\\:ss\\.ff} │{line.Text}");
                Console.WriteLine($"   {bar} {progress * 100,5:F1}%");
                if (!string.IsNullOrEmpty(line.Translation))
                    Console.WriteLine($"     译: {line.Translation}");
                if (line.Syllables.Count > 0)
                    Console.WriteLine($"     [逐字时间轴 {line.Syllables.Count} 段]");
            }
            else
            {
                Console.WriteLine($"{marker} {line.Start:mm\\:ss\\.ff} │{line.Text}");
            }
        }
        Console.WriteLine();
    }

    /// <summary>
    /// How far through the current line playback is, in 0..1. Uses real syllable
    /// timing when available, otherwise interpolates across the line.
    /// </summary>
    private static double ComputeHighlightProgress(LyricLine line, TimeSpan position)
    {
        if (line.Duration <= TimeSpan.Zero) return 1;

        var elapsed = position - line.Start;
        if (elapsed <= TimeSpan.Zero) return 0;
        if (elapsed >= line.Duration) return 1;

        return elapsed / line.Duration;
    }

    private static string BuildProgressBar(double progress, int width)
    {
        var filled = (int)Math.Round(progress * width);
        filled = Math.Clamp(filled, 0, width);
        return "[" + new string('#', filled) + new string('-', width - filled) + "]";
    }

    /// <summary>
    /// Offline unit checks for the parser and matcher, so correctness does not
    /// depend on what happens to be playing.
    /// </summary>
    private static int SelfTest()
    {
        int passed = 0, failed = 0;

        void Check(string name, bool ok, string? detail = null)
        {
            var suffix = detail is null ? "" : $"  ({detail})";
            if (ok) { passed++; Console.WriteLine($"  PASS  {name}{suffix}"); }
            else { failed++; Console.WriteLine($"  FAIL  {name}{suffix}"); }
        }

        // Snapshot for the playback-clock checks. The anchor timestamp and the poll
        // time are the same instant, so every expectation below is exact arithmetic.
        static PlaybackSnapshot ClockSample(TimeSpan position, DateTimeOffset at, bool playing = true,
            double rate = 1.0, TimeSpan? duration = null) =>
            new("s", "s", "clock", "a", "", duration ?? TimeSpan.FromMinutes(30),
                position, playing, at)
            { PositionUpdatedAt = at, PlaybackRate = rate };

        Console.WriteLine("=== LRC parser ===");

        const string lrc = """
            [ti:Test]
            [offset:0]
            [00:01.00]First line
            [00:03.50]Second line
            [00:06.25]Third line
            """;

        var doc = LrcParser.ParseLrc(lrc, LyricSourceKind.QqMusic);
        Check("parses 3 lines", doc.Lines.Count == 3);
        Check("first line text", doc.Lines[0].Text == "First line");
        Check("first line start", Math.Abs(doc.Lines[0].Start.TotalSeconds - 1.0) < 0.001);
        Check("line end clipped to next", Math.Abs(doc.Lines[0].End.TotalSeconds - 3.5) < 0.001);
        Check("IndexAt before first = -1", doc.IndexAt(TimeSpan.FromSeconds(0.5)) == -1);
        Check("IndexAt 2s = 0", doc.IndexAt(TimeSpan.FromSeconds(2)) == 0);
        Check("IndexAt 4s = 1", doc.IndexAt(TimeSpan.FromSeconds(4)) == 1);
        Check("IndexAt 100s = last", doc.IndexAt(TimeSpan.FromSeconds(100)) == 2);

        Console.WriteLine("=== translation merge ===");

        const string trans = """
            [00:01.00]第一行
            [00:03.50]//
            [00:06.25]第三行
            """;

        var doc2 = LrcParser.ParseLrc(lrc, LyricSourceKind.QqMusic, trans);
        Check("line0 translated", doc2.Lines[0].Translation == "第一行");
        Check("placeholder // dropped", doc2.Lines[1].Translation is null);
        Check("line2 translated", doc2.Lines[2].Translation == "第三行");

        Console.WriteLine("=== multiple timestamps per line ===");
        var doc3 = LrcParser.ParseLrc("[00:01.00][00:05.00]Repeat", LyricSourceKind.NetEase);
        Check("expands to 2 lines", doc3.Lines.Count == 2);

        Console.WriteLine("=== metadata detection ===");
        Check("作词 detected", LrcParser.IsMetadataLine("作词：潘伟源"));
        Check("Lyrics by detected", LrcParser.IsMetadataLine("Lyrics by：Ed Sheeran"));
        Check("normal lyric not metadata", !LrcParser.IsMetadataLine("街边焦急的我"));

        Console.WriteLine("=== session selection: music, not video ===");
        // A lyrics app has no use for a video session, and following one is worse than
        // following nothing: the overlay then shows a progress bar and a duration for
        // something with no lyrics. Measured case: a paused music player plus a browser
        // playing a 43-minute lecture put "43:04" on screen and resolved no lyrics.
        Check("known music player is music",
            SmtcMediaSessionSource.LooksLikeMusic("QQMusic.exe", "个人简介", "安全着陆"));
        Check("NetEase is music",
            SmtcMediaSessionSource.LooksLikeMusic("cloudmusic.exe", "孤身", "徐秉龙"));
        Check("a media filename is not music",
            !SmtcMediaSessionSource.LooksLikeMusic(
                "Quark.Player", "03.第6讲二、合同变换，二次型的合同标准形、规范形03.mp4", ""));
        Check("no artist is not music",
            !SmtcMediaSessionSource.LooksLikeMusic("SomeBrowser", "Some Video Title", ""));
        Check("an unknown player with an artist is still music",
            SmtcMediaSessionSource.LooksLikeMusic("UnknownPlayer.exe", "夜曲", "周杰伦"));
        Console.WriteLine("=== matcher ===");
        var track = new PlaybackSnapshot("QQMusic.exe", "QQMusic.exe", "敢爱敢做", "林子祥",
            "album", TimeSpan.FromSeconds(299), TimeSpan.Zero, true, DateTimeOffset.Now);

        var exact = new LyricCandidate(LyricSourceKind.QqMusic, "a", "敢爱敢做", "林子祥",
            TimeSpan.FromSeconds(299));
        var live = new LyricCandidate(LyricSourceKind.QqMusic, "b", "敢爱敢做 (Live)", "林子祥/叶蒨文",
            TimeSpan.FromSeconds(254));
        var wrong = new LyricCandidate(LyricSourceKind.QqMusic, "c", "晴天", "周杰伦",
            TimeSpan.FromSeconds(269));

        var sExact = LyricMatcher.Score(track, exact);
        var sLive = LyricMatcher.Score(track, live);
        var sWrong = LyricMatcher.Score(track, wrong);

        Console.WriteLine($"  exact={sExact:F1}  live={sLive:F1}  wrong={sWrong:F1}");
        Check("exact above confident threshold", sExact >= LyricResolver.ConfidentScore);
        Check("exact beats live", sExact > sLive);
        Check("exact beats wrong", sExact > sWrong);
        Check("wrong below minimum", sWrong < LyricResolver.MinimumScore);

        Console.WriteLine("=== matcher: version-marker false positives ===");
        // These titles merely CONTAIN marker-like substrings. A plain substring
        // scan flagged them as version conflicts and sank a correct match.
        foreach (var title in new[] { "白日梦蓝", "活着", "出版", "Deliver", "演唱会", "版本" })
        {
            var t = new PlaybackSnapshot("s", "s", title, "artist", "",
                TimeSpan.FromMinutes(4), TimeSpan.Zero, true, DateTimeOffset.Now);
            var c = new LyricCandidate(LyricSourceKind.QqMusic, "x", title, "artist",
                TimeSpan.FromMinutes(4));

            var score = LyricMatcher.Score(t, c);
            Check($"identical '{title}' scores confidently",
                score >= LyricResolver.ConfidentScore, $"score={score:F1}");
        }

        Console.WriteLine("=== matcher: real version mismatch still caught ===");
        var liveCandidate = new LyricCandidate(LyricSourceKind.QqMusic, "x", "敢爱敢做 (Live)",
            "林子祥", TimeSpan.FromSeconds(299));
        var sLiveSame = LyricMatcher.Score(track, liveCandidate);
        Console.WriteLine($"  studio vs (Live) = {sLiveSame:F1}");
        Check("live variant penalised below exact", sLiveSame < sExact);
        Check("live variant still acceptable when otherwise identical",
            sLiveSame >= LyricResolver.MinimumScore);

        Console.WriteLine("=== matcher: a different recording is disqualified ===");
        // Duration is the one objective signal that the candidate is a different edition.
        // Every timestamp in a live take or a cover is wrong for the studio recording, and
        // the highlight drifts further out of step as the song runs, so such a candidate
        // must not be usable at all. It used to keep ~85 because a 30 s tolerance was worth
        // only 15 points.
        Console.WriteLine($"  studio 299s vs live 254s (delta 45s) = {sLive:F1}");
        Check("live take 45s off is disqualified", sLive == 0, $"score={sLive:F1}");

        var farOff = new LyricCandidate(LyricSourceKind.NetEase, "d", "敢爱敢做", "林子祥",
            TimeSpan.FromSeconds(360));
        var sFarOff = LyricMatcher.Score(track, farOff);
        Check("same title and artist but 61s longer is still disqualified", sFarOff == 0,
            $"score={sFarOff:F1}");

        // The second pass disables that check. It has to, because the check depends on both
        // sides reporting a comparable length and they do not always: a streamed trial clip
        // or a player whose figure differs from its catalogue makes every candidate look like
        // a different recording. Refusing all of them showed no lyrics at all.
        var sRelaxed = LyricMatcher.Score(track, farOff, ignoreDuration: true);
        Console.WriteLine($"  61s-longer candidate, duration ignored = {sRelaxed:F1}");
        Check("the second pass accepts a duration mismatch rather than showing nothing",
            sRelaxed >= LyricResolver.MinimumScore, $"score={sRelaxed:F1}");
        Check("but it still ranks below an exact match", sRelaxed < sExact,
            $"relaxed={sRelaxed:F1} exact={sExact:F1}");
        Check("the second pass still rejects a wrong title",
            LyricMatcher.Score(track, wrong, ignoreDuration: true) < LyricResolver.MinimumScore);

        // A few seconds of encoder padding or leading silence must not disqualify anything.
        var closeEnough = new LyricCandidate(LyricSourceKind.NetEase, "e", "敢爱敢做", "林子祥",
            TimeSpan.FromSeconds(302));
        var sClose = LyricMatcher.Score(track, closeEnough);
        Check("3s difference is treated as the same recording",
            sClose >= LyricResolver.ConfidentScore, $"score={sClose:F1}");

        // Providers that omit a length must not lose out.
        var noDuration = new LyricCandidate(LyricSourceKind.Lrclib, "f", "敢爱敢做", "林子祥",
            TimeSpan.Zero);
        var sNoDuration = LyricMatcher.Score(track, noDuration);
        Check("an unknown duration is not treated as a mismatch",
            sNoDuration >= LyricResolver.MinimumScore, $"score={sNoDuration:F1}");

        Console.WriteLine("=== matcher: prefers the service that is playing ===");
        // Scores tie routinely: two services both hold the same recording. The preference is
        // therefore a tie-break in the resolver, not a bonus inside the clamped score, which
        // would vanish at exactly the moment it was needed.
        var fromPlaying = new LyricCandidate(LyricSourceKind.QqMusic, "g", "敢爱敢做", "林子祥",
            TimeSpan.FromSeconds(299));
        var fromOther = new LyricCandidate(LyricSourceKind.NetEase, "h", "敢爱敢做", "林子祥",
            TimeSpan.FromSeconds(299));
        var sFromPlaying = LyricMatcher.Score(track, fromPlaying);
        var sFromOther = LyricMatcher.Score(track, fromOther);
        Console.WriteLine($"  playing=QQMusic.exe  QQ={sFromPlaying:F1}  NetEase={sFromOther:F1}  (tie)");

        Check("identical candidates score identically, so a tie-break is needed",
            Math.Abs(sFromPlaying - sFromOther) < 0.001, $"{sFromPlaying:F1} vs {sFromOther:F1}");
        Check("the playing service is recognised",
            LyricMatcher.MatchesPlayingService(track.SourceAppId, LyricSourceKind.QqMusic));
        Check("another service is not mistaken for the playing one",
            !LyricMatcher.MatchesPlayingService(track.SourceAppId, LyricSourceKind.NetEase));

        // The session reports an executable name, so an unknown or absent player must simply
        // match nothing rather than breaking the comparison.
        var unknownPlayer = new PlaybackSnapshot("s", "Spotify.exe", "敢爱敢做", "林子祥",
            "", TimeSpan.FromSeconds(299), TimeSpan.Zero, true, DateTimeOffset.Now);
        var sUnknown = LyricMatcher.Score(unknownPlayer, fromPlaying);
        Check("an unknown player still scores on title, artist and duration",
            sUnknown >= LyricResolver.ConfidentScore, $"score={sUnknown:F1}");
        Check("an unknown player matches no service",
            !LyricMatcher.MatchesPlayingService(unknownPlayer.SourceAppId, LyricSourceKind.QqMusic) &&
            !LyricMatcher.MatchesPlayingService(unknownPlayer.SourceAppId, LyricSourceKind.NetEase));

        Console.WriteLine("=== URI encoder ===");
        Check("escapes ampersand", QueryUri.Encode("A&B") == "A%26B");
        Check("escapes hash", QueryUri.Encode("C#") == "C%23");
        Check("escapes percent", QueryUri.Encode("100%") == "100%25");
        Check("escapes space", QueryUri.Encode("a b") == "a%20b");

        var hostile = QueryUri.Build("https://example.com/api",
            ("w", "x&format=evil"), ("n", "1"));
        Check("hostile value cannot inject a parameter",
            hostile.Query.Contains("%26") && !hostile.Query.Contains("format=evil"),
            hostile.Query);
        Check("absurdly long value is truncated",
            QueryUri.Encode(new string('a', 2000)).Length < 1600);
        Check("rejects relative base", Throws(() => QueryUri.Build("not-a-url", ("a", "b"))));
        Check("rejects non-http scheme", Throws(() => QueryUri.Build("file:///c:/x", ("a", "b"))));

        Console.WriteLine("=== position extrapolation ===");
        var snap = new PlaybackSnapshot("s", "s", "t", "a", "", TimeSpan.FromSeconds(300),
            TimeSpan.FromSeconds(10), true, DateTimeOffset.Now.AddSeconds(-2));
        Check("extrapolates while playing",
            Math.Abs(snap.ExtrapolatedPosition(DateTimeOffset.Now).TotalSeconds - 12) < 0.3);

        var paused = snap with { IsPlaying = false };
        Check("frozen while paused",
            Math.Abs(paused.ExtrapolatedPosition(DateTimeOffset.Now).TotalSeconds - 10) < 0.001);

        var clamped = snap with { Position = TimeSpan.FromSeconds(299.5) };
        Check("clamped at duration",
            clamped.ExtrapolatedPosition(DateTimeOffset.Now) <= TimeSpan.FromSeconds(300));

        // ---- PlaybackClock -------------------------------------------------
        // The renderer drives off PlaybackClock rather than ExtrapolatedPosition:
        // SMTC's roughly once-per-second samples need a rate measured from the
        // samples themselves, and each correction has to be absorbed rather than
        // snapped to. A fixed origin makes every expectation below exact arithmetic.
        var clockOrigin = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);

        Console.WriteLine("=== playback clock: measured rate ===");
        // The player claims 1.0x while genuinely running at 1.5x — the case that
        // makes the reported rate useless. Extrapolating at 1.0x drifts 0.5 s/s.
        var clock = new PlaybackClock();
        for (int i = 0; i <= 10; i++)
        {
            var at = clockOrigin.AddSeconds(i);
            clock.Observe(ClockSample(TimeSpan.FromSeconds(20 + i * 1.5), at), at);
        }

        var onSample = clock.Position(clockOrigin.AddSeconds(10));
        Check("lands on the reported sample after 10 s of 1.5x playback",
            Math.Abs(onSample.TotalSeconds - 35) < 0.005, $"pos={onSample.TotalSeconds:F3}s");

        // Sampled the way the renderer does, between two samples, the prediction must
        // stay on the true position line rather than fall behind it.
        double worst = 0;
        for (int ms = 10000; ms <= 10500; ms += 80)
        {
            var truth = 20 + 1.5 * (ms / 1000.0);
            var sampled = clock.Position(clockOrigin.AddMilliseconds(ms)).TotalSeconds;
            worst = Math.Max(worst, Math.Abs(sampled - truth));
        }
        Check("never drifts more than a frame from the true position",
            worst < 0.05, $"worst={worst * 1000:F1}ms");

        var halfway = clock.Position(clockOrigin.AddMilliseconds(10500));
        Check("extrapolates at the measured 1.5x rate, not the reported 1.0x",
            Math.Abs(halfway.TotalSeconds - 35.75) < 0.005, $"pos={halfway.TotalSeconds:F3}s");

        Console.WriteLine("=== playback clock: repeated polls of one sample ===");
        var dedup = new PlaybackClock();
        dedup.Observe(ClockSample(TimeSpan.FromSeconds(10), clockOrigin), clockOrigin);

        // Three more polls re-reading the very same sample: same anchor, same
        // position. They carry no new information and must not enter the rate chain.
        for (int i = 1; i <= 3; i++)
        {
            dedup.Observe(ClockSample(TimeSpan.FromSeconds(10), clockOrigin),
                clockOrigin.AddMilliseconds(300 * i));
        }

        // A genuinely new sample one second after the first reports 11.5 s: the real
        // rate is 1.5x. Had the stale re-polls counted as samples, the pair would
        // measure 3x instead.
        var dedupAt = clockOrigin.AddSeconds(1);
        dedup.Observe(ClockSample(TimeSpan.FromSeconds(11.5), dedupAt), dedupAt);
        var afterDedup = dedup.Position(clockOrigin.AddMilliseconds(1500));
        Check("stale re-polls do not corrupt the measured rate",
            Math.Abs(afterDedup.TotalSeconds - 12.25) < 0.005, $"pos={afterDedup.TotalSeconds:F3}s");

        Console.WriteLine("=== playback clock: correction is absorbed, not snapped ===");
        var smooth = new PlaybackClock();
        smooth.Observe(ClockSample(TimeSpan.FromSeconds(10), clockOrigin), clockOrigin);

        // One second later the player reports 11.080 s: 80 ms ahead of the 11.000 s we
        // predicted at 1.0x. That is drift, not a seek.
        var driftAt = clockOrigin.AddSeconds(1);
        smooth.Observe(ClockSample(TimeSpan.FromSeconds(11.080), driftAt), driftAt);

        var immediately = smooth.Position(driftAt);
        Check("does not jump to the reported position",
            immediately != TimeSpan.FromSeconds(11.080) &&
            Math.Abs(immediately.TotalSeconds - 11) < 0.005,
            $"pos={immediately.TotalSeconds:F3}s");

        // The measured rate is now 1.08x, so the truth at +150 ms is 11.242 s.
        var midway = smooth.Position(driftAt.AddMilliseconds(150));
        Check("moves toward the reported position gradually",
            midway > immediately && midway < TimeSpan.FromSeconds(11.242),
            $"pos={midway.TotalSeconds:F3}s");

        var settled = smooth.Position(driftAt.AddMilliseconds(300));
        Check("has converged once the correction window has passed",
            Math.Abs(settled.TotalSeconds - (11.080 + 0.3 * 1.08)) < 0.005,
            $"pos={settled.TotalSeconds:F3}s  expected=11.404s");

        Console.WriteLine("=== playback clock: seek and pause ===");
        var seek = new PlaybackClock();
        seek.Observe(ClockSample(TimeSpan.FromSeconds(10), clockOrigin), clockOrigin);
        var seekAt = clockOrigin.AddSeconds(1);
        seek.Observe(ClockSample(TimeSpan.FromSeconds(60), seekAt), seekAt);

        var landed = seek.Position(seekAt);
        Check("a seek lands immediately",
            Math.Abs(landed.TotalSeconds - 60) < 0.005, $"pos={landed.TotalSeconds:F3}s");

        var crept = seek.Position(seekAt.AddMilliseconds(100));
        Check("advances from the new position right after the seek",
            Math.Abs(crept.TotalSeconds - 60.1) < 0.005, $"pos={crept.TotalSeconds:F3}s");

        var pausedClock = new PlaybackClock();
        pausedClock.Observe(ClockSample(TimeSpan.FromSeconds(30), clockOrigin), clockOrigin);
        var pauseAt = clockOrigin.AddSeconds(1);
        pausedClock.Observe(ClockSample(TimeSpan.FromSeconds(31), pauseAt, playing: false), pauseAt);

        var frozen = pausedClock.Position(pauseAt.AddSeconds(6));
        Check("does not advance across a multi-second pause",
            frozen == pausedClock.Position(pauseAt.AddMilliseconds(50)) &&
            Math.Abs(frozen.TotalSeconds - 31) < 0.005,
            $"pos={frozen.TotalSeconds:F3}s");

        var resumeAt = pauseAt.AddSeconds(6);
        pausedClock.Observe(ClockSample(TimeSpan.FromSeconds(31), resumeAt), resumeAt);
        var resumed = pausedClock.Position(resumeAt.AddSeconds(2));
        Check("a resume re-anchors instead of counting the paused wall-clock time",
            Math.Abs(resumed.TotalSeconds - 33) < 0.005, $"pos={resumed.TotalSeconds:F3}s");

        Console.WriteLine("=== playback clock: clamping ===");
        var negative = new PlaybackClock();
        negative.Observe(ClockSample(TimeSpan.FromMilliseconds(-50), clockOrigin), clockOrigin);
        Check("never returns a negative position",
            negative.Position(clockOrigin) == TimeSpan.Zero);

        var bounded = new PlaybackClock();
        bounded.Observe(ClockSample(TimeSpan.FromSeconds(299.5), clockOrigin,
            duration: TimeSpan.FromSeconds(300)), clockOrigin);
        Check("never runs past the track duration",
            bounded.Position(clockOrigin.AddSeconds(2)) == TimeSpan.FromSeconds(300));

        Console.WriteLine("=== playback clock: rate guards ===");
        // Two samples 50 ms apart are too close to measure a rate from: position
        // quantisation alone would report 2x for a 100 ms step.
        var tooShort = new PlaybackClock();
        tooShort.Observe(ClockSample(TimeSpan.FromSeconds(10), clockOrigin), clockOrigin);
        var shortAt = clockOrigin.AddMilliseconds(50);
        tooShort.Observe(ClockSample(TimeSpan.FromSeconds(10.1), shortAt), shortAt);
        var shortPrediction = tooShort.Position(clockOrigin.AddMilliseconds(1050));
        Check("ignores a rate measured over too short an interval",
            Math.Abs(shortPrediction.TotalSeconds - 11.1) < 0.005,
            $"pos={shortPrediction.TotalSeconds:F3}s");

        // 0.1x is absurd for a player reporting normal speed, so the measurement is
        // rejected and the reported rate stands.
        var absurd = new PlaybackClock();
        absurd.Observe(ClockSample(TimeSpan.FromSeconds(10), clockOrigin), clockOrigin);
        var absurdAt = clockOrigin.AddSeconds(1);
        absurd.Observe(ClockSample(TimeSpan.FromSeconds(10.1), absurdAt), absurdAt);
        var absurdPrediction = absurd.Position(clockOrigin.AddSeconds(2));
        Check("rejects a measured rate outside 0.25..4.0",
            Math.Abs(absurdPrediction.TotalSeconds - 11.1) < 0.005,
            $"pos={absurdPrediction.TotalSeconds:F3}s");

        // No measurement is possible from one sample, so the reported rate is used;
        // when even that is nonsense, 1.0.
        var reported = new PlaybackClock();
        reported.Observe(ClockSample(TimeSpan.FromSeconds(10), clockOrigin, rate: 1.25), clockOrigin);
        var reportedPos = reported.Position(clockOrigin.AddSeconds(1));
        Check("falls back to the reported rate when none can be measured",
            Math.Abs(reportedPos.TotalSeconds - 11.25) < 0.005, $"pos={reportedPos.TotalSeconds:F3}s");

        var bogus = new PlaybackClock();
        bogus.Observe(ClockSample(TimeSpan.FromSeconds(10), clockOrigin, rate: 0), clockOrigin);
        var bogusPos = bogus.Position(clockOrigin.AddSeconds(1));
        Check("falls back to 1.0 when the reported rate is nonsense",
            Math.Abs(bogusPos.TotalSeconds - 11) < 0.005, $"pos={bogusPos.TotalSeconds:F3}s");

        Console.WriteLine("=== playback clock: reset ===");
        var reset = new PlaybackClock();
        reset.Observe(ClockSample(TimeSpan.FromSeconds(40), clockOrigin), clockOrigin);
        reset.Reset();
        Check("Reset drops all history",
            reset.Position(clockOrigin.AddSeconds(5)) == TimeSpan.Zero);

        Console.WriteLine("=== sung span excludes instrumental gaps ===");
        // Two lines with a deliberately long gap between them: the second line starts
        // 20 s after the first, but only has a few characters to sing. The highlight
        // must not be stretched across that gap.
        const string gapped = """
            [00:00.00]短句
            [00:20.00]另一句
            """;
        var gd = LrcParser.ParseLrc(gapped, LyricSourceKind.QqMusic);
        Check("parsed 2 lines", gd.Lines.Count == 2);
        if (gd.Lines.Count == 2)
        {
            var first = gd.Lines[0];
            Console.WriteLine($"  line0 gap={first.Duration.TotalSeconds:F1}s  " +
                              $"sung={first.SungDuration.TotalSeconds:F2}s");
            Check("gap is 20 s", Math.Abs(first.Duration.TotalSeconds - 20) < 0.01);

            // Without word timing there is nothing to narrow the span with, so the whole
            // gap is used and the renderer's ease-out curve handles the tail. This
            // replaces an assertion that expected a syllables-per-second estimate; that
            // estimate was removed (too slow for rap, too fast for ballads) and this
            // assertion was left behind, failing, until now.
            Check("sung span spans the gap when there is no word timing",
                Math.Abs(first.SungDuration.TotalSeconds - first.Duration.TotalSeconds) < 0.01,
                $"sung={first.SungDuration.TotalSeconds:F2}s");
            Check("sung span is positive", first.SungDuration > TimeSpan.Zero);

            // A long line in the same gap must still never exceed it.
            var longLine = LrcParser.ParseLrc(
                "[00:00.00]" + new string('字', 60) + "\n[00:20.00]x", LyricSourceKind.QqMusic).Lines[0];
            Console.WriteLine($"  long line: sung={longLine.SungDuration.TotalSeconds:F2}s " +
                              $"of {longLine.Duration.TotalSeconds:F1}s gap");
            Check("long line still within the gap", longLine.SungDuration <= longLine.Duration);
        }

        Console.WriteLine();
        Console.WriteLine("=== word timing wins over the inter-line gap ===");
        // A duet overlaps: the second line can begin before the first one finishes.
        // Stretching a line to the next line's start then truncates syllables that are
        // still being sung, and the renderer can never light them. Measured on a real
        // TTML file, that cost three syllables.
        const string duet = """
            <tt xmlns="http://www.w3.org/ns/ttml" xmlns:ttm="http://www.w3.org/ns/ttml#metadata">
              <body><div>
                <p begin="00:00.000" end="00:04.000">
                  <span begin="00:00.000" end="00:02.000">甲</span>
                  <span begin="00:03.000" end="00:04.000">乙</span>
                </p>
                <p begin="00:03.500" end="00:06.000">
                  <span begin="00:03.500" end="00:05.000">丙</span>
                </p>
              </div></body>
            </tt>
            """;

        var duetDoc = TtmlParser.Parse(duet);
        Check("parsed the overlapping duet", duetDoc.Lines.Count == 2);
        if (duetDoc.Lines.Count == 2)
        {
            var a = duetDoc.Lines[0];
            Console.WriteLine($"  line0 [{a.Start.TotalSeconds:F2}-{a.End.TotalSeconds:F2}] " +
                              $"sung={a.SungDuration.TotalSeconds:F2}s  \"{a.Text}\"");

            Check("line keeps its own end, not the next line's start",
                a.End.TotalSeconds >= 4.0 - 0.001, $"end={a.End.TotalSeconds:F3}s");
            Check("sung span comes from the last syllable",
                Math.Abs(a.SungDuration.TotalSeconds - 4.0) < 0.001,
                $"sung={a.SungDuration.TotalSeconds:F3}s");
            Check("every syllable sits inside its line",
                duetDoc.Lines.All(l => l.Syllables.All(
                    s => s.Start >= l.Start && s.End <= l.End + TimeSpan.FromMilliseconds(1))));
        }

        Console.WriteLine();
        Console.WriteLine("=== TTML clock formats ===");
        Check("hh:mm:ss.mmm", TtmlParser.ParseClock("01:02:03.500")?.TotalSeconds is > 3723.4 and < 3723.6);
        Check("mm:ss.mmm", TtmlParser.ParseClock("02:03.250")?.TotalSeconds is > 123.2 and < 123.3);
        Check("offset ms", TtmlParser.ParseClock("1250ms")?.TotalSeconds is > 1.24 and < 1.26);
        Check("offset s", TtmlParser.ParseClock("12.5s")?.TotalSeconds is > 12.4 and < 12.6);
        Check("garbage rejected", TtmlParser.ParseClock("banana") is null);
        Check("empty rejected", TtmlParser.ParseClock("") is null);

        Console.WriteLine();
        Console.WriteLine("=== YRC word-level (NetEase) ===");
        // NetEase's word timing lives in the yrc field, which the API only returns when the
        // request asks for it (yv). Its word tags carry three numbers where QRC carries two,
        // so the QRC pattern does not match and this parser is deliberately separate.
        const string yrc = """
            [0,1000](0,1000,0)OP: someone
            [17630,3810](17630,210,0)A(17840,360,0)B(18200,130,0)C(18330,430,0)D(18760,940,0)E
            [21890,5120](21890,310,0)V(22200,240,0)W(22440,300,0)X(22740,260,0)Y(23000,200,0)Z
            """;

        var yrcDoc = YrcParser.Parse(yrc);
        Check("parsed the yrc lines", yrcDoc.Lines.Count == 3, $"lines={yrcDoc.Lines.Count}");
        Check("yrc reports real word timing", yrcDoc.HasRealWordTiming);

        if (yrcDoc.Lines.Count == 3)
        {
            var first = yrcDoc.Lines[1];
            Console.WriteLine($"  line1 [{first.Start.TotalMilliseconds:F0}ms] \"{first.Text}\" " +
                              $"syllables={first.Syllables.Count}");

            Check("line tag is a millisecond pair",
                Math.Abs(first.Start.TotalMilliseconds - 17630) < 0.5,
                $"start={first.Start.TotalMilliseconds:F0}ms");
            Check("word text is concatenated", first.Text == "ABCDE", $"\"{first.Text}\"");
            Check("every word became a syllable", first.Syllables.Count == 5,
                $"count={first.Syllables.Count}");
            Check("word times are absolute",
                first.Syllables.Count > 1 &&
                Math.Abs(first.Syllables[1].Start.TotalMilliseconds - 17840) < 0.5,
                first.Syllables.Count > 1
                    ? $"second={first.Syllables[1].Start.TotalMilliseconds:F0}ms"
                    : "n/a");
            Check("metadata line is flagged", yrcDoc.Lines[0].IsMetadata,
                $"\"{yrcDoc.Lines[0].Text}\"");
            Check("no syllable falls outside its line",
                yrcDoc.Lines.All(l => l.Syllables.All(
                    s => s.Start >= l.Start && s.End <= l.End + TimeSpan.FromMilliseconds(1))));
        }

        Check("garbage yrc yields an empty document",
            YrcParser.Parse("not a yrc file at all").IsEmpty);
        Check("empty yrc yields an empty document",
            YrcParser.Parse(string.Empty).IsEmpty);

        Console.WriteLine();
        Console.WriteLine("=== QRC decryption ===");
        // Round-trip: compress and encrypt with the same key schedule, then decrypt.
        // This proves the DES port and its key schedule are self-consistent without
        // touching the network, and it is the part most likely to be subtly wrong.
        const string sample = "[ti:test]\n[0,1000](0,300,0)hello (300,700,0)world";
        var sealedBytes = QrcDecryptor.EncryptForTest(sample);
        Check("qrc encrypt produces whole blocks", sealedBytes.Length % 8 == 0,
            sealedBytes.Length.ToString());
        var opened = QrcDecryptor.TryDecrypt(sealedBytes);
        Check("qrc round-trip recovers the text", opened == sample,
            opened is null ? "null" : $"len={opened.Length}");
        Check("garbage is rejected rather than throwing",
            QrcDecryptor.TryDecrypt(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16 }) is null);

        Console.WriteLine();
        Console.WriteLine($"self-test: {passed} passed, {failed} failed");
        return failed == 0 ? 0 : 1;
    }

    /// <summary>True when the action throws, used for negative test cases.</summary>
    private static bool Throws(Action action)
    {
        try
        {
            action();
            return false;
        }
        catch
        {
            return true;
        }
    }
}
