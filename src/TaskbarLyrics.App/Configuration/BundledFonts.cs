using System.IO;
using System.Windows;
using System.Windows.Media;

namespace TaskbarLyrics.App.Configuration;

/// <summary>
/// Maps the user-facing font name stored in settings to the family WPF should
/// actually render with.
///
/// The bundled MiSans subset (Soda Music's own typeface, see
/// docs/soda-taskbar-lyrics-parity.md §10.5) ships inside this assembly as a
/// Resource *and* next to the executable, so it is addressed by URI rather than by
/// an installed family name. Every candidate is validated with
/// <see cref="Typeface.TryGetGlyphTypeface"/> before use - a URI that does not
/// resolve would otherwise silently render a fallback face. Everything that is not
/// the bundled family passes through unchanged, which keeps settings.json readable.
/// </summary>
internal static class BundledFonts
{
    /// <summary>How the bundled family is listed in the settings combo box.</summary>
    public const string MiSansDisplayName = "MiSans（内置·汽水同款）";

    private const string MiSansFamily = "MiSans";

    /// <summary>
    /// The bundled file is a <c>wght=600</c> static instance of MiSans VF renamed to
    /// the family "MiSans"/"SemiBold", so the default SemiBold weight renders with the
    /// real face instead of WPF's synthetic emboldening.
    /// </summary>
    private static readonly string[] MiSansPackCandidates =
    {
        // Embedded WPF resource. Note the lower-cased, dash-preserving resource key
        // WPF's ResourcesGenerator produces ("fonts/misans-subset.ttf").
        "pack://application:,,,/TaskbarLyrics;component/fonts/misans-subset.ttf#" + MiSansFamily,
        "pack://application:,,,/fonts/misans-subset.ttf#" + MiSansFamily,
        "pack://application:,,,/TaskbarLyrics;component/Fonts/MiSans-subset.ttf#" + MiSansFamily,
        // Loose file copied next to the executable (this is what actually resolves in
        // practice; kept last only because the pack form is shorter to log).
        "./Fonts/MiSans-subset.ttf#" + MiSansFamily,
    };

    private static string? _miSansResolved;

    public static string Resolve(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return string.Empty;

        var trimmed = name.Trim();
        if (!IsMiSans(trimmed)) return trimmed;

        // Probing loads the font file, so the answer is cached: it cannot change while
        // the process runs, and Resolve is also called from a diagnostic log line.
        return _miSansResolved ??= ProbeMiSans();
    }

    private static string ProbeMiSans()
    {
        var loose = LooseFileCandidate();
        if (loose.Length > 0 && IsResolvable(loose)) return loose;

        foreach (var candidate in MiSansPackCandidates)
        {
            if (IsResolvable(candidate)) return candidate;
        }

        // Nothing worked: fall back to a system family rather than to a URI that would
        // render as an arbitrary default face.
        return "Microsoft YaHei UI";
    }

    /// <summary>
    /// Absolute <c>file:///</c> URI for the copy that lives next to the executable.
    /// An absolute URI is the only form WPF resolves without an ambient base URI, which
    /// makes it the dependable one for a font loaded from code.
    /// </summary>
    private static string LooseFileCandidate()
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "Fonts", "MiSans-subset.ttf");
            return File.Exists(path) ? new Uri(path).AbsoluteUri + "#" + MiSansFamily : string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static bool IsMiSans(string name) =>
        name.Equals(MiSansDisplayName, StringComparison.OrdinalIgnoreCase) ||
        name.Equals(MiSansFamily, StringComparison.OrdinalIgnoreCase) ||
        name.Equals("MiSans VF", StringComparison.OrdinalIgnoreCase);

    private static bool IsResolvable(string family)
    {
        try
        {
            var typeface = new Typeface(
                new FontFamily(family),
                FontStyles.Normal,
                FontWeights.SemiBold,
                FontStretches.Normal);
            return typeface.TryGetGlyphTypeface(out _);
        }
        catch
        {
            return false;
        }
    }
}
