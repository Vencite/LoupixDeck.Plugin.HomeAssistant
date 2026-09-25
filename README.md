# LoupixDeck.Plugin.HomeAssistant

Direct Home Assistant integration for [LoupixDeck](https://github.com/RadiatorTwo/LoupixDeck).

![.NET 10](https://img.shields.io/badge/.NET-10-512BD4)
![MIT License](https://img.shields.io/badge/license-MIT-green)
![Status: early development](https://img.shields.io/badge/status-early%20development-orange)
![AI-assisted development](https://img.shields.io/badge/development-AI--assisted-purple)

## Status

This plugin is under active development. The current repository contains a buildable plugin that can be configured with a Home Assistant URL and access token, keeps a background connection to Home Assistant, synchronizes entity state locally, and exposes control commands with dynamic entity menus and touch-button state feedback. It has not been released yet, and the commands and button rendering have not been validated in a running LoupixDeck host.

## Configuration

Open the plugin's settings in LoupixDeck and fill in:

- **Home Assistant URL** — the base URL of your instance, for example `http://homeassistant.local:8123` or `https://ha.example.com`.
- **Long-Lived Access Token** — a token created in Home Assistant under your user profile (Security → Long-lived access tokens).

Save the settings; the plugin then connects and keeps the connection alive in the background. The optional **Test Connection** button verifies the saved values without disturbing the running connection.

The token field is masked in the settings UI, and both values are stored by LoupixDeck's standard per-plugin settings store. The token is never logged.

## Features

The implementation currently provides:

- Live entity state synchronization and touch-button feedback
- Toggle, turn-on, and turn-off actions for a single entity
- Actions for scripts, scenes, and Home Assistant buttons
- A generic service-call action for anything the dedicated commands do not cover
- Dynamic LoupixDeck menus that group entities by Home Assistant area and domain, using cached registry metadata
- Read-only state buttons for sensors and binary sensors (including sensor units)

All entity commands take one shared `entity_id` parameter, so a single command serves every entity of its kind instead of one command per entity.

Each entity command also accepts optional per-button display overrides stored as trailing parameters: a `ShowIcon` flag, a custom `Label` and a custom `Icon` (a host symbol id, optionally with an `mdi:` prefix). Bindings that only contain `entity_id` keep working unchanged; leaving an override empty falls back to the automatic Home Assistant presentation (friendly name and an icon from entity metadata, device class or domain).

The renderer gives `off`, `unknown`, and `unavailable` distinct colors. Home Assistant icons that are absent from LoupixDeck's built-in symbol library fall back to a supported icon for the entity's device class or domain.

In the command editor, expand the command chip and change **Label** to shorten the text beneath the state (for example, `Lampka`) and **Icon** to a symbol id such as `mdi:lightbulb`. Use `auto` in either field to return to the Home Assistant default. **StateSize** and **LabelSize** set the two text sizes per button (8–18 px). LoupixDeck currently presents the icon parameter as a text field and renders only the symbol ids in its built-in library. Menu actions include the entity name, so searching for a friendly name finds its commands.

If assigning a command adds regular **Text** or **Symbol** layers, hide or remove those layers in the button editor to avoid covering the plugin-rendered state. The plugin's rendered layer is a single image and its text cannot be styled as an independent LoupixDeck layer.

## Installation

There is no usable Home Assistant plugin release yet.

### LoupixDeck Plugin Store

Plugin Store distribution is planned once the plugin reaches a usable release and is accepted into the LoupixDeck catalogue. The plugin is not currently available in the store.

### GitHub Releases

Official releases will include an installable archive produced by the upstream LoupixDeck Plugin SDK release workflow, named `homeassistant-<version>-any.zip`.

Compatible ZIP releases can be installed through LoupixDeck's plugin installation UI. If needed, the archive contents can also be placed manually in the user's LoupixDeck plugin directory.

### Build from source

Install the .NET 10 SDK, then run:

```bash
dotnet restore
dotnet build -c Release
```

This builds the plugin class library, containing the connection, entity-state synchronization and the command layer described above.

## Home Assistant authentication

The connection uses a Home Assistant long-lived access token. Tokens must never be committed to source control.

## AI-assisted development

This project is developed with substantial AI assistance ("vibe coding"), primarily using coding agents such as Codex. Architecture, upstream compatibility, builds, and behavior are reviewed and validated during development. AI-generated changes are treated as code contributions, not as automatically trusted output.

## Development

The project targets .NET 10 and uses the LoupixDeck Plugin SDK as a NuGet dependency. It builds as a normal plugin class library. GitHub Actions validates builds; official GitHub Releases are packaged through LoupixDeck's reusable release workflow.

## License

Licensed under the [MIT License](LICENSE).
