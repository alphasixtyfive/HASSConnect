# Product screenshots

Published screenshots must come from a native, lossless capture of the HASS Connect
window. A screenshot that looks acceptable in an automation preview can still become
unreadable when GitHub scales it.

## Never publish preview captures

Do not use any of these as repository or release assets:

- a desktop-control or chat preview image;
- a `get_window_state` screenshot data URL;
- a JPEG transport frame saved with a `.png` extension;
- a JPEG that has been cropped and encoded again;
- an image upscaled from logical Windows coordinates.

The desktop-control preview is intended for locating controls. It may be downscaled to
logical pixels and encoded as JPEG. On a scaled Windows display, this discards the native
text and icon pixels. Re-encoding it damages them again, and GitHub thumbnail scaling makes
the entire interface look garbled.

If a native capture is not available, do not publish a replacement screenshot. Leave the
existing verified image in place or omit the image until a proper capture can be made.

## Capture requirements

1. Run the release build at the normal Windows display scale.
2. Put the app on the required page using UI automation only for navigation.
3. Move the pointer outside the app and wait for click highlights to disappear.
4. Capture the window directly at its physical backing-pixel dimensions.
5. Save straight to PNG. Do not pass the image through JPEG at any stage.
6. Crop only the title bar or empty border when needed, using a lossless operation.
7. Keep every screenshot in the set at the same window size, theme, and scale.

Use `tools/Capture-HassConnectWindow.ps1` for the lossless capture after arranging the
required page. The script makes its capture thread DPI-aware and writes PNG directly.

An 800 × 840 DIP window on a display scaled above 100% must produce an image larger than
800 × 840 pixels. A capture near 788 × 803 pixels is a logical-pixel preview and must be
rejected.

## Content requirements

- Use real HASS Connect UI, not a generated mock-up.
- Never expose an access token, Home Assistant address, private IP address, personal path,
  diagnostic log, or unrelated desktop content.
- Use generic device and command names where practical.
- Show valid examples. Do not publish placeholder launch options as if they were real
  arguments.
- Make sure switches, labels, dialogs, and examples match the released version.
- Keep the pointer, click ripples, other windows, and taskbar out of the image.

## Quality gate

Check every image before committing it:

- Confirm the file is actually PNG by its format, not only its extension.
- Record its pixel dimensions and verify they match the native window capture.
- Inspect the original at 100% zoom. Text strokes, icons, borders, and switch edges must be
  crisp, with no JPEG blocks, colour halos, doubled edges, or resampling blur.
- Inspect the image at the width used by GitHub, as well as at full size.
- Render the README and release page after publishing. Confirm every image loads at its
  natural dimensions and remains readable in the gallery.
- Have a second visual pass compare all screenshots for consistent scale, cropping, theme,
  spacing, and data privacy.

Failure of any check blocks publication. Do not try to repair damaged UI text by sharpening,
upscaling, automated editing, or another lossy export; recapture it from the application.

## Repository layout

Store approved screenshots in `docs/images/` with descriptive lowercase names. Reference
the same files from the README, feature guides, and release notes so there is only one
source image to replace and verify.
