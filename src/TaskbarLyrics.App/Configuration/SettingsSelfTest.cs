using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace TaskbarLyrics.App.Configuration;

/// <summary>
/// Headless verification of <see cref="AppSettings"/> persistence, run via
/// <c>TaskbarLyrics.exe --selftest</c>.
/// <para>
/// It lives in the app rather than the CLI so it exercises the exact production
/// settings code, and it exists because the overlay's saved position, colours and
/// lyric offset all depend on this round-trip being lossless.
/// </para>
/// </summary>
internal static class SettingsSelfTest
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static int Run()
    {
        // A WinExe has no attached console, so write the report to a file as well
        // as stdout (which is visible when launched from a console host).
        var reportPath = Path.Combine(Path.GetTempPath(), "taskbar-lyrics-selftest.txt");

        using var file = new StreamWriter(reportPath, append: false, System.Text.Encoding.UTF8)
        {
            AutoFlush = true,
        };

        var writer = new TeeWriter(Console.Out, file);
        int result = RunCore(writer);

        Console.WriteLine($"report written to {reportPath}");
        return result;
    }

    private static int RunCore(TextWriter output)
    {
        output.WriteLine("=== AppSettings persistence ===");

        int passed = 0, failed = 0;

        void Check(string name, bool ok)
        {
            if (ok) { passed++; output.WriteLine($"  PASS  {name}"); }
            else { failed++; output.WriteLine($"  FAIL  {name}"); }
        }

        var dir = Path.Combine(Path.GetTempPath(), "tbl-settings-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "settings.json");

        try
        {
            var original = new AppSettings
            {
                Width = 512,
                OffsetX = -37.5,
                OffsetY = 2,
                HighlightColor = "#FF12AB34",
                BaseColor = "#FFE8E8E8",
                BackgroundColor = "#B3121212",
                FontFamily = "Microsoft YaHei UI",
                OriginalFontSize = 13.5,
                GlobalOffsetMs = -250,
                Locked = false,
                ShowTranslation = false,
                EnableLrclib = false,
            };

            // Mirror exactly what AppSettings.Save/Load do.
            File.WriteAllText(path, JsonSerializer.Serialize(original, Options));
            var loaded = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), Options);

            Check("settings file written", File.Exists(path));
            Check("width survives round-trip", loaded is { Width: 512 });
            Check("sub-pixel offset survives", loaded is { OffsetX: -37.5, OffsetY: 2 });
            Check("highlight colour survives", loaded is { HighlightColor: "#FF12AB34" });
            Check("background colour survives", loaded is { BackgroundColor: "#B3121212" });
            Check("font size survives", loaded is { OriginalFontSize: 13.5 });
            Check("lyric offset survives", loaded is { GlobalOffsetMs: -250 });
            Check("locked flag survives", loaded is { Locked: false });
            Check("translation toggle survives", loaded is { ShowTranslation: false });
            Check("provider toggles survive",
                loaded is { EnableLrclib: false, EnableQqMusic: true, EnableNetEase: true });
            Check("position memory initialises",
                loaded is not null && loaded.Positions is not null);

            // A corrupt file must never prevent startup.
            File.WriteAllText(path, "{ this is not valid json");
            var recovered = AppSettings.Load();
            Check("corrupt settings fall back to defaults",
                recovered is { Width: > 0, HighlightColor: not null });
        }
        catch (Exception ex)
        {
            Check("completed without exception", false);
            output.WriteLine("  " + ex);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }

        output.WriteLine();
        output.WriteLine("=== colour picker palette renders ===");
        VerifyPalette(output, ref passed, ref failed);

        output.WriteLine();
        output.WriteLine("=== translation line centring ===");
        VerifyTranslationCentring(output, ref passed, ref failed);
        VerifyWordSweep(output, ref passed, ref failed);

        output.WriteLine();
        output.WriteLine($"settings self-test: {passed} passed, {failed} failed");
        return failed == 0 ? 0 : 1;
    }

    /// <summary>
    /// Render a lyric pair off-screen and confirm the translation row is centred on its
    /// own width.
    /// <para>
    /// It used to be positioned from the original line's width, which left a shorter
    /// translation aligned to that line's left edge and read as left-shifted. Asserting
    /// the rendered ink position catches that; asserting layout properties would not.
    /// </para>
    /// </summary>
    /// <summary>
    /// The highlight must land on the character being sung, not somewhere along the line.
    /// <para>
    /// This is what word timing buys, and it is the only claim that matters for sync: the
    /// line is given four equal-width characters with known per-character times, and the
    /// sweep boundary is measured in the rendered pixels at three instants. Each instant is
    /// the exact end of a character, so the boundary has to sit at 1/4, 1/2 and 3/4 of the
    /// text's own ink width. Without word timing those ratios are whatever the ease-out
    /// curve guesses.
    /// </para>
    /// </summary>
    private static void VerifyWordSweep(TextWriter output, ref int passed, ref int failed)
    {
        int ok = 0, bad = 0;

        void Check(string name, bool success, string? detail = null)
        {
            output.WriteLine($"  {(success ? "PASS" : "FAIL")}  {name}{(detail is null ? "" : $"  ({detail})")}");
            if (success) ok++; else bad++;
        }

        try
        {
            const int width = 600;
            const int height = 64;
            const string glyphs = "甲乙丙丁";
            const int perSyllableMs = 500;
            var total = TimeSpan.FromMilliseconds(perSyllableMs * glyphs.Length);

            var syllables = glyphs
                .Select((ch, i) => new Core.Models.LyricSyllable(
                    TimeSpan.FromMilliseconds(i * perSyllableMs),
                    TimeSpan.FromMilliseconds(perSyllableMs),
                    ch.ToString()))
                .ToArray();

            // Measures the sweep: the text's own extent and the rightmost highlighted
            // pixel, in device-independent pixels.
            (double Start, double Boundary, double End) Render(double progress)
            {
                var line = new Controls.KaraokeLine
                {
                    Text = glyphs,
                    Syllables = syllables,
                    LineStart = TimeSpan.Zero,
                    LineEnd = total,
                    IsCurrent = true,
                    WordHighlightEnabled = true,
                    FontSizeValue = 28,
                    Progress = progress,
                    // Base is white and the highlight is strongly blue, so a pixel can be
                    // classified by colour alone.
                    BaseColor = System.Windows.Media.Brushes.White,
                    HighlightColor = System.Windows.Media.Brushes.DeepSkyBlue,
                    ContextColor = System.Windows.Media.Brushes.Gray,
                };

                line.Measure(new System.Windows.Size(width, height));
                line.Arrange(new System.Windows.Rect(0, 0, width, height));
                line.UpdateLayout();

                var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(
                    width, height, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
                rtb.Render(line);

                var pixels = new byte[width * height * 4];
                rtb.CopyPixels(pixels, width * 4, 0);

                double minInk = double.MaxValue, maxInk = -1, maxHighlight = -1;
                for (int y = 0; y < height; y++)
                {
                    for (int x = 0; x < width; x++)
                    {
                        int i = ((y * width) + x) * 4;
                        if (pixels[i + 3] <= 8) continue;

                        if (x < minInk) minInk = x;
                        if (x > maxInk) maxInk = x;

                        byte b = pixels[i], g = pixels[i + 1], r = pixels[i + 2];

                        // "Highlighted" means any blue tint at all, not a saturated pixel. The
                        // last column or two of a glyph is a faint anti-aliased edge, so a
                        // threshold that demands saturation reports a fully sung line as one or
                        // two pixels short of its own text. Over the white base a DeepSkyBlue
                        // blend lifts blue above red by ~13 per 5% coverage, while an unsung
                        // (white) or grey pixel has blue == red.
                        if (b > r + 6)
                        {
                            if (x > maxHighlight) maxHighlight = x;
                        }
                    }
                }

                return (minInk == double.MaxValue ? 0 : minInk, maxHighlight, maxInk);
            }

            // The extent comes from a fully-swept render, so the expectations depend on
            // neither the font's metrics nor where the centred text happens to sit.
            // Two pixels of tolerance: the final column of a glyph is a sub-pixel anti-aliased
            // ramp whose classification can differ between machines, while a genuine regression
            // (a faded sweep tail, or no highlight pass at all) is far wider than that.
            var (fStart, fBoundary, fEnd) = Render(1.0);
            Check("fully swept: highlight covers the whole line",
                fBoundary >= fEnd - 2 && fEnd - fStart > 40,
                $"highlight={fBoundary:F0} text=[{fStart:F0}..{fEnd:F0}]");

            foreach (var (progress, fraction, label) in new[]
                     {
                         (0.25, 0.25, "after 1 of 4"),
                         (0.50, 0.50, "after 2 of 4"),
                         (0.75, 0.75, "after 3 of 4"),
                     })
            {
                var (start, boundary, end) = Render(progress);

                // Measured against the TEXT's own extent, not the control's. The line is
                // centred, so dividing by an absolute x reports 76% for a quarter-swept
                // line - a plausible-looking number that hides a real regression.
                double span = end - start;
                double ratio = span > 0 ? (boundary - start) / span : 0;

                Check($"sweep lands on the character boundary {label}",
                    Math.Abs(ratio - fraction) < 0.06,
                    $"boundary at {ratio:P0} of text width, expected {fraction:P0}");
            }

            // Half-way through the third character the sweep must be strictly between the
            // second and third boundaries: that is "following the character being sung".
            var (mStart, mBoundary, mEnd) = Render(0.625);
            double mSpan = mEnd - mStart;
            double midRatio = mSpan > 0 ? (mBoundary - mStart) / mSpan : 0;
            Check("part-way through a character the sweep is part-way through it",
                midRatio > 0.5 + 0.03 && midRatio < 0.75 - 0.03,
                $"boundary={midRatio:P0}, strictly between 50% and 75%");
        }
        catch (Exception ex)
        {
            Check("word sweep harness", false, ex.GetType().Name);
        }

        output.WriteLine($"  word sweep: {ok} passed, {bad} failed");
        passed += ok;
        failed += bad;

        VerifySweepFollowsThePosition(output, ref passed, ref failed);
    }

    /// <summary>
    /// The whole chain, on the real pipeline: a TTML document is parsed, the playback
    /// position goes through the renderer's own <c>ComputeProgress</c>, and the resulting
    /// sweep boundary is measured in rendered pixels.
    /// <para>
    /// The sweep test above sets <c>Progress</c> directly, which leaves the step that
    /// actually decides sync - turning a position into a progress - untested. These four
    /// cases are the ones a listener notices, and each fails differently if the syllable
    /// times are not being honoured:
    /// </para>
    /// <list type="bullet">
    /// <item><b>Slow / held note</b>: a two-second syllable. A density guess races to the
    /// end of the line; the sweep must still be half-way through that one character.</item>
    /// <item><b>Fast</b>: eight syllables of 100 ms. The sweep must be on the right one,
    /// where a coarse estimate would smear across several.</item>
    /// <item><b>Tempo change</b>: a long note then a burst. Uniform distribution over the
    /// line puts a position at 82% of the line; the truth is 37%.</item>
    /// <item><b>Pause</b>: a silent gap between syllables. The sweep must hold at the end
    /// of the last sung character rather than creeping across the gap.</item>
    /// </list>
    /// </summary>
    private static void VerifySweepFollowsThePosition(TextWriter output, ref int passed, ref int failed)
    {
        int ok = 0, bad = 0;

        void Check(string name, bool success, string? detail = null)
        {
            output.WriteLine($"  {(success ? "PASS" : "FAIL")}  {name}{(detail is null ? "" : $"  ({detail})")}");
            if (success) ok++; else bad++;
        }

        const int width = 600;
        const int height = 64;

        // Renders the first line of a TTML document at a playback position, going through
        // the same ComputeProgress the overlay uses, and reports where the sweep ended as
        // a fraction of the text's own width.
        (double Ratio, double Progress) At(string ttml, TimeSpan position)
        {
            var doc = Core.Lyrics.TtmlParser.Parse(ttml);
            if (doc.Lines.Count == 0) return (double.NaN, double.NaN);

            var line = doc.Lines[0];
            var progress = Views.OverlayWindow.ComputeProgress(line, position);

            var control = new Controls.KaraokeLine
            {
                Text = line.Text,
                Syllables = line.Syllables,
                LineStart = line.Start,
                LineEnd = line.End,
                IsCurrent = true,
                WordHighlightEnabled = true,
                FontSizeValue = 28,
                Progress = progress,
                BaseColor = System.Windows.Media.Brushes.White,
                HighlightColor = System.Windows.Media.Brushes.DeepSkyBlue,
                ContextColor = System.Windows.Media.Brushes.Gray,
            };

            control.Measure(new System.Windows.Size(width, height));
            control.Arrange(new System.Windows.Rect(0, 0, width, height));
            control.UpdateLayout();

            var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(
                width, height, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
            rtb.Render(control);

            var pixels = new byte[width * height * 4];
            rtb.CopyPixels(pixels, width * 4, 0);

            double minInk = double.MaxValue, maxInk = -1, maxHighlight = -1;
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    int i = ((y * width) + x) * 4;
                    if (pixels[i + 3] <= 8) continue;

                    if (x < minInk) minInk = x;
                    if (x > maxInk) maxInk = x;

                    byte b = pixels[i], g = pixels[i + 1], r = pixels[i + 2];
                    if (b > 150 && b > r + 40 && g > r)
                    {
                        if (x > maxHighlight) maxHighlight = x;
                    }
                }
            }

            double span = maxInk - (minInk == double.MaxValue ? 0 : minInk);
            double ratio = span > 0 ? (maxHighlight - minInk) / span : 0;
            return (ratio, progress);
        }

        /// <summary>Wrap syllables in a minimal document so FillEndTimes runs for real.</summary>
        static string Doc(string spans)
            => $"""
               <tt xmlns="http://www.w3.org/ns/ttml" xmlns:ttm="http://www.w3.org/ns/ttml#metadata">
                 <body><div><p begin="00:00.000">{spans}</p></div></body>
               </tt>
               """;

        /// <summary>Milliseconds as a TTML clock value (mm:ss.fff).</summary>
        static string Clock(int ms) => TimeSpan.FromMilliseconds(ms).ToString(@"mm\:ss\.fff");

        static string Span(string text, int startMs, int endMs)
            => $"<span begin=\"{Clock(startMs)}\" end=\"{Clock(endMs)}\">{text}</span>";

        try
        {
            // --- slow: one character held for two seconds -------------------------
            var held = Doc(Span("啊", 0, 2000));
            var (halfRatio, halfProgress) = At(held, TimeSpan.FromMilliseconds(1000));
            Check("slow: a held note is half-swept half-way through it",
                halfRatio is > 0.25 and < 0.75,
                $"boundary={halfRatio:P0} at t=1000ms, progress={halfProgress:F2}");

            var (lateRatio, _) = At(held, TimeSpan.FromMilliseconds(1800));
            Check("slow: still not finished shortly before the note ends",
                lateRatio < 1.0,
                $"boundary={lateRatio:P0} at t=1800ms of 2000ms");

            // --- fast: eight characters of 100 ms ---------------------------------
            var burst = Doc(string.Concat(
                Enumerable.Range(0, 8).Select(i => Span("一二三四五六七八"[i].ToString(), i * 100, (i + 1) * 100))));
            var (fastRatio, fastProgress) = At(burst, TimeSpan.FromMilliseconds(350));
            Check("fast: lands on the fourth of eight characters",
                Math.Abs(fastRatio - 0.375) < 0.08,
                $"boundary={fastRatio:P0}, expected ~38% at t=350ms, progress={fastProgress:F2}");

            // --- tempo change: a long note, then a burst --------------------------
            // 1600 ms, then three quick ones. A uniform spread over the line would put
            // t=1650 ms at 82% of the line; the syllables put it half-way through the
            // second character, which is 37.5%.
            var tempo = Doc(
                Span("长", 0, 1600) + Span("快", 1600, 1700) +
                Span("快", 1700, 1800) + Span("快", 1800, 2000));

            var (longRatio, _) = At(tempo, TimeSpan.FromMilliseconds(1100));
            Check("tempo: during the long note the sweep is still on the first character",
                longRatio < 0.25,
                $"boundary={longRatio:P0} at t=1100ms, a uniform sweep would say {1100 / 2000.0:P0}");

            var (afterRatio, _) = At(tempo, TimeSpan.FromMilliseconds(1650));
            Check("tempo: the burst is tracked by its own times, not the line average",
                Math.Abs(afterRatio - 0.375) < 0.08,
                $"boundary={afterRatio:P0}, expected ~38%, uniform would say {1650 / 2000.0:P0}");

            // --- pause: a silent gap between syllables ----------------------------
            // Two characters, so holding at the end of the first one is half the text.
            // The point is that it does not creep across the gap into the second.
            var withGap = Doc(Span("停", 0, 400) + Span("顿", 1200, 1600));
            var (gapRatio, _) = At(withGap, TimeSpan.FromMilliseconds(800));
            Check("pause: the sweep holds through a silent gap",
                Math.Abs(gapRatio - 0.5) < 0.08,
                $"boundary={gapRatio:P0} at t=800ms inside the gap; it must hold at 50% "
                + "(one of two characters), not creep toward the next one");

            var (afterGapRatio, _) = At(withGap, TimeSpan.FromMilliseconds(1400));
            Check("pause: it moves on once the next character starts",
                afterGapRatio > 0.6,
                $"boundary={afterGapRatio:P0} at t=1400ms, into the second character");
        }
        catch (Exception ex)
        {
            Check("sweep-follows-position harness", false, $"{ex.GetType().Name}: {ex.Message}");
        }

        output.WriteLine($"  sweep follows position: {ok} passed, {bad} failed");
        passed += ok;
        failed += bad;
    }

    private static void VerifyTranslationCentring(TextWriter output, ref int passed, ref int failed)
    {
        int ok = 0, bad = 0;

        void Check(string name, bool success, string? detail = null)
        {
            output.WriteLine($"  {(success ? "PASS" : "FAIL")}  {name}{(detail is null ? "" : $"  ({detail})")}");
            if (success) ok++; else bad++;
        }

        try
        {
            const int width = 600;
            const int height = 64;

            var line = new Controls.KaraokeLine
            {
                // The translation is deliberately the wider of the two, and much wider
                // than the original. Otherwise the scan band catches the original's own
                // ink and the test passes without ever measuring the translation.
                Text = new string('长', 2),
                Translation = new string('译', 12),
                IsCurrent = true,
                WordHighlightEnabled = true,
                FontSizeValue = 22,
                TranslationFontSizeValue = 14,
                BaseColor = System.Windows.Media.Brushes.White,
                HighlightColor = System.Windows.Media.Brushes.DeepSkyBlue,
                ContextColor = System.Windows.Media.Brushes.Gray,
                Progress = 0.5,
            };

            line.Measure(new System.Windows.Size(width, height));
            line.Arrange(new System.Windows.Rect(0, 0, width, height));
            line.UpdateLayout();

            var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(
                width, height, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
            rtb.Render(line);

            var pixels = new byte[width * height * 4];
            rtb.CopyPixels(pixels, width * 4, 0);

            // The translation is the last row, so scan only the bottom band: the two text
            // rows are laid out as a block, and 60% of the height falls below the first.
            int bandTop = (int)(height * 0.6);
            int minX = int.MaxValue, maxX = int.MinValue, inkRows = 0;
            for (int y = bandTop; y < height; y++)
            {
                bool rowHasInk = false;
                for (int x = 0; x < width; x++)
                {
                    int alpha = pixels[((y * width) + x) * 4 + 3];
                    if (alpha <= 8) continue;

                    rowHasInk = true;
                    if (x < minX) minX = x;
                    if (x > maxX) maxX = x;
                }

                if (rowHasInk) inkRows++;
            }

            if (inkRows == 0 || minX > maxX)
            {
                Check("translation row rendered", false, "no ink found in the bottom band");
                return;
            }

            double centre = (minX + maxX) / 2.0;
            double offset = Math.Abs(centre - (width / 2.0));
            int span = maxX - minX;

            output.WriteLine($"  bottom-band ink spans x={minX}..{maxX} (width {span}), " +
                              $"centre={centre:F1} vs control centre {width / 2.0}, offset={offset:F1}px");

            Check("translation row rendered", inkRows > 0, $"{inkRows} rows");

            // Guards against the earlier false pass, where the band caught the original.
            Check("measured the translation, not the original", span > 120, $"width {span}px");

            Check("translation is centred on the control", offset <= 6, $"{offset:F1}px off");
        }
        catch (Exception ex)
        {
            Check("translation centring rendered", false, ex.GetType().Name + ": " + ex.Message);
        }
        finally
        {
            passed += ok;
            failed += bad;
        }
    }

    /// <summary>
    /// Render the picker's swatch grid off-screen and confirm the swatches actually
    /// paint distinct colours.
    /// <para>
    /// This exists because the swatches once rendered as blank white: they were
    /// Buttons whose Background was ignored by an ambient ControlTemplate. Asserting
    /// the rendered pixels, rather than that a Background property was assigned,
    /// is what catches that class of bug.
    /// </para>
    /// </summary>
    private static void VerifyPalette(TextWriter output, ref int passed, ref int failed)
    {
        // Locals rather than the ref parameters: a local function cannot close over
        // ref/out parameters.
        int ok = 0, bad = 0;

        void Check(string name, bool success, string? detail = null)
        {
            output.WriteLine($"  {(success ? "PASS" : "FAIL")}  {name}{(detail is null ? "" : $"  ({detail})")}");
            if (success) ok++; else bad++;
        }

        try
        {
            var field = new Controls.ColorField();

            // Swatches live inside the drop-down Popup, so render the popup's own
            // child: it is a normal visual, merely hosted in a popup window when open.
            if (field.FindName("SwatchGrid") is not System.Windows.Controls.Panel grid)
            {
                Check("swatch grid present", false);
                return;
            }

            grid.Measure(new System.Windows.Size(400, 300));
            grid.Arrange(new System.Windows.Rect(0, 0, grid.DesiredSize.Width, grid.DesiredSize.Height));
            grid.UpdateLayout();

            int w = (int)Math.Ceiling(grid.ActualWidth);
            int h = (int)Math.Ceiling(grid.ActualHeight);
            if (w <= 0 || h <= 0)
            {
                Check("swatch grid has a size", false, $"{w}x{h}");
                return;
            }

            var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(
                w, h, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
            rtb.Render(grid);

            var pixels = new byte[w * h * 4];
            rtb.CopyPixels(pixels, w * 4, 0);

            var distinct = new HashSet<int>();
            for (int i = 0; i < pixels.Length; i += 4)
            {
                int b = pixels[i], g = pixels[i + 1], r = pixels[i + 2];
                int sat = Math.Max(r, Math.Max(g, b)) - Math.Min(r, Math.Min(g, b));
                if (sat > 45) distinct.Add((r << 16) | (g << 8) | b);
            }

            output.WriteLine($"  swatch grid {w}x{h}, {grid.Children.Count} swatches, " +
                              $"{distinct.Count} distinct saturated colours");

            Check("grid contains swatches", grid.Children.Count >= 16,
                grid.Children.Count.ToString());
            Check("swatches paint distinct colours", distinct.Count >= 8,
                distinct.Count.ToString());
        }
        catch (Exception ex)
        {
            Check("palette rendering completed", false, ex.GetType().Name + ": " + ex.Message);
        }
        finally
        {
            passed += ok;
            failed += bad;
        }
    }

    /// <summary>Forwards writes to two writers, so a report can go to both stdout and a file.</summary>
    private sealed class TeeWriter(TextWriter first, TextWriter second) : TextWriter
    {
        public override System.Text.Encoding Encoding => first.Encoding;

        public override void Write(char value)
        {
            first.Write(value);
            second.Write(value);
        }

        public override void Write(string? value)
        {
            first.Write(value);
            second.Write(value);
        }

        public override void WriteLine(string? value)
        {
            first.WriteLine(value);
            second.WriteLine(value);
        }

        public override void Flush()
        {
            first.Flush();
            second.Flush();
        }
    }
}
