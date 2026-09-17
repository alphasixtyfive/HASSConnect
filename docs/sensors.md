# Sensors

Sensors are individually opt-in. Enable sensors pauses collection while preserving
individual choices. Reports normally run every 15 seconds while awake and connected.

Display state reports `on`, `dimmed` or `off` for the current Windows session using
GUID_SESSION_DISPLAY_STATUS. It stays unknown until Windows supplies a value. This
does not detect individual monitor hardware switches.

Last seen reports the timestamp sampled for the latest report. It stops advancing
when sensors are paused, the PC sleeps, the connection fails or the app closes.
It does not automatically become unavailable. An HA template helper can detect
stale reports (replace the entity ID):

```jinja2
{{ 0 <= as_timestamp(now()) - as_timestamp(states('sensor.desktop_last_seen'), 0) < 90 }}
```

Templates using `now()` refresh at least once per minute, so this threshold is
approximate. It indicates recent reporting, not network reachability.
