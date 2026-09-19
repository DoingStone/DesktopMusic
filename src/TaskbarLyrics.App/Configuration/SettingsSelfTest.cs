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
