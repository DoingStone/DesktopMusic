# Fonts

This folder is empty in the repository **on purpose**.

`TaskbarLyrics.App` offers `MiSans（内置·汽水同款）` as the first entry in the
settings font drop-down. That option expects a typeface file here:

```
Fonts/MiSans-subset.ttf
```

No such file is committed. The subset the feature was developed against was carved out of
another application's installation, and a typeface's licence is its own — it is not this
project's to redistribute (this repository is MIT; see `docs/soda-taskbar-lyrics-parity.md`
§10.5). You may supply your own:

1. Obtain MiSans (or any typeface you are licensed to use) and convert it to TTF.
2. Rename the result to `MiSans-subset.ttf` and place it in this folder.
3. Rebuild. `TaskbarLyrics.App.csproj` embeds the file as a WPF Resource *and* copies it
   next to the executable; `Configuration/BundledFonts.cs` probes both and only reports the
   family name when the glyph face actually resolves.

Without the file the build is unchanged — the project builds with no font items at all —
and `BundledFonts.Resolve` falls back to `Microsoft YaHei UI`, so the font drop-down simply
loses its first entry's advantage instead of rendering wrong glyphs.

A ready-made recipe (needs `fonttools` + `brotli`), pinning the weight to the overlay's
default `SemiBold` and subsetting to GB2312 + Latin + common punctuation:

```python
from fontTools.ttLib import TTFont
from fontTools.varLib import instancer
from fontTools import subset

font = TTFont("MiSansVF.ttf")
font = instancer.instantiateVariableFont(font, {"wght": 600}, updateFontNames=False)
subset.main(["MiSansVF.ttf", "--output-file=MiSans-subset.ttf",
             "--unicodes=U+0020-00FF,U+2000-2070,U+3000-3100,U+FF00-FF60",
             "--text-file=gb2312.txt"])   # one GB2312 character per line
```

Keep the resulting family name as `MiSans` (or edit `BundledFonts.MiSansFamily` to match);
the settings file stores the *display* name, and `BundledFonts.Resolve` maps it to the
resolved URI.
