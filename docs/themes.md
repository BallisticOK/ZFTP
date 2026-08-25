# ZFTP themes

ZFTP themes are live, local JSON files. Open **Settings → Appearance → Community themes** and either choose **Create from selected** to clone the current theme, or **Open folder** to work directly in:

`%AppData%\ZFTP\Themes`

ZFTP watches that directory. Saving a `.json` file normally reloads it automatically, and **Reload themes** is available if an editor saves files in an unusual way.

## Schema version 2

Schema v2 keeps the simple first-class theme properties and adds `Resources`: a safe typed override layer over ZFTP/WPF resources. That means a theme can change far more than a color palette: typography, spacing, window size, density, card geometry, status-bar sizing, dialog layout, and compatible WPF-UI resources can all be overridden.

```json
{
  "SchemaVersion": 2,
  "Id": "my-theme",
  "Name": "My Theme",
  "Author": "Me",
  "Description": "A complete visual redesign.",
  "Base": "Dark",
  "Backdrop": "None",
  "Accent": "#FF4FD1C5",
  "WindowBackground": "#FF090B10",
  "Surface": "#FF10151D",
  "SurfaceAlt": "#FF17202B",
  "Border": "#FF2E4053",
  "Text": "#FFF4F7FB",
  "TextMuted": "#FF91A3B5",
  "Selection": "#554FD1C5",
  "Hover": "#224FD1C5",
  "Badge": "#334FD1C5",
  "StatusBar": "#FF07090D",
  "Success": "#FF51CF66",
  "Warning": "#FFFFC857",
  "Error": "#FFFF6B6B",
  "FontFamily": "Cascadia Mono",
  "CornerRadius": 4,
  "RowHeight": 46,
  "Resources": {
    "ZftpBaseFontSize": 13,
    "ZftpSectionFontSize": 20,
    "ZftpPageMargin": { "Type": "Thickness", "Value": "28,24,28,20" },
    "ZftpCardPadding": { "Type": "Thickness", "Value": "20,16" },
    "ZftpWindowWidth": 1240,
    "ZftpSmallCornerRadius": { "Type": "CornerRadius", "Value": 2 }
  }
}
```

Colors accept `#RRGGBB` or `#AARRGGBB`. `Base` can be `Dark` or `Light`. `Backdrop` can be `None`, `Mica`, or another `WindowBackdropType` supported by the bundled WPF-UI version. The simple `CornerRadius` and `RowHeight` properties remain intentionally clamped so basic themes cannot make the app unusable; advanced authors can override the corresponding resource tokens directly.

## Resource overrides

For an existing resource, ZFTP infers its type, so numbers and strings can usually be written directly. For a new key or when you want to be explicit, use `{ "Type": "...", "Value": ... }`.

Supported types are `Brush`, `Color`, `Double`, `Integer`, `Thickness`, `CornerRadius`, `FontFamily`, `FontWeight`, `GridLength`, `Boolean`, `String`, `Enum`, and `Resource`. `Resource` aliases another existing resource by key. Enum values can be overridden when the target resource already exists, allowing ZFTP to infer the enum type.

Examples:

```json
"Resources": {
  "ZftpWindowBackgroundBrush": { "Type": "Brush", "Value": "#FF000000" },
  "ZftpPageMargin": { "Type": "Thickness", "Value": "8,8,8,8" },
  "ZftpThemeFontFamily": { "Type": "FontFamily", "Value": "Consolas" },
  "ZftpWindowWidth": 1380,
  "ZftpListPadding": { "Type": "Thickness", "Value": "2" },
  "SomeOtherBrush": { "Type": "Resource", "Value": "ZftpAccentBrush" }
}
```

When a theme is switched, ZFTP restores every resource overridden by the old theme before applying the new one. This prevents custom WPF-UI overrides from leaking between themes. Invalid resource overrides are skipped and shown as theme warnings instead of crashing the app.

## ZFTP layout tokens

These tokens are stable theme-facing resources defined by ZFTP:

