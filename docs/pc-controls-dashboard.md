# PC controls dashboard

The example dashboard view provides phone-friendly buttons for every PC command
supported by HASS Connect. It uses only built-in Home Assistant cards and does not
require HACS, custom cards or helpers.

## Add the view

1. In Home Assistant, open **Developer Tools > Actions** and find the notify action
   for the PC, such as `notify.mobile_app_desktop`.
2. Copy [`home-assistant/pc-controls-view.yaml`](home-assistant/pc-controls-view.yaml)
   and replace every `notify.mobile_app_your_pc` with that action.
3. Open the target dashboard, select **Edit dashboard > Raw configuration editor**,
   and append the copied list item under `views:`.
4. Save the dashboard, then open the new **PC Controls** view on a phone.

If the dashboard does not yet contain a `views:` key, use this structure:

```yaml
views:
  # Paste the example here, retaining its leading `- title:` line.
```

Before testing, keep HASS Connect running and connected. In its **Controls** page,
turn on **Enable PC control** and enable the individual commands you intend to use.
Lock, display-off and sleep buttons ask for confirmation because they interrupt the
current Windows session. Volume uses fixed test levels so the view remains portable
and does not require an `input_number` helper or templated script.

The placeholder notify action is intentionally repeated in the view: this makes each
button self-contained and easy to copy into another dashboard later.
