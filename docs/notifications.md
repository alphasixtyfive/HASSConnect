# Notifications

Enable notifications on the Notifications page. HASS Connect receives messages over Home Assistant's WebSocket connection and displays native Windows notifications. No MQTT broker, cloud relay or custom Home Assistant integration is required.

The app must remain running, including in the tray, with the PC awake and connected. Messages are not queued while offline. Windows notification settings and Do not disturb control whether a banner or sound appears. The **Send test** button checks local Windows delivery; sending from Home Assistant checks the full connection.

Windows registration supplies the **HASS Connect** sender name and small header
icon automatically. No app certificate or identity package is needed.

## Send from Home Assistant

Use the notify action created for this device. For a device named DESKTOP:

```yaml
action: notify.mobile_app_desktop
data:
  title: Home Assistant
  message: The laundry is finished.
```

The actual action name follows the registered device name. Look in the Actions tab of Developer Tools if it differs.

## Images and action buttons

Images and action buttons are supported automatically when included in a message:

```yaml
action: notify.mobile_app_desktop
data:
  title: Front door
  message: Someone is at the door.
  data:
    image: /local/front-door.jpg
    actions:
      - action: HASS_CONNECT_DOOR_ACKNOWLEDGE
        title: Acknowledge
      - action: HASS_CONNECT_DOOR_LATER
        title: Later
```

The example uses an existing `/config/www/front-door.jpg` file; it does not capture
a new camera snapshot. For an image in Home Assistant's Media browser, use its
authenticated path, for example `/media/local/camera/snapshots/front-door.jpg`.
Omit the temporary `?authSig=...` query: HASS Connect supplies the saved token when
fetching from the configured Home Assistant server.

Action buttons emit `mobile_app_notification_action` in Home Assistant with the selected identifier in `event.data.action`. Use that event to trigger an automation. Buttons work while HASS Connect is running. URI buttons open HTTP(S) links in the default browser; paths beginning with `/` resolve against the configured HA server. Inline replies and local PC commands are not supported.

Clicking the notification body opens HASS Connect. An action button runs once and expires after 24 hours or when the app restarts.

The app supports up to three action buttons. PNG, JPEG and GIF images are limited
to 5 MB and a five-second download timeout. Redirects are not followed. Image
failures fall back to a text notification and add a credential-free diagnostic log
entry. Home Assistant credentials are sent only to the configured Home Assistant
origin, never to an external image host.

Downloaded images are cached locally and passed to Windows as file URIs. The
snapshot occupies the large image area; the sender icon stays in the header.
Use an image URL in YAML, not a Windows file path.

Notification preferences are independent of Enable sensors. Turning notifications off stops the receiver and preserves the sound choice.

## PC control

Open **Controls** and turn on **Enable PC control** to accept selected commands.
See the [PC command reference](commands.md) for payloads, safety behavior, Windows
implementation details and the phone-friendly dashboard.

## Desktop activation

Windows delivers clicks through the native notification callback. No URI handler
or app chooser is involved. A second launch is redirected to the running app.
Buttons carry random, one-use tokens; Home Assistant action names and credentials
are never included in notification activation arguments. Unknown or expired tokens
are ignored.

## Replace, clear and identify alerts

Use the same `data.tag` to replace a previous alert. Tags are case-sensitive and
limited to 256 characters. Replacing or clearing an alert invalidates its old buttons.

```yaml
action: notify.mobile_app_desktop
data:
  message: Motion detected
  data:
    tag: kitchen-motion
    image: /media/local/camera/snapshots/latest-kitchen.jpg
    action_data:
      alert_id: kitchen-alert-001
      camera: camera.kitchen_side
    actions:
      - action: URI
        title: Open camera
        uri: /dashboard-cameras/kitchen
      - action: CONFIRM_KITCHEN
        title: Confirm
```

Use your actual image path and dashboard URL. URI buttons open a page and do not
fire an HA event. Other buttons return `action`, `device_id`, `tag`, a generated
`notification_id`, and the supplied object under `action_data` in the
`mobile_app_notification_action` event. The generated ID distinguishes deliveries;
use your own `alert_id` to correlate the click with a camera event.

Clear a tagged notification without another banner:

```yaml
action: notify.mobile_app_desktop
data:
  message: clear_notification
  data:
    tag: kitchen-motion
```

When an action fails, the app updates its status and attempts to show an **Action
not confirmed** notification. Windows DND can suppress its banner. Actions are not
automatically retried: a timed-out request might have reached HA already. Successful
delivery means HA accepted the event, not that its automation completed.

References: [Windows notification management](https://learn.microsoft.com/en-us/windows/apps/develop/notifications/app-notifications/manage-app-notifications),
[HA actionable notifications](https://companion.home-assistant.io/docs/notifications/actionable-notifications/).
