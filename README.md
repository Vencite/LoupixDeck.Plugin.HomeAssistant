# Home Assistant for LoupixDeck

Control Home Assistant from [LoupixDeck](https://github.com/RadiatorTwo/LoupixDeck), with live button states and a brightness dial.

**Early development:** no published release yet. Requires .NET 10 and a host compatible with Plugin SDK 1.26.0. Hardware behavior still needs validation in a running host.

## Build and install

```bash
dotnet build -c Release
```

Copy the plugin output from `bin/Release/` and `plugin.json` into LoupixDeck's user plugin directory (`%USERPROFILE%\.config\LoupixDeck\plugins\homeassistant` on Windows), then restart LoupixDeck. Do not copy `LoupixDeck.PluginSdk.dll`; the host supplies it.

## Connect

1. In Home Assistant, open your profile → **Security** → **Long-lived access tokens** and create a token.
2. In LoupixDeck, open the Home Assistant plugin settings. Enter your instance URL and token, then save.
3. Use **Test Connection** to check the saved settings or **Connection Status** to read the current connection state. The plugin reconnects automatically after temporary connection loss.

The token is masked in the editor and saved by LoupixDeck. Never put it in command parameters.

## Assign buttons and dials

- **Buttons:** open the action picker → **Home Assistant** → area → entity type → entity → action. Multi-action entities offer **Controls folder**: assign it to a button, then tap it on the deck to open live controls. Hidden, disabled and auxiliary entities are omitted. Available actions follow the entity's capabilities.
- **Status:** assign **Connection status** from the Home Assistant menu to show whether HA is connected and when entity data last updated.
- **Sensors:** choose **Show state** for a read-only button; sensor values include units.
- **Brightness dial:** open rotary presets and choose **entity · Brightness**. Each tick changes brightness by 5%; pressing toggles the light. Only dimmable lights get presets; the indicator shows the cached brightness.
- **Other controls:** climate folders show temperature up/down buttons and a live HVAC mode subfolder. Covers, fans, media players, locks, input numbers and switches also have controls folders; each action remains assignable to its own button.

Value actions start with a preset (for example 50% volume). Expand the assigned command to edit its service data as described below.

## Customize button labels

Expand an entity command in the button editor. These parameters customize the plugin image; formatting a regular host text layer does not change it:

| Parameter | Effect |
| --- | --- |
| `ShowIcon` | Show or hide the icon |
| `Label` | Custom name; `auto` uses the entity name |
| `Icon` | Host symbol name, such as `mdi:lightbulb`; `auto` uses the entity icon |
| `StateSize`, `LabelSize` | Text size, 8–18 px |

Long labels wrap to two lines and then shorten with an ellipsis. If the editor adds regular **Text** or **Symbol** layers above the plugin image, hide/remove them to reveal its state display.

Buttons distinguish **off**, **unknown**, **unavailable**, **No state**, **Connecting**, **Reconnecting** and **Offline**. State comes from Home Assistant updates, with a five-second refresh fallback.

## Custom service calls

Choose **Call Service** for advanced actions. Parameters are positional:

`HomeAssistant.CallService(Domain,Service,EntityId,ServiceData,Target)`

- `Domain` and `Service`: for example `light` and `turn_on`.
- `EntityId`: one entity, or `none` when using `Target`.
- `ServiceData`: a JSON object, URI-escaped; `none` omits it.
- `Target`: a URI-escaped JSON object supporting `entity_id`, `device_id`, `area_id`, `label_id` and `floor_id`, each as a string or list. Use `none` to omit it. Do not combine it with `EntityId`.

For example, 50% brightness for an illustrative light:

```text
HomeAssistant.CallService(light,turn_on,light.example,%7B%22brightness_pct%22%3A50%7D,none)
```

Use `[Uri]::EscapeDataString('{"brightness_pct":50}')` in PowerShell to encode a JSON object. Encoding is required because the host splits command parameters at commas. Existing calls with just domain, service and entity still work.

## Current limitations

- Rotary support currently covers light brightness.
- The generic **Call Service** button does not render live entity state or offer the entity display overrides. Actions selected from an entity menu do. Use a separate **Show state** button when needed.
- LoupixDeck currently adds its own Text/Symbol layers when an action is assigned. The SDK has no switch to suppress these layers; hide or remove them in the button editor to reveal the plugin's image. The plugin image is a single layer, so its text and icon cannot be styled as independent host layers. Only host-supported symbols render; unknown icons use a fallback.
- Avoid commas, parentheses and semicolons in custom labels: they are command syntax in the host. Use `auto` instead of an empty field to preserve parameter positions.
- Live updates request refreshes per command type, not per individual entity. Actual host redraw performance has not yet been measured.

## License

Plugin code: MIT; see [LICENSE](LICENSE). The Home Assistant logo has a separate [asset attribution and license](LICENSES/home-assistant-logo.md). Developed with AI assistance.
