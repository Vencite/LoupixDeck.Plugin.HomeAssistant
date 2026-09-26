# Development

This repository contains the Home Assistant plugin for LoupixDeck.

## Requirements

- .NET 10 SDK
- LoupixDeck.PluginSdk 1.26.0

The plugin targets `net10.0`.

## Build

```bash
dotnet restore
dotnet build -c Release --no-restore
```

LoupixDeck provides `LoupixDeck.PluginSdk.dll` at runtime. Do not redistribute a private copy of the SDK assembly with the plugin.

## Smoke tests

The repository includes a standalone smoke-test project that exercises the Home Assistant client, entity store, plugin lifecycle, command generation, rendering and command dispatch paths.

Run it with:

```bash
dotnet run --project tests/ClientSmoke/ClientSmoke.csproj -c Release
```

A release preparation change should not be considered complete unless both the Release build and the smoke test pass.

## Compatibility rules

Treat these values as public compatibility surface:

- `plugin.json` id
- plugin settings keys
- `HomeAssistant.*` command IDs
- existing command parameter ordering

Do not rename or reorder them without a migration plan. Existing profiles and saved button bindings must continue to work.

The manifest version and `PluginMetadata.Version` must match.

## Release workflow

The repository uses the reusable workflow from `RadiatorTwo/LoupixDeck.PluginSdk`.

A published GitHub Release must use the tag:

```text
v<plugin.json version>
```

For version `0.2.0`, the tag is:

```text
v0.2.0
```

A manual `workflow_dispatch` run can be used to validate packaging before publishing a GitHub Release.

The official workflow creates the plugin ZIP, standalone `plugin.json`, `SHA256SUMS` and a `store-entry.json` artifact for a later LoupixDeck Plugin Store pull request.

## Sources of truth

Before changing SDK usage or manifest fields, verify the current code and the current upstream repositories:

- `Vencite/LoupixDeck.Plugin.HomeAssistant`
- `RadiatorTwo/LoupixDeck`
- `RadiatorTwo/LoupixDeck.PluginSdk`

Do not invent SDK APIs, manifest fields or host behavior.
