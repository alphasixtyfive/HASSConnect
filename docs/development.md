# Development

## Build and check

Requires Windows 11 x64, the .NET 10 SDK and the Windows SDK. The .NET SDK version is pinned in `global.json`. From the project directory:

```powershell
./build.ps1
```

For an SDK installed outside PATH:

```powershell
./build.ps1 -DotNetPath 'C:/Tools/dotnet/dotnet.exe'
```

The script runs the tests and publishes the desktop app to `artifacts/app`. Quit the running app before rebuilding that folder.

The app uses shared .NET and Windows App SDK runtimes. Setup installs these dependencies. Runtime download versions and checksums are pinned in `packaging/RuntimeDependencies.json`.

## Structure

- `HassConnect.Core`: settings, sensor definitions, sampling calculations, reconciliation and address validation; no Windows or network dependencies.
- `HassConnect.HomeAssistant`: HTTP and WebSocket protocols, independent of the UI and sensor collection.
- `HassConnect.Updates`: stable-release discovery plus verified installer downloads from GitHub, independent of Windows and Home Assistant.
- `HassConnect.App`: native WinUI settings, tray lifecycle, Windows sensor readers, encrypted storage and the reporting session.
- `HassConnect.Tests`: protocol, validation, reconciliation and CPU calculation tests with an in-memory HTTP handler.

The initial window is 800 × 840 DIPs, capped to the available desktop work area. The window keeps a minimum client area of 720 × 440 DIPs and caps the settings column at 960 DIPs. Shorter windows scroll vertically; maximizing retains the same title and control alignment.

The reporting session serializes connection and sensor changes. It reuses a stable device identity, reads Home Assistant's enabled states before publishing, backs off on temporary failures, and stops retrying a removed registration until Connect is explicitly pressed. Disabled sensors are not polled.

Network sensors follow the IPv4 route to the configured Home Assistant server. Speeds measure all traffic on that adapter, averaged over the reporting interval, in Mbit/s. The first rate sample is unavailable; changing adapters or resuming reporting starts a new baseline. No public IP lookup is performed. IPv6-only Home Assistant addresses currently leave network readings unavailable.

To add a sensor, define its stable identity and metadata in Core, implement its Windows reader, and test its behaviour. Keep platform-specific APIs out of the HTTP client. Do not add a framework or abstraction until there is a second concrete need for it.

## Product configuration

`src/HassConnect.Core/ProductInfo.cs` defines the name, author and GitHub repository.
`Directory.Build.props` supplies the version for all application assemblies and HA
registration.

The Updates section checks the latest stable GitHub release automatically when the app starts. A newer release can be
downloaded and installed from the app after explicit confirmation. Downloads are accepted only from the configured
repository and only when `SHA256SUMS.txt` verifies the exact `HASSConnect-X.Y.Z-Setup-x64.exe` asset. If a release is
missing either asset, the app falls back to its release page. Publish a stable release tagged `vX.Y.Z` to make it available.

## Windows notifications

`WindowsNotifications` owns native registration, rendering and removal.
`DesktopActivation` routes launches and notification clicks to the existing window.
`NotificationService` receives HA messages, resolves images and sends action events.
The app owns the Windows registration lifetime; the reporting session owns its HA connection.

Unpackaged registration supplies the app name and icon through the Windows App SDK.
Subscribe to `NotificationInvoked` before `Register`, and register before reading
`AppInstance.GetActivatedEventArgs`. Keep the UI event loop running to receive COM
activation. Do not add a custom URI protocol, explicit AUMID or Start-menu property
store registration for notifications.

Notification images are cached under `%LOCALAPPDATA%/HassConnect/NotificationImages`
and passed to Windows as file URIs. No HASS Connect identity package or certificate
is required. Installer signing is a separate distribution concern.

See [packaging](../packaging/README.md) for installer builds and deployment requirements.

Use the [release checklist](releasing.md) before publishing a version.

Follow the [product screenshot standard](screenshots.md) for every README, guide, and
release image. Automation preview JPEGs are not acceptable source assets.
