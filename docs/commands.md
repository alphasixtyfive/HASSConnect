# PC commands

HASS Connect accepts a small, fixed set of Home Assistant notification messages as
PC commands. Open **Controls** in the app, enable PC control, and choose the commands
this PC may run. PC control and every individual command are opt-in.

Replace `notify.mobile_app_your_pc` in the examples with the notify action created
for your HASS Connect device.

## Commands

| Command | Message | Additional data |
| --- | --- | --- |
| Lock PC | `command_lock` | None |
| Turn off displays | `command_monitor_sleep` | None |
| Sleep PC | `command_sleep` | None |
| Media playback | `command_media` | `media_command`: `play_pause`, `next`, `previous`, or `stop` |
| Mute or unmute | `command_volume_mute` | None |
| Set volume | `command_volume_level` | `volume_level`: whole number from 0 through 100 |

Simple commands use the notify action directly:

```yaml
action: notify.mobile_app_your_pc
data:
  message: command_lock
```

Commands with arguments put them in the notification's nested `data` object:

```yaml
action: notify.mobile_app_your_pc
data:
  message: command_media
  data:
    media_command: play_pause
```

```yaml
action: notify.mobile_app_your_pc
data:
  message: command_volume_level
  data:
    volume_level: 35
```

The [example PC Controls dashboard](pc-controls-dashboard.md) provides phone-friendly
buttons for every command and uses confirmation prompts for disruptive actions.

## Custom commands

Custom commands cover device-specific actions without turning incoming Home Assistant
messages into a remote shell. In **Controls > Custom commands**, select **Add**
and configure:

- A display name used only in HASS Connect.
- A stable command name beginning with `command_custom_`, followed by lowercase
  letters, numbers or underscores. Command names cannot be changed after creation.
- An absolute path to a local `.exe` file.
- Up to 16 fixed arguments, one per line. A line containing spaces remains one argument.

For example, a command named `command_custom_open_music` is triggered with:

```yaml
action: notify.mobile_app_your_pc
data:
  message: command_custom_open_music
```

The executable and arguments are stored locally. Data attached to the Home Assistant
message cannot replace or add arguments. To run a script, explicitly select its trusted
interpreter as the executable and put the script path and other fixed values in the
argument list. Avoid interpreters or scripts that evaluate untrusted files or network
content.

See the [custom-command guide](custom-commands.md) for setup examples, validation rules,
testing, editing, deletion and troubleshooting.

## Safety and failure handling

- Unknown messages remain ordinary notifications; they are never executed as commands.
- Custom commands must be explicitly configured on the PC and individually enabled.
- Custom command names, paths and arguments are validated before saving and again before execution.
- Custom commands start the selected `.exe` directly, without a shell, elevation or remote arguments.
- Only local absolute executable paths are accepted; UNC paths, relative paths and non-`.exe` targets are rejected.
- A custom command can start at most once every five seconds, with a global limit of ten starts per minute.
- Disabled commands are ignored and recorded in the local diagnostic log.
- Sleep is delayed briefly so Home Assistant can receive its delivery confirmation
  before the network connection is suspended.
- Every received command is acknowledged even if its payload is invalid or Windows
  rejects the action. The failure is logged, the receiver remains connected, and the
  next command is processed normally.
- A transport or confirmation failure reconnects the receiver with bounded backoff.

## Windows implementation

HASS Connect targets Windows 11 x64 and uses documented Windows APIs:

- Lock calls `LockWorkStation` on the interactive desktop.
- Display-off posts `WM_SYSCOMMAND` with `SC_MONITORPOWER` to avoid blocking on a
  non-responsive window.
- Sleep enables `SeShutdownPrivilege` only around `SetSuspendState`, then restores
  the process token's previous privilege state.
- Media and mute use correctly sized native `INPUT` structures with `SendInput`.
- Volume uses Core Audio and updates the default Console and Multimedia render roles.

Display power behavior ultimately depends on the PC firmware and Windows power model.
The standard display-off command is appropriate for conventional S3 systems. Some
Modern Standby devices couple display power with system sleep; test the command on
the target hardware before using it in an unattended automation.
