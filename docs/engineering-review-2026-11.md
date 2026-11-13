# Engineering and UX review — 13 November 2026

Reviewed commit: `ae48316` (0.7.0).

The highest-value next release is a reliability pass: make notification recovery dependable, bound network operations, and keep settings recoverable. The existing native settings layout and separation between Core, HomeAssistant, Updates and App are useful foundations. Keep that structure and make focused changes.

The findings below describe the reviewed commit. The approved reliability and UI work
is included in the 0.8.0 release candidate; see the completion notes below.

## Implementation

- Notification capability setup now participates in retry; credential rejection stops retries with an actionable status.
- Receipt confirmation is independent of ordered delivery, with a bounded queue and explicit overload rejection.
- Dashboard/subscription setup and entity body reads have deadlines. Dashboard loading no longer holds the reporting lock; obsolete discovery and popup requests are cancelled.
- Settings backups, credential recovery and encrypted pairing journals preserve recoverable state. Unsupported schemas are protected.
- Offline and paused sensor choices are persisted and reconciled; the Sensors page has one freshness line and omits repetitive Off labels.
- Settings has a connection reset action and a direct token-help link. First connection opens Sensors.
- The custom-command editor is a separate component with generated IDs, Copy, script-specific fields, retained input after save failures and Remove wording.
- The editor explicitly uses the native WinUI dialog style, preserving rounded corners. Its form scrolls in short windows while footer actions remain visible.
- Added safe diagnostic status codes, an isolated UI profile, native capture selection by process, regression tests and a repeatable UI smoke script.

Validation: 284 automated tests passed, including a real loopback WebSocket with a withheld
dashboard result. Release publish, installer packaging and the native UI checks passed. UI checks exercised a
denied save followed by retry, generated IDs, script field visibility, paused/offline
sensor choices and short-window scrolling. Captures were visually inspected at 150% Windows
scale. Real HA outages, physical suspend/resume and a new installer deployment remain
outside these local checks. Native QA captures are in `artifacts/v0.8.0-ui`; the README
gallery has been refreshed with native 1200 × 1260 PNGs. The release workflow additionally
checks fresh installation, upgrade from the preceding release, retained preferences and
encrypted credentials, and removal before publishing.

## Verification

- `./build.ps1 -DotNetPath C:/Users/alexey/.dotnet/dotnet.exe -OutputDirectory artifacts/review-app` passed release checks, all 264 tests and the Release desktop publish.
- Two temporary regression probes failed against the current implementation, confirming that notification acknowledgement waits for delivery and that an entity response body can outlive `HttpClient.Timeout`. These probes were removed from the test project; their source remains locally in ignored `artifacts/ReviewRegressionProbes.cs`.
- The original 264 tests passed again after removing the probes.
- Reviewed connection/session code, persistence, notifications, commands, quick access, sensor collection, updates, packaging, XAML and existing product screenshots. The screenshots are older than some current features; UI suggestions also use current source.
- No live Home Assistant outage, actual remote PC command, installer execution, or interactive DPI/accessibility test was performed. Findings below distinguish executable probes from source inspection.

## Reliability changes, in order

### 1. P1 — Restart notification setup after an offline launch

Evidence: `NotificationService.ConfigureAsync` stops the existing receiver, then awaits `SetRemoteCapabilityAsync` before creating `_receiver`. A network failure at that point leaves no receiver or retry loop. `SensorSession.StartMessageReceiverAsync` logs the exception once. The sensor loop can subsequently recover, but it does not configure the notification service again.

Effect: start Windows while Home Assistant or the network is unavailable; sensors can later reconnect while notifications and remote PC controls remain inactive until another explicit configuration action or restart. The notification status can remain `Off` despite enabled settings. This is established by the startup control flow, not a live-server reproduction.

Change: put capability registration and subscription establishment inside the receiver's supervised retry loop. Set a truthful connecting/reconnecting state before network work. Stop only on an explicit disable, shutdown, invalid credentials or removed registration.

Acceptance: launch offline, restore the server, and verify both notifications and controls recover without a toggle or restart; repeat when capability registration fails after replacing a running receiver.

### 2. P1 — Acknowledge notification receipt independently of rendering

Evidence: `NotificationChannel.ReceiveAsync` processes messages serially. `ProcessEventAsync` waits for `deliver` before confirming receipt. Delivery can download an image for up to five seconds, write the cache and invoke Windows notification APIs.

