# ZFTP themes

ZFTP includes 16 built-in themes and can load community themes from your local theme folder:

`%AppData%\ZFTP\Themes`

Open **Settings → Appearance → Community themes → Open folder** to jump there. ZFTP watches the folder, so saving a `.json` theme normally reloads it automatically. There is also a **Reload themes** button.

On first run ZFTP creates `theme-template.json.example` and `README.txt` in the folder. Copy the example to a new filename ending in `.json`, give it a unique `Id`, and edit it.

## Schema version 1

```json
{
  "SchemaVersion": 1,
  "Id": "my-theme",
  "Name": "My Theme",
  "Author": "Your name",
  "Description": "What this theme is going for.",
  "Base": "Dark",
  "Backdrop": "None",
  "Accent": "#4FD1C5",
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
  "FontFamily": "Consolas",
  "CornerRadius": 4,
  "RowHeight": 46
}
```

Colors accept `#RRGGBB` or `#AARRGGBB`. `Base` can be `Dark` or `Light`. `Backdrop` can be `None`, `Mica`, or another `WindowBackdropType` supported by the WPF-UI version bundled with ZFTP. `CornerRadius` is clamped to 0–32 and `RowHeight` to 36–80 so a broken theme cannot make the interface unusable.

Theme JSON is intentionally data-only: it can change the visual tokens, typography, density and backdrop, but it cannot execute code. Invalid or duplicate theme files are skipped instead of crashing ZFTP.

## Built-in creative themes

- **Hacker 2000** — black panels, phosphor green terminal text, square edges, no glass.
- **Terminal Amber** — early-workstation amber/black terminal look, no glass and no green.
- **Sakura Night · Anime** — midnight violet, sakura pink and lavender.
- **Mecha Neon · Anime** — deep navy, electric cyan and magenta warning lights.
- **Moonlit Shrine · Anime** — ink blue, shrine red and warm moon-paper text.
- **Vaporwave 2001** — ultraviolet, cyan and hot pink Y2K desktop energy.

The older ZFTP dark/light accent themes remain available too, so existing settings continue to resolve by name while newer settings store the stable theme `Id`.