| Token | Type | Controls |
| --- | --- | --- |
| `ZftpAccentBrush` | Brush | Primary accent |
| `ZftpWindowBackgroundBrush` | Brush | Window background |
| `ZftpSurfaceBrush` / `ZftpSurfaceAltBrush` | Brush | Panels and alternate surfaces |
| `ZftpBorderBrush` | Brush | Borders and dividers |
| `ZftpTextBrush` / `ZftpMutedTextBrush` | Brush | Primary and secondary text |
| `ZftpSelectionBrush` / `ZftpHoverBrush` | Brush | List selection and hover |
| `ZftpBadgeBrush` | Brush | Protocol/type badges |
| `ZftpStatusBarBrush` | Brush | Bottom status bar |
| `ZftpSuccessBrush` / `ZftpWarningBrush` / `ZftpErrorBrush` | Brush | State colors |
| `ZftpStoppedBrush` | Brush | Stopped/idle state color |
| `ZftpDownloadBrush` / `ZftpUploadBrush` | Brush | Transfer indicators |
| `ZftpThemeFontFamily` | FontFamily | App-wide font |
| `ZftpBaseFontSize` | Double | App-wide base font size |
| `ZftpMicroFontSize` | Double | Dense labels and metadata |
| `ZftpCaptionFontSize` | Double | Caption/help text |
| `ZftpItemTitleFontSize` | Double | Drive names and status icons |
| `ZftpIconFontSize` | Double | Main list icons |
| `ZftpSectionFontSize` | Double | Settings section headings |
| `ZftpCornerRadius` / `ZftpSmallCornerRadius` | CornerRadius | Panels, rows and badges |
| `ZftpRowHeight` | Double | Server row density |
| `ZftpWindowWidth` / `ZftpWindowHeight` | Double | Main window starting size |
| `ZftpWindowMinWidth` / `ZftpWindowMinHeight` | Double | Main window minimum size |
| `ZftpDialogWidth` / `ZftpDialogHeight` | Double | Edit dialog starting size |
| `ZftpDialogMinWidth` / `ZftpDialogMinHeight` | Double | Edit dialog minimum size |
| `ZftpSettingsMaxWidth` | Double | Settings content width |
| `ZftpThemeSelectorMinWidth` | Double | Theme picker width |
| `ZftpActionPanelMinWidth` | Double | Drive action panel width |
| `ZftpTabMargin` | Thickness | Main tab frame spacing |
| `ZftpMainPageMargin` | Thickness | Drives-page spacing |
| `ZftpPageMargin` | Thickness | Settings-page spacing |
| `ZftpSectionMargin` | Thickness | Section heading spacing |
| `ZftpCardMargin` / `ZftpCardPadding` | Thickness | Settings card geometry |
| `ZftpFooterCardMargin` / `ZftpFooterCardPadding` | Thickness | Settings footer card geometry |
| `ZftpListHeaderMargin` / `ZftpListPadding` | Thickness | Server-list geometry |
| `ZftpActionPanelMargin` | Thickness | Drive action panel spacing |
| `ZftpStatusBarPadding` | Thickness | Status-bar density |
| `ZftpDialogContentPadding` | Thickness | Edit dialog content spacing |
| `ZftpDialogFooterMargin` | Thickness | Edit dialog footer spacing |
| `ZftpFormFieldMargin` | Thickness | Form-field rhythm |
| `ZftpCompactCardPadding` | Thickness | Compact dialog cards |

Advanced themes may also override compatible existing WPF and WPF-UI resource keys. Those keys belong to their respective libraries and may change when ZFTP upgrades dependencies, so the `Zftp*` tokens above are the stable surface to prefer.

## Backward compatibility

Schema version 1 theme files continue to load unchanged. They simply do not use `Resources`. New themes and clones are written as schema version 2.

Theme JSON remains data-only: it cannot execute arbitrary XAML or code. Duplicate IDs or invalid core theme files are skipped rather than taking down the application.

## Built-in creative themes

ZFTP still ships its built-in accent themes plus creative presets such as Hacker 2000, Terminal Amber, Sakura Night, Mecha Neon, Moonlit Shrine, and Vaporwave 2001. Any built-in can be selected and then cloned with **Create from selected** as a starting point for a fully custom theme.
