# LoupixDeck.Plugin.HomeAssistant

Direct Home Assistant integration for [LoupixDeck](https://github.com/RadiatorTwo/LoupixDeck).

![.NET 10](https://img.shields.io/badge/.NET-10-512BD4)
![MIT License](https://img.shields.io/badge/license-MIT-green)
![Status: early development](https://img.shields.io/badge/status-early%20development-orange)
![AI-assisted development](https://img.shields.io/badge/development-AI--assisted-purple)

## Status

This plugin is under active development. The current repository contains a buildable plugin that can be configured with a Home Assistant URL and access token, keeps a background connection to Home Assistant, and synchronizes entity state locally. Controllable commands and button feedback are not available yet.

## Configuration

Open the plugin's settings in LoupixDeck and fill in:

- **Home Assistant URL** — the base URL of your instance, for example `http://homeassistant.local:8123` or `https://ha.example.com`.
- **Long-Lived Access Token** — a token created in Home Assistant under your user profile (Security → Long-lived access tokens).

Save the settings; the plugin then connects and keeps the connection alive in the background. The optional **Test Connection** button verifies the saved values without disturbing the running connection.

The token field is masked in the settings UI, and both values are stored by LoupixDeck's standard per-plugin settings store. The token is never logged.

## Planned features

The planned first usable version aims to provide:

- Live entity state and touch-button feedback
- Toggle, turn-on, and turn-off actions
- Actions for scripts, scenes, and Home Assistant buttons
- Generic service calls and dynamic LoupixDeck menus

These features are planned, not currently available.

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

This builds the plugin class library. It contains the connection and entity-state synchronization described above; commands are not implemented yet.

## Home Assistant authentication

The connection uses a Home Assistant long-lived access token. Tokens must never be committed to source control.

## AI-assisted development

This project is developed with substantial AI assistance ("vibe coding"), primarily using coding agents such as Codex. Architecture, upstream compatibility, builds, and behavior are reviewed and validated during development. AI-generated changes are treated as code contributions, not as automatically trusted output.

## Development

The project targets .NET 10 and uses the LoupixDeck Plugin SDK as a NuGet dependency. It builds as a normal plugin class library. GitHub Actions validates builds; official GitHub Releases are packaged through LoupixDeck's reusable release workflow.

## License

Licensed under the [MIT License](LICENSE).
