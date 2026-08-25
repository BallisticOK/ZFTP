using System.IO;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Media;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;
using ZFTP.Core;
using Application = System.Windows.Application;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;
using Colors = System.Windows.Media.Colors;
using FontFamily = System.Windows.Media.FontFamily;

namespace ZFTP.App;

public sealed class ThemeDefinition
{
    public int SchemaVersion { get; set; } = 2;
    public string Id { get; set; } = "custom-theme";
    public string Name { get; set; } = "Custom Theme";
    public string Author { get; set; } = "Community";
    public string Description { get; set; } = "A custom ZFTP theme.";
    public string Base { get; set; } = "Dark";
    public string Backdrop { get; set; } = "Mica";
    public string Accent { get; set; } = "#2D7DD2";
    public string WindowBackground { get; set; } = "#FF0E1117";
    public string Surface { get; set; } = "#FF161B22";
    public string SurfaceAlt { get; set; } = "#FF1F2630";
    public string Border { get; set; } = "#FF30363D";
    public string Text { get; set; } = "#FFF0F4F8";
    public string TextMuted { get; set; } = "#FFA4AFBA";
    public string Selection { get; set; } = "#553B82F6";
    public string Hover { get; set; } = "#22FFFFFF";
    public string Badge { get; set; } = "#332D7DD2";
    public string StatusBar { get; set; } = "#FF10151C";
    public string Success { get; set; } = "#FF4ADE80";
    public string Warning { get; set; } = "#FFFBBF24";
    public string Error { get; set; } = "#FFFB7185";
    public string FontFamily { get; set; } = "Segoe UI";
    public double CornerRadius { get; set; } = 10;
    public double RowHeight { get; set; } = 48;
    public Dictionary<string, JsonElement> Resources { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    [JsonIgnore] public bool IsBuiltIn { get; init; }
    [JsonIgnore] public string? SourcePath { get; init; }

    public override string ToString() => Name;
}

public sealed record ThemeLoadResult(IReadOnlyList<ThemeDefinition> Themes, IReadOnlyList<string> Errors);

public static class ThemeCatalog
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true,
    };

    public static string ThemeFolderPath { get; } = Path.Combine(ProfileStore.FolderPath, "Themes");

    public static ThemeLoadResult LoadAll()
    {
        EnsureThemeFolder();
        var themes = BuiltIns().ToList();
        var errors = new List<string>();
        var ids = new HashSet<string>(themes.Select(t => t.Id), StringComparer.OrdinalIgnoreCase);

        foreach (var path in Directory.EnumerateFiles(ThemeFolderPath, "*.json", SearchOption.TopDirectoryOnly))
        {
            try
            {
                var raw = JsonSerializer.Deserialize<ThemeDefinition>(File.ReadAllText(path), JsonOptions)
                          ?? throw new InvalidDataException("The file did not contain a theme object.");
                var theme = Normalize(raw, path);
                if (!ids.Add(theme.Id))
                    throw new InvalidDataException($"Theme id '{theme.Id}' is already in use.");
                themes.Add(theme);
            }
            catch (Exception ex)
            {
                errors.Add($"{Path.GetFileName(path)}: {ex.Message}");
            }
        }

        return new ThemeLoadResult(themes, errors);
    }

    public static void EnsureThemeFolder()
    {
        Directory.CreateDirectory(ThemeFolderPath);

        var examplePath = Path.Combine(ThemeFolderPath, "theme-template-v2.json.example");
        if (!File.Exists(examplePath))
        {
            var template = new ThemeDefinition
            {
                Id = "my-awesome-theme",
                Name = "My Awesome Theme",
                Author = "Your name",
                Description = "Copy this file, rename it to .json, then edit anything you like.",
                Base = "Dark",
                Backdrop = "None",
                Accent = "#FF4FD1C5",
                WindowBackground = "#FF090B10",
                Surface = "#FF10151D",
                SurfaceAlt = "#FF17202B",
                Border = "#FF2E4053",
                Text = "#FFF4F7FB",
                TextMuted = "#FF91A3B5",
                Selection = "#554FD1C5",
                Hover = "#224FD1C5",
                Badge = "#334FD1C5",
                StatusBar = "#FF07090D",
                Success = "#FF51CF66",
                Warning = "#FFFFC857",
                Error = "#FFFF6B6B",
                FontFamily = "Consolas",
                CornerRadius = 4,
                RowHeight = 46,
                Resources = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase)
                {
                    ["ZftpBaseFontSize"] = JsonSerializer.SerializeToElement(13.0),
                    ["ZftpSectionFontSize"] = JsonSerializer.SerializeToElement(18.0),
                    ["ZftpPageMargin"] = JsonSerializer.SerializeToElement(new { Type = "Thickness", Value = "20,20,20,16" }),
                    ["ZftpCardPadding"] = JsonSerializer.SerializeToElement(new { Type = "Thickness", Value = "16,14" }),
                },
            };
            File.WriteAllText(examplePath, JsonSerializer.Serialize(template, JsonOptions));
        }

        var readmePath = Path.Combine(ThemeFolderPath, "README-v2.txt");
        if (!File.Exists(readmePath))
        {
            File.WriteAllText(readmePath,
                "ZFTP LOCAL THEMES\r\n\r\n" +
                "1. Copy theme-template-v2.json.example to a new file ending in .json, or use Create from selected in Settings.\r\n" +
                "2. Give it a unique Id and Name.\r\n" +
                "3. Edit the core colors, font, corner radius, row height, Base (Dark/Light), and Backdrop.\r\n" +
                "4. Use the Resources object to override any ZFTP/WPF resource token for advanced styling.\r\n" +
                "5. ZFTP watches this folder and reloads themes automatically. You can also press Reload themes.\r\n\r\n" +
                "Colors accept #RRGGBB or #AARRGGBB. Backdrop can be None, Mica, or any backdrop supported by your installed WPF-UI version.\r\n" +
                "SchemaVersion 2 adds typed resource overrides. SchemaVersion 1 themes are still supported. Invalid values are skipped instead of crashing the app.\r\n");
        }
    }

    public static string CreateCustomCopy(ThemeDefinition source)
    {
        EnsureThemeFolder();

        var baseId = Slug(source.Id) + "-custom";
        var id = baseId;
        var suffix = 2;
        var existingIds = LoadAll().Themes.Select(t => t.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        while (existingIds.Contains(id) || File.Exists(Path.Combine(ThemeFolderPath, id + ".json")))
        {
            id = $"{baseId}-{suffix++}";
        }

        var copy = new ThemeDefinition
        {
            SchemaVersion = 2,
            Id = id,
            Name = source.Name + " Custom",
            Author = "You",
            Description = $"A fully customizable theme based on {source.Name}.",
            Base = source.Base,
            Backdrop = source.Backdrop,
            Accent = source.Accent,
            WindowBackground = source.WindowBackground,
            Surface = source.Surface,
            SurfaceAlt = source.SurfaceAlt,
            Border = source.Border,
            Text = source.Text,
            TextMuted = source.TextMuted,
            Selection = source.Selection,
            Hover = source.Hover,
            Badge = source.Badge,
            StatusBar = source.StatusBar,
            Success = source.Success,
            Warning = source.Warning,
            Error = source.Error,
            FontFamily = source.FontFamily,
            CornerRadius = source.CornerRadius,
            RowHeight = source.RowHeight,
            Resources = source.Resources.ToDictionary(p => p.Key, p => p.Value.Clone(), StringComparer.OrdinalIgnoreCase),
        };

        var path = Path.Combine(ThemeFolderPath, id + ".json");
        File.WriteAllText(path, JsonSerializer.Serialize(copy, JsonOptions));
        return path;
    }

    private static ThemeDefinition Normalize(ThemeDefinition t, string path)
    {
        if (t.SchemaVersion is not (1 or 2))
            throw new InvalidDataException($"Unsupported SchemaVersion {t.SchemaVersion}; expected 1 or 2.");
        if (string.IsNullOrWhiteSpace(t.Id)) throw new InvalidDataException("Id is required.");
        if (string.IsNullOrWhiteSpace(t.Name)) throw new InvalidDataException("Name is required.");

        string[] colors =
        {
            t.Accent, t.WindowBackground, t.Surface, t.SurfaceAlt, t.Border, t.Text, t.TextMuted,
            t.Selection, t.Hover, t.Badge, t.StatusBar, t.Success, t.Warning, t.Error,
        };
        if (colors.Any(c => !TryColor(c, out _)))
            throw new InvalidDataException("One or more color values are invalid. Use #RRGGBB or #AARRGGBB.");

        t.Id = t.Id.Trim();
        t.Name = t.Name.Trim();
        t.Author = string.IsNullOrWhiteSpace(t.Author) ? "Community" : t.Author.Trim();
        t.Description = t.Description?.Trim() ?? "";
        t.Base = t.Base.Equals("Light", StringComparison.OrdinalIgnoreCase) ? "Light" : "Dark";
        t.Backdrop = string.IsNullOrWhiteSpace(t.Backdrop) ? "Mica" : t.Backdrop.Trim();
        t.FontFamily = string.IsNullOrWhiteSpace(t.FontFamily) ? "Segoe UI" : t.FontFamily.Trim();
        t.CornerRadius = Math.Clamp(t.CornerRadius, 0, 32);
        t.RowHeight = Math.Clamp(t.RowHeight, 36, 80);

        var resources = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        if (t.Resources is not null)
        {
            foreach (var pair in t.Resources)
            {
                if (string.IsNullOrWhiteSpace(pair.Key))
                    throw new InvalidDataException("Resource override names cannot be empty.");
                resources[pair.Key.Trim()] = pair.Value.Clone();
            }
        }

        return new ThemeDefinition
        {
            SchemaVersion = t.SchemaVersion,
            Id = t.Id,
            Name = t.Name,
            Author = t.Author,
            Description = t.Description,
            Base = t.Base,
            Backdrop = t.Backdrop,
            Accent = t.Accent,
            WindowBackground = t.WindowBackground,
            Surface = t.Surface,
            SurfaceAlt = t.SurfaceAlt,
            Border = t.Border,
            Text = t.Text,
            TextMuted = t.TextMuted,
            Selection = t.Selection,
            Hover = t.Hover,
            Badge = t.Badge,
            StatusBar = t.StatusBar,
            Success = t.Success,
            Warning = t.Warning,
            Error = t.Error,
            FontFamily = t.FontFamily,
            CornerRadius = t.CornerRadius,
            RowHeight = t.RowHeight,
            Resources = resources,
            SourcePath = path,
        };
    }

    private static IEnumerable<ThemeDefinition> BuiltIns()
    {
        yield return Modern("dark-blue", "Dark Blue", "#2D7DD2");
        yield return Modern("midnight-purple", "Midnight Purple", "#8B5CF6");
        yield return Modern("forest-green", "Forest Green", "#22C55E");
        yield return Modern("sunset-orange", "Sunset Orange", "#F97316");
        yield return Modern("crimson-red", "Crimson Red", "#EF4444");
        yield return Modern("ocean-cyan", "Ocean Cyan", "#06B6D4");
        yield return Modern("rose-pink", "Rose Pink", "#EC4899");
        yield return Modern("amber-gold", "Amber Gold", "#F5B300");
        yield return Modern("light-blue", "Light Blue", "#2563EB", light: true);
        yield return Modern("light-green", "Light Green", "#16A34A", light: true);

        yield return BuiltIn(new ThemeDefinition
        {
            Id = "hacker-2000", Name = "Hacker 2000", Author = "ZFTP",
            Description = "No glass. Hard black panels, phosphor green text, square edges and a terminal font.",
            Base = "Dark", Backdrop = "None", Accent = "#39FF14", WindowBackground = "#FF020402",
            Surface = "#FF050805", SurfaceAlt = "#FF091009", Border = "#FF1D6B18", Text = "#FF72FF63",
            TextMuted = "#FF42A83A", Selection = "#6639FF14", Hover = "#2239FF14", Badge = "#3339FF14",
            StatusBar = "#FF000000", Success = "#FF72FF63", Warning = "#FFFFFF66", Error = "#FFFF4D4D",
            FontFamily = "Consolas", CornerRadius = 0, RowHeight = 44,
        });
        yield return BuiltIn(new ThemeDefinition
        {
            Id = "terminal-amber", Name = "Terminal Amber", Author = "ZFTP",
            Description = "Early workstation energy: black, warm amber, zero glass and almost no rounding.",
            Base = "Dark", Backdrop = "None", Accent = "#FFB000", WindowBackground = "#FF050300",
            Surface = "#FF0B0700", SurfaceAlt = "#FF151000", Border = "#FF5F4300", Text = "#FFFFC44D",
            TextMuted = "#FFC58B25", Selection = "#66FFB000", Hover = "#22FFB000", Badge = "#33FFB000",
            StatusBar = "#FF020100", Success = "#FFFFD166", Warning = "#FFFFA000", Error = "#FFFF5D5D",
            FontFamily = "Cascadia Mono", CornerRadius = 2, RowHeight = 44,
        });
        yield return BuiltIn(new ThemeDefinition
        {
            Id = "sakura-night", Name = "Sakura Night · Anime", Author = "ZFTP",
            Description = "Soft midnight violet with sakura pink and lavender highlights.",
            Base = "Dark", Backdrop = "Mica", Accent = "#FFFE6FAE", WindowBackground = "#FF171322",
            Surface = "#DD211A30", SurfaceAlt = "#FF2C2240", Border = "#FF5E416E", Text = "#FFFFF2F8",
            TextMuted = "#FFC8AEC8", Selection = "#66FE6FAE", Hover = "#22FEA5C9", Badge = "#44FE6FAE",
            StatusBar = "#EE110E19", Success = "#FF8FE3B2", Warning = "#FFFFCF70", Error = "#FFFF708D",
            FontFamily = "Segoe UI Variable Text", CornerRadius = 14, RowHeight = 50,
        });
        yield return BuiltIn(new ThemeDefinition
        {
            Id = "mecha-neon", Name = "Mecha Neon · Anime", Author = "ZFTP",
            Description = "Deep navy armor, electric cyan systems and magenta warning lights.",
            Base = "Dark", Backdrop = "Mica", Accent = "#FF21D4FD", WindowBackground = "#FF070B18",
            Surface = "#DD0D1428", SurfaceAlt = "#FF131F3C", Border = "#FF234A6B", Text = "#FFEAFBFF",
            TextMuted = "#FF86A9BD", Selection = "#6621D4FD", Hover = "#2221D4FD", Badge = "#3321D4FD",
            StatusBar = "#F0050812", Success = "#FF45F0B8", Warning = "#FFFFD166", Error = "#FFFF4FA3",
            FontFamily = "Segoe UI Variable Text", CornerRadius = 8, RowHeight = 48,
        });
        yield return BuiltIn(new ThemeDefinition
        {
            Id = "moonlit-shrine", Name = "Moonlit Shrine · Anime", Author = "ZFTP",
            Description = "Ink blue, shrine red and moon-paper text with a calm cinematic feel.",
            Base = "Dark", Backdrop = "Mica", Accent = "#FFE33E50", WindowBackground = "#FF0C1320",
            Surface = "#E0141D2C", SurfaceAlt = "#FF1B2A3B", Border = "#FF3B4B5E", Text = "#FFF3EDE2",
            TextMuted = "#FFB5AD9F", Selection = "#66E33E50", Hover = "#22E33E50", Badge = "#33E33E50",
            StatusBar = "#F0080D16", Success = "#FF7DCEA0", Warning = "#FFEBCB8B", Error = "#FFE85D6A",
            FontFamily = "Yu Gothic UI", CornerRadius = 10, RowHeight = 49,
        });
        yield return BuiltIn(new ThemeDefinition
        {
            Id = "vaporwave-2001", Name = "Vaporwave 2001", Author = "ZFTP",
            Description = "A loud Y2K desktop fever dream in ultraviolet, cyan and hot pink.",
            Base = "Dark", Backdrop = "None", Accent = "#FF00F5FF", WindowBackground = "#FF120522",
            Surface = "#FF21083A", SurfaceAlt = "#FF321052", Border = "#FF7433A5", Text = "#FFFFE8FF",
            TextMuted = "#FFCEA6D9", Selection = "#66FF4FD8", Hover = "#2200F5FF", Badge = "#4400F5FF",
            StatusBar = "#FF090012", Success = "#FF65FFB5", Warning = "#FFFFFF78", Error = "#FFFF4FD8",
            FontFamily = "Segoe UI", CornerRadius = 5, RowHeight = 48,
        });
    }

    private static ThemeDefinition Modern(string id, string name, string accent, bool light = false)
    {
        return BuiltIn(new ThemeDefinition
        {
            Id = id, Name = name, Author = "ZFTP",
            Description = light ? "Clean light theme with a focused accent." : "Clean dark theme with a focused accent.",
            Base = light ? "Light" : "Dark", Backdrop = "Mica", Accent = accent,
            WindowBackground = light ? "#FFF6F8FB" : "#FF0E1117",
            Surface = light ? "#EFFFFFFF" : "#DD161B22",
            SurfaceAlt = light ? "#FFE9EEF5" : "#FF1F2630",
            Border = light ? "#FFD5DCE5" : "#FF30363D",
            Text = light ? "#FF18212B" : "#FFF0F4F8",
            TextMuted = light ? "#FF66717D" : "#FFA4AFBA",
            Selection = "#55" + accent.TrimStart('#'), Hover = light ? "#12000000" : "#18FFFFFF",
            Badge = "#33" + accent.TrimStart('#'),
            StatusBar = light ? "#EFFFFFFF" : "#EE10151C",
            Success = "#FF34A853", Warning = "#FFF9AB00", Error = "#FFE84545",
            FontFamily = "Segoe UI Variable Text", CornerRadius = 10, RowHeight = 48,
        });
    }

    private static ThemeDefinition BuiltIn(ThemeDefinition t) => new()
    {
        SchemaVersion = t.SchemaVersion, Id = t.Id, Name = t.Name, Author = t.Author, Description = t.Description,
        Base = t.Base, Backdrop = t.Backdrop, Accent = t.Accent, WindowBackground = t.WindowBackground,
        Surface = t.Surface, SurfaceAlt = t.SurfaceAlt, Border = t.Border, Text = t.Text, TextMuted = t.TextMuted,
        Selection = t.Selection, Hover = t.Hover, Badge = t.Badge, StatusBar = t.StatusBar, Success = t.Success,
        Warning = t.Warning, Error = t.Error, FontFamily = t.FontFamily, CornerRadius = t.CornerRadius,
        RowHeight = t.RowHeight,
        Resources = t.Resources.ToDictionary(p => p.Key, p => p.Value.Clone(), StringComparer.OrdinalIgnoreCase),
        IsBuiltIn = true,
    };

    private static string Slug(string value)
    {
        var chars = value.Trim().ToLowerInvariant()
            .Select(c => char.IsLetterOrDigit(c) ? c : '-')
            .ToArray();
        var slug = new string(chars);
        while (slug.Contains("--", StringComparison.Ordinal)) slug = slug.Replace("--", "-", StringComparison.Ordinal);
        var clean = slug.Trim('-');
        return clean.Length > 0 ? clean : "custom-theme";
    }

    internal static bool TryColor(string value, out Color color)
    {
        try
        {
            var parsed = ColorConverter.ConvertFromString(value);
            if (parsed is Color c) { color = c; return true; }
        }
        catch { }
        color = Colors.Transparent;
        return false;
    }
}

