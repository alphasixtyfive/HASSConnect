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
- `HassConnect.Tests`: Windows tests for protocol, validation, reconciliation, recovery and sampling. The settings store is source-linked to exercise the same persistence code without loading WinUI.

The initial window is 800 × 840 DIPs, capped to the available desktop work area. The window keeps a minimum client area of 720 × 440 DIPs and caps the settings column at 960 DIPs. Shorter windows scroll vertically; maximizing retains the same title and control alignment.

The reporting session serializes connection and sensor changes. It reuses a stable device identity, reads Home Assistant's enabled states before publishing, backs off on temporary failures, and stops retrying a removed registration until Connect is explicitly pressed. Disabled sensors are not polled.

Local sensor changes are saved before network work, including while paused or offline.
Pending choices survive restarts and take precedence until registered successfully;
after reconciliation, later HA-side changes are respected again. Dashboard discovery
runs outside the reporting lock. Dashboard requests, subscription setup and streamed
entity responses have operation deadlines.

The notification receiver retries capability registration as well as subscription.
It confirms receipt before dispatching to one ordered worker with a 32-message pending
queue. On overload, additional messages are confirmed as received but rejected and
logged; they are not executed or replayed. Confirmation failure restarts the channel,
while rejected credentials and a removed registration wait for explicit reconnection.

Settings retain a validated last-good backup. Recovering that backup disables PC control
and clears command opt-ins so older permissions cannot silently return. Unreadable
credentials require sign-in without discarding preferences. Pairing uses an encrypted,
flushed journal to complete interrupted settings/credential writes on the next launch.
A missing valid backup cannot reconstruct lost settings; newer schemas are never overwritten.

## Isolated UI checks

After publishing, run the native Windows checks without touching the running app:

```powershell
powershell.exe -NoProfile -File tests/UiSmoke.ps1 -AppPath artifacts/app/HassConnect.App.exe
```

The script creates a fresh test profile under `artifacts/ui-smoke`, injects a denied
settings write, verifies retry and offline preferences, and captures the command editor
at normal and short window sizes. It never runs a PC command. Native captures are PNGs;
inspect them before using any as product screenshots.

`HASSCONNECT_TEST_PROFILE` selects a separate data directory and single-instance key.
In this mode native notification registration and automatic update checks are disabled.
Use only disposable profiles without credentials. The script restores its process
environment and stops only the test process that it started.

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
