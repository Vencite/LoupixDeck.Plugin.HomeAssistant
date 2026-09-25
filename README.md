# LoupixDeck.Plugin.HomeAssistant

Direct Home Assistant integration for [LoupixDeck](https://github.com/RadiatorTwo/LoupixDeck).

![.NET 10](https://img.shields.io/badge/.NET-10-512BD4)
![MIT License](https://img.shields.io/badge/license-MIT-green)
![Status: early development](https://img.shields.io/badge/status-early%20development-orange)
![AI-assisted development](https://img.shields.io/badge/development-AI--assisted-purple)

## Status

This plugin is under active development. The current repository contains a buildable plugin scaffold and a smoke-test command. It does not connect to Home Assistant or control entities yet.

## Planned features

The planned first usable version aims to provide:

- A direct Home Assistant WebSocket connection and entity discovery
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

This builds the plugin class library. It does not provide Home Assistant functionality yet.

## Home Assistant authentication

The planned connection will use a Home Assistant long-lived access token. Tokens must never be committed to source control.

## AI-assisted development

This project is developed with substantial AI assistance ("vibe coding"), primarily using coding agents such as Codex. Architecture, upstream compatibility, builds, and behavior are reviewed and validated during development. AI-generated changes are treated as code contributions, not as automatically trusted output.

## Development

The project targets .NET 10 and uses the LoupixDeck Plugin SDK as a NuGet dependency. It builds as a normal plugin class library. GitHub Actions validates builds; official GitHub Releases are packaged through LoupixDeck's reusable release workflow.

## License

Licensed under the [MIT License](LICENSE).
