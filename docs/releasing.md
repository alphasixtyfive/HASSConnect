# Releasing

The first release is `v0.1.0`. The version lives in `Directory.Build.props`.

## Before publishing

- Run `build.ps1` and require the Windows CI build to pass.
- Build an installer with a stable per-user installation path, a Start-menu shortcut and upgrade support.
- Include or install the Microsoft runtime dependencies described in [packaging](../packaging/README.md).
- On a clean Windows 11 account, check first launch, connection errors, sensors, startup and tray exit.
- Verify a notification with a snapshot and two buttons. Check the returned HA event, replacement by tag and clearing.
- Test an upgrade and uninstall. Preserve settings and encrypted credentials; remove shortcuts, startup entries and notification registration owned by the app.
- Check the release archive for credentials, signing keys, test photos, diagnostic logs and development output.

## Publish

Commit the release notes to `docs/releases/vX.Y.Z.md`, then push the matching
`vX.Y.Z` tag. The workflow checks the tag against `Directory.Build.props`, builds
and tests the installer, and publishes it with its SHA-256 checksum only after
the installation checks pass. Keep installation instructions clear about signing.
No certificate import is required for HASS Connect notifications.

Before announcing the release, make the repository public, verify the download
without signing in, and check **Settings > Updates** against the published release.
For later versions, also check that the preceding version offers the new release.

The app opens the release page. It does not download or run an installer itself.
