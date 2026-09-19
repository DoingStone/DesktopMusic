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
            _ => Usage(),
        };
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
            Check("sung span much shorter than the gap",
                first.SungDuration.TotalSeconds < 3,
                $"sung={first.SungDuration.TotalSeconds:F2}s");
            Check("sung span is positive", first.SungDuration > TimeSpan.Zero);

            // A long line in the same gap should get proportionally more time,
            // but still never exceed the gap.
            var longLine = LrcParser.ParseLrc(
                "[00:00.00]" + new string('字', 60) + "\n[00:20.00]x", LyricSourceKind.QqMusic).Lines[0];
            Console.WriteLine($"  long line: sung={longLine.SungDuration.TotalSeconds:F2}s " +
                              $"of {longLine.Duration.TotalSeconds:F1}s gap");
            Check("long line still within the gap", longLine.SungDuration <= longLine.Duration);
            Check("long line gets more time than the short one",
                longLine.SungDuration > first.SungDuration);
        }

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