public static class ZftpThemeManager
{
    private sealed record ResourceBaseline(bool HadLocalValue, object? Value);

    private static readonly Dictionary<string, ResourceBaseline> ResourceBaselines = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> ActiveResourceOverrides = new(StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyList<string> Apply(ThemeDefinition theme, FluentWindow window)
    {
        var warnings = new List<string>();
        var resources = Application.Current.Resources;
        RestoreResourceOverrides(resources);

        var baseTheme = theme.Base.Equals("Light", StringComparison.OrdinalIgnoreCase)
            ? ApplicationTheme.Light
            : ApplicationTheme.Dark;

        if (!Enum.TryParse<WindowBackdropType>(theme.Backdrop, ignoreCase: true, out var backdrop))
            backdrop = WindowBackdropType.Mica;

        ApplicationThemeManager.Apply(baseTheme, backdrop, updateAccent: false);
        var accent = ParseColor(theme.Accent, Colors.DodgerBlue);
        ApplicationAccentColorManager.Apply(accent, baseTheme);

        resources["ZftpAccentBrush"] = Brush(theme.Accent, "#FF2D7DD2");
        resources["ZftpWindowBackgroundBrush"] = Brush(theme.WindowBackground, "#FF0E1117");
        resources["ZftpSurfaceBrush"] = Brush(theme.Surface, "#FF161B22");
        resources["ZftpSurfaceAltBrush"] = Brush(theme.SurfaceAlt, "#FF1F2630");
        resources["ZftpBorderBrush"] = Brush(theme.Border, "#FF30363D");
        resources["ZftpTextBrush"] = Brush(theme.Text, "#FFF0F4F8");
        resources["ZftpMutedTextBrush"] = Brush(theme.TextMuted, "#FFA4AFBA");
        resources["ZftpSelectionBrush"] = Brush(theme.Selection, "#553B82F6");
        resources["ZftpHoverBrush"] = Brush(theme.Hover, "#22FFFFFF");
        resources["ZftpBadgeBrush"] = Brush(theme.Badge, "#332D7DD2");
        resources["ZftpStatusBarBrush"] = Brush(theme.StatusBar, "#FF10151C");
        resources["ZftpSuccessBrush"] = Brush(theme.Success, "#FF4ADE80");
        resources["ZftpWarningBrush"] = Brush(theme.Warning, "#FFFBBF24");
        resources["ZftpErrorBrush"] = Brush(theme.Error, "#FFFB7185");
        resources["ZftpCornerRadius"] = new CornerRadius(theme.CornerRadius);
        resources["ZftpSmallCornerRadius"] = new CornerRadius(Math.Max(0, theme.CornerRadius * 0.55));
        resources["ZftpRowHeight"] = theme.RowHeight;

        FontFamily font;
        try { font = new FontFamily(theme.FontFamily); }
        catch { font = new FontFamily("Segoe UI"); }
        resources["ZftpThemeFontFamily"] = font;

        foreach (var pair in theme.Resources)
        {
            try
            {
                if (!ResourceBaselines.ContainsKey(pair.Key))
                {
                    var hadLocalValue = resources.Contains(pair.Key);
                    ResourceBaselines[pair.Key] = new ResourceBaseline(hadLocalValue, hadLocalValue ? resources[pair.Key] : null);
                }

                if (!TryCreateResourceValue(pair.Key, pair.Value, out var value, out var error))
                {
                    warnings.Add($"{pair.Key}: {error}");
                    continue;
                }

                resources[pair.Key] = value!;
                ActiveResourceOverrides.Add(pair.Key);
            }
            catch (Exception ex)
            {
                warnings.Add($"{pair.Key}: {ex.Message}");
            }
        }

        window.WindowBackdropType = backdrop;
        window.Background = (Brush)resources["ZftpWindowBackgroundBrush"];
        window.Foreground = (Brush)resources["ZftpTextBrush"];
        window.FontFamily = resources["ZftpThemeFontFamily"] as FontFamily ?? font;
        if (resources["ZftpBaseFontSize"] is double fontSize)
            window.FontSize = fontSize;

        return warnings;
    }

    private static void RestoreResourceOverrides(ResourceDictionary resources)
    {
        foreach (var key in ActiveResourceOverrides)
        {
            if (!ResourceBaselines.TryGetValue(key, out var baseline)) continue;
            if (baseline.HadLocalValue)
                resources[key] = baseline.Value!;
            else
                resources.Remove(key);
        }
        ActiveResourceOverrides.Clear();
    }

    private static bool TryCreateResourceValue(string key, JsonElement specification, out object? value, out string error)
    {
        string? explicitType = null;
        var raw = specification;

        if (specification.ValueKind == JsonValueKind.Object)
        {
            if (!TryGetProperty(specification, "value", out raw))
            {
                value = null;
                error = "Typed resource objects require a Value property.";
                return false;
            }
            if (TryGetProperty(specification, "type", out var typeElement) && typeElement.ValueKind == JsonValueKind.String)
                explicitType = typeElement.GetString();
        }

        object? current = null;
        try { current = Application.Current.TryFindResource(key); } catch { }

        var type = explicitType?.Trim().ToLowerInvariant() ?? InferType(current);
        if (string.IsNullOrWhiteSpace(type))
        {
            value = null;
            error = "Unknown resource. Add an explicit Type (Brush, Color, Double, Thickness, CornerRadius, FontFamily, FontWeight, GridLength, Boolean, String, or Enum).";
            return false;
        }

        try
        {
            switch (type)
            {
                case "brush":
                case "solidcolorbrush":
                    if (!TryString(raw, out var brushText) || !ThemeCatalog.TryColor(brushText, out var brushColor))
                        throw new FormatException("Expected a color such as #RRGGBB or #AARRGGBB.");
                    var brush = new SolidColorBrush(brushColor);
                    brush.Freeze();
                    value = brush;
                    break;

                case "color":
                    if (!TryString(raw, out var colorText) || !ThemeCatalog.TryColor(colorText, out var color))
                        throw new FormatException("Expected a color such as #RRGGBB or #AARRGGBB.");
                    value = color;
                    break;

                case "double":
                case "number":
                    value = ReadDouble(raw);
                    break;

                case "int":
                case "integer":
                    value = checked((int)Math.Round(ReadDouble(raw)));
                    break;

                case "thickness":
                    value = ReadThickness(raw);
                    break;

                case "cornerradius":
                case "corner-radius":
                    value = ReadCornerRadius(raw);
                    break;

                case "fontfamily":
                case "font-family":
                    value = new FontFamily(ReadString(raw));
                    break;

                case "fontweight":
                case "font-weight":
                    value = ReadFontWeight(raw);
                    break;

                case "gridlength":
                case "grid-length":
                    value = ReadGridLength(raw);
                    break;

                case "bool":
                case "boolean":
                    value = ReadBoolean(raw);
                    break;

                case "string":
                    value = ReadString(raw);
                    break;

                case "enum":
                    if (current is null || !current.GetType().IsEnum)
                        throw new FormatException("Enum overrides require an existing enum resource so its enum type can be inferred.");
                    value = Enum.Parse(current.GetType(), ReadString(raw), ignoreCase: true);
                    break;

                case "resource":
                    var referencedKey = ReadString(raw);
                    value = Application.Current.TryFindResource(referencedKey)
                            ?? throw new KeyNotFoundException($"Resource '{referencedKey}' was not found.");
                    break;

                default:
                    throw new FormatException($"Unsupported resource type '{explicitType ?? type}'.");
            }

            error = "";
            return true;
        }
        catch (Exception ex)
        {
            value = null;
            error = ex.Message;
            return false;
        }
    }

    private static string? InferType(object? value) => value switch
    {
        SolidColorBrush => "brush",
        Color => "color",
        double or float or decimal => "double",
        int or long or short => "integer",
        Thickness => "thickness",
        CornerRadius => "cornerradius",
        FontFamily => "fontfamily",
        FontWeight => "fontweight",
        GridLength => "gridlength",
        bool => "boolean",
        string => "string",
        Enum => "enum",
        _ => null,
    };

    private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }
        value = default;
        return false;
    }

    private static bool TryString(JsonElement element, out string value)
    {
        if (element.ValueKind == JsonValueKind.String)
        {
            value = element.GetString() ?? "";
            return true;
        }
        value = "";
        return false;
    }

    private static string ReadString(JsonElement element) =>
        TryString(element, out var value) ? value : throw new FormatException("Expected a string value.");

    private static double ReadDouble(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Number && element.TryGetDouble(out var number)) return number;
        if (TryString(element, out var text) && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out number)) return number;
        throw new FormatException("Expected a number.");
    }

    private static bool ReadBoolean(JsonElement element)
    {
        if (element.ValueKind is JsonValueKind.True or JsonValueKind.False) return element.GetBoolean();
        if (TryString(element, out var text) && bool.TryParse(text, out var value)) return value;
        throw new FormatException("Expected true or false.");
    }

    private static double[] ReadNumberList(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Number) return new[] { ReadDouble(element) };
        var text = ReadString(element);
        var values = text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(part => double.Parse(part, NumberStyles.Float, CultureInfo.InvariantCulture))
            .ToArray();
        return values.Length > 0 ? values : throw new FormatException("Expected one or more comma-separated numbers.");
    }

    private static Thickness ReadThickness(JsonElement element)
    {
        var values = ReadNumberList(element);
        return values.Length switch
        {
            1 => new Thickness(values[0]),
            2 => new Thickness(values[0], values[1], values[0], values[1]),
            4 => new Thickness(values[0], values[1], values[2], values[3]),
            _ => throw new FormatException("Thickness expects 1, 2, or 4 values."),
        };
    }

    private static CornerRadius ReadCornerRadius(JsonElement element)
    {
        var values = ReadNumberList(element);
        return values.Length switch
        {
            1 => new CornerRadius(values[0]),
            4 => new CornerRadius(values[0], values[1], values[2], values[3]),
            _ => throw new FormatException("CornerRadius expects 1 or 4 values."),
        };
    }

    private static FontWeight ReadFontWeight(JsonElement element) => ReadString(element).Trim().ToLowerInvariant() switch
    {
        "thin" => FontWeights.Thin,
        "extralight" or "extra-light" => FontWeights.ExtraLight,
        "light" => FontWeights.Light,
        "normal" or "regular" => FontWeights.Normal,
        "medium" => FontWeights.Medium,
        "semibold" or "semi-bold" => FontWeights.SemiBold,
        "bold" => FontWeights.Bold,
        "extrabold" or "extra-bold" => FontWeights.ExtraBold,
        "black" or "heavy" => FontWeights.Black,
        _ => throw new FormatException("Unknown font weight."),
    };

    private static GridLength ReadGridLength(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Number) return new GridLength(ReadDouble(element));
        var text = ReadString(element).Trim();
        if (text.Equals("Auto", StringComparison.OrdinalIgnoreCase)) return GridLength.Auto;
        if (text.EndsWith('*'))
        {
            var weightText = text[..^1];
            var weight = string.IsNullOrWhiteSpace(weightText)
                ? 1
                : double.Parse(weightText, NumberStyles.Float, CultureInfo.InvariantCulture);
            return new GridLength(weight, GridUnitType.Star);
        }
        return new GridLength(double.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture));
    }

    private static SolidColorBrush Brush(string value, string fallback)
    {
        var brush = new SolidColorBrush(ParseColor(value, ParseColor(fallback, Colors.Transparent)));
        brush.Freeze();
        return brush;
    }

    private static Color ParseColor(string value, Color fallback) =>
        ThemeCatalog.TryColor(value, out var color) ? color : fallback;
}
