# Packaging

The WiX installer installs HASS Connect for the current Windows user under
`%LOCALAPPDATA%/Programs/HASSConnect` and adds a Start-menu shortcut.

## Build

Run in PowerShell 7 on Windows with the .NET SDK from `global.json`:

```powershell
./build.ps1
./packaging/Build-Installer.ps1
```

Both scripts accept `-DotNetPath`. Output is in `artifacts/release`.
Publish only `HASSConnect-<version>-Setup-x64.exe` and `SHA256SUMS.txt`.
The MSI, WiX symbols and downloaded prerequisite are intermediate build files.

The EXE contains the app and downloads the official Microsoft runtime installers.
.NET 10 and Visual C++ are installed only when a sufficient x64 runtime is missing. The Windows
App Runtime installer runs on each installation to check and register its shared
packages for the current user. Downloads are verified against pinned SHA-512
hashes and sizes in `RuntimeDependencies.json`. Internet access is required;
installing .NET or Visual C++ may request administrator permission.

No separate HASS Connect identity package or certificate import is required for
notifications.

The first release is unsigned. Code signing can be added when a trusted signing
certificate is available; it is separate from notification registration.

## Installation and removal

Setup supports `/quiet /norestart /log <path>` and `/uninstall`. The installer asks
the running app to exit before replacing or removing it. Uninstall removes its
shortcut, startup entry and native notification registration. Settings, encrypted
credentials and logs in `%LOCALAPPDATA%/HassConnect` are retained. The shared
Microsoft runtime is retained for other apps.

Keep both UpgradeCode values stable between releases. Increase the three-part
version in `Directory.Build.props` for each release. Never replace a published
installer with different contents under the same version.

## References

- [Native notifications in .NET](https://learn.microsoft.com/en-us/windows/apps/develop/notifications/app-notifications/app-notifications-dotnet)
- [Windows App Runtime deployment](https://learn.microsoft.com/en-us/windows/apps/windows-app-sdk/deploy-unpackaged-apps)
