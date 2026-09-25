# LoupixDeck.Plugin.HomeAssistant

Direct Home Assistant integration for [LoupixDeck](https://github.com/RadiatorTwo/LoupixDeck).

The plugin is intended to connect directly to Home Assistant over its WebSocket API and expose Home Assistant entities as LoupixDeck controls with live feedback.

## Planned v1

- Home Assistant URL + long-lived access token
- WebSocket authentication and reconnect
- entity discovery and local state cache
- dynamic LoupixDeck menus
- toggle / turn on / turn off
- scripts, scenes and Home Assistant buttons
- generic service calls
- live touch-button rendering from cached entity state

## Development

Primary development environment:

- WSL on Windows
- .NET 10
- LoupixDeck running on Windows

Build:

```bash
dotnet build
```

Local reference repositories are downloaded into `_references/`.

Local AI/research notes are stored in `docs/`.

Both directories are intentionally ignored by Git.

To download reference repositories:

```bash
./scripts/bootstrap-references.sh
```

## Architecture

- `HomeAssistantPlugin.cs` — plugin lifecycle, metadata, registration and settings
- `HomeAssistant/` — WebSocket protocol, models and entity cache
- `Commands/` — thin LoupixDeck command adapters
- `Rendering/` — dynamic touch-button rendering
- `scripts/` — local development helpers

Rendering must use cached state only. It must not perform network I/O.

## Status

Initial scaffold. Home Assistant communication is not implemented yet.
