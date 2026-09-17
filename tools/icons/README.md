# Icons

The source is `src/HassConnect.App/Assets/hass-connect-icon.svg`.
It uses cyan and white on a transparent background.

To regenerate the app and tray assets:

```powershell
cd tools/icons
npm ci --ignore-scripts
npm run build
```

The script renders the SVG directly at each ICO size: 16, 20, 24, 32, 40, 48,
64, 128 and 256 pixels. It also produces the 256-pixel PNG used by the app.