Home Assistant requires confirmation within ten seconds and closes the notification channel when it does not arrive. A burst of three slow image notifications can exhaust that deadline even though each individual download has a timeout. [Home Assistant notification protocol](https://contributors.home-assistant.io/companion/mobile-app-api/notifications/).

The temporary `ReceiptIsConfirmedBeforeSlowDeliveryCompletes` probe confirmed that acknowledgement waits while delivery is pending. It did not simulate a live HA channel closure.

Change: separate receipt/confirmation from delivery with a bounded queue and one delivery worker. Preserve message order, tag replacement and command ordering. Confirm accepted receipts promptly; define queue-full behavior explicitly. Monitor confirmation responses and subscription failures instead of ignoring every later `result` packet. Coordinate deferred sleep/shutdown commands with the confirmation send rather than relying solely on a one-second delay.

Acceptance: burst slow images followed by a control message; verify timely acknowledgements, ordered delivery, bounded memory and clear recovery on a lost subscription.

### 3. P1 — Bound dashboard requests and release the session lock

Evidence: `SensorSession.GetDashboardsAsync` holds `_gate` throughout dashboard discovery. `DashboardCatalog.GetAsync` passes the application lifetime token into both `ReadResultAsync` calls. `HaWebSocket` bounds the initial handshake but has no deadline for these result reads or notification subscription setup.

Effect: a server that answers WebSocket keepalives but never returns the requested dashboard result can hold the gate indefinitely. Sensor reporting and settings changes wait behind an optional dashboard picker. The Connect UI also awaits dashboard loading before restoring its controls.

Change: copy the connection information under the gate, then release it before discovery. Apply an overall operation deadline to dashboard discovery and a separate deadline to notification subscription setup. Retain the selected dashboard and let the user retry discovery. Do not impose a short request timeout on the normal idle notification receive loop.

Acceptance: a fake server authenticates and responds to pings but withholds the dashboard result; discovery times out while sensors and settings remain usable.

### 4. P2 — Apply deadlines to streamed entity bodies

Evidence: both quick-action entity methods in `HaClient` use `ResponseHeadersRead`, then deserialize the body using only the supplied cancellation token. The HTTP client's twenty-second timeout does not cover this phase. [Microsoft's timeout documentation](https://learn.microsoft.com/en-us/dotnet/api/system.net.http.httpcompletionoption?view=net-10.0).

The temporary `EntityBodyReadHonorsRequestTimeout` probe used an in-memory handler and stalled response stream. After reducing the private HTTP timeout to 100 ms, the operation still waited beyond 500 ms and required caller cancellation. This confirms the missing body deadline without contacting a server.

Change: use a linked operation deadline for request headers, body reads and JSON enumeration, as `NotificationImageClient` already does. Cancel obsolete popup requests when the popup closes rather than merely ignoring their results.

Acceptance: promptly received headers followed by a stalled body produce a bounded failure for both entity discovery and state refresh; cancellation closes the response stream.

### 5. P2 — Handle rejected credentials consistently

Evidence: `HaWebSocket.ConnectAsync` reports an invalid token as `HttpRequestException` with HTTP 401. `NotificationService.RunAsync` instead catches `UnauthorizedAccessException`. Invalid credentials therefore enter its generic reconnect loop.

Change: recognize HTTP 401/403, show a sign-in action and stop retrying until credentials change. Remove the unused `HaClient` constructed in this loop. Decode subscription failures sufficiently to distinguish a removed registration from a transient failure.

Acceptance: invalid credentials stop retries and show the remedy; saving a valid replacement token restarts the channel.

### 6. P2 — Preserve the command editor on storage failures

Evidence: `MainWindow.SaveCustomCommandAsync` catches validation exceptions but not `IOException` or `UnauthorizedAccessException` from saving settings. It is called by `async void` event handlers. The application's unhandled-exception handler logs the type without marking the exception handled.

Effect: a full disk or denied file write can escape the save operation and reach the unhandled UI exception path, potentially terminating the app instead of preserving the form. This is a source-level finding; no disk permissions were changed to reproduce it.

Change: handle expected storage errors locally with the existing `UserMessage` mapping, retain form input, restore buttons and allow retry. Keep validation focus handling separate from file errors. Audit the file picker and log-folder launch handlers for the same boundary issue.

Acceptance: inject a denied write and a full-disk error; the editor stays open, the command remains unsaved and retry succeeds after the failure clears.

### 7. P2 — Add recoverable persistence and pairing

Evidence: `SettingsStore` flushes a temporary file and replaces the primary file, but keeps no backup. A corrupt settings document or undecryptable credential file prevents session construction. The window preserves files and disables Connect, with no recovery action. Pairing saves settings and credentials as two separate replacements after the remote registration has already happened.

Change: retain a validated last-good settings backup and preserve unreadable files for diagnosis. Recover settings separately from credentials: a token problem should lead to sign-in while keeping sensor and command choices. Preserve device identity. Refuse to overwrite a newer schema. Treat pairing as a recoverable operation so interruption between writes does not leave an unexplained partial state. Avoid rewriting unchanged settings on every successful launch.

Acceptance: corrupt primary/valid backup, corrupt credentials/valid settings, unsupported schema, and interrupted pairing each have a defined outcome. Recovery must not silently re-enable previously disabled commands or replace a good backup with a corrupt primary.

## UX changes with the best return

1. **Provide a real connection-repair path.** The server becomes read-only after registration, and the error asks the user to reset the connection even though there is no reset control. Add an explicit Change server / Reset connection flow with clear consequences and retained local preferences. A renamed HA hostname should not require manually editing application files.
2. **Make offline sensor choices editable.** Individual sensor changes currently call the server before saving locally, so disabling one fails when HA is unavailable. Save the local intent immediately and reconcile it on reconnect, while preserving explicit HA-side choices. Let users configure selections while global sensor sharing is paused.
3. **Show freshness where readings appear.** Put one last-updated/offline message above the sensor list, rather than only showing last report time inside Settings. Retained readings should look stale after disconnect. Keep healthy operation quiet.
4. **Simplify custom commands.** Suggest a stable command ID from the name when creating a command, preserve existing IDs, add a copy action, hide executable-only arguments for scripts, and use Remove for removing a saved definition. A single failure/result line is enough; avoid a permanent activity dashboard.
5. **Make setup actionable.** Link token help directly to the configured HA profile, then offer a clear route to choose sensors after the first successful connection. Keep the manual token flow working; OAuth can be evaluated separately if onboarding remains a major obstacle.
6. **Show partial connection failures only when relevant.** Sensor HTTP health and notification WebSocket health are separate. If notifications or controls are enabled but unavailable, expose that fault without adding several permanent status badges.
7. **Refresh documentation images.** Current checked-in screenshots omit Quick access and show the older executable-only command editor. Capture the implemented UI using the existing screenshot standard after changes land.

## Refactoring and test coverage

The four-project split already separates platform-independent logic from Windows concerns. A framework migration or broad rewrite would add cost without addressing these failures.

- Move the custom-command editor out of `MainWindow`, which currently owns connection fields, navigation, command forms and window lifecycle. Keep validation in Core.
- Give notification connection/retry logic a small testable boundary, and keep it independent from native toast rendering. Use dependency seams for the concrete failure cases above rather than introducing a general service framework.
- Separate settings persistence/validation from WinUI so corruption and write-failure behavior can be exercised against temporary directories.
- Extend tests into the app's orchestration. The current test project references HomeAssistant and Updates (and Core transitively), not App; the passing tests therefore do not validate `SensorSession`, `NotificationService`, `SettingsStore` or WinUI event-handler behavior. CI does have an installed-app startup/removal smoke check.
- Add the fault cases described above, plus resume/network restoration and a small Windows UI check for keyboard navigation and short/high-DPI windows. Keep existing protocol and validation tests.
- Improve logs with safe status codes, operation phase and native error codes. Preserve the existing avoidance of tokens, webhook IDs and signed image URLs. Repeated exception type names alone are insufficient to diagnose recovery failures.

## Suggested delivery order

1. Notification startup recovery, credential classification, acknowledgement timing, network deadlines and command-save error handling, each with focused regression coverage.
2. Settings/credential recovery, interrupted pairing, connection repair and offline sensor preferences.
3. Command-editor extraction, the small UX changes above, refreshed screenshots and Windows UI verification.

Signed release packaging remains a separate distribution improvement already acknowledged in the packaging guide. No release, installation, signing enrollment or new helper is part of this review.
