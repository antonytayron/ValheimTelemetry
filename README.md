# Valheim Telemetry

A server-side BepInEx 5 plugin that exposes a REST API and Prometheus metrics
for a Valheim dedicated server.

## Features

- `GET /health` reports plugin status and version.
- `GET /api/v1/players` returns online player IDs, names, health, and deaths
  observed during the current plugin session.
- `GET /api/v1/players/{playerId}` returns one online player's data.
- `GET /metrics` exposes Prometheus metrics for the process, network, server,
  world, players, observed player deaths, and tracked portals.
- Optional API-key authentication and configurable listener address and port.

## Player data limitations

Health and maximum health are read from the player's server-side ZDO when that
ZDO is available. The server does not receive the player's stamina, eitr, or
adrenaline fields; these values are intentionally not included in the API.
Obtaining them would require a client-side mod to send the values to the server.

Death counts record dead-state transitions observed while the plugin is
running. They are session counters, are not loaded from the player's saved
profile, and reset when the plugin or server restarts. The portal metric counts
portal ZDOs currently tracked by the server.

## Installation

Build the project and copy `bin/Release/net48/ValheimTelemetry.dll` to:

```text
BepInEx/plugins/ValheimTelemetry/ValheimTelemetry.dll
```

Restart the server. The plugin does not require Jotunn.

## Configuration

After the first run, edit:

```text
BepInEx/config/br.com.midgard.valheimtelemetry.cfg
```

Example:

```ini
[HTTP]
BindAddress = 127.0.0.1
Port = 8765
ApiKey =
```

Use `0.0.0.0` as the bind address to listen on all network interfaces. Set a
non-empty API key before exposing the endpoint to an untrusted network. Clients
can send it using either `X-API-Key` or `Authorization: Bearer <key>`.

## API examples

```http
GET /health
GET /api/v1/players
GET /api/v1/players/{playerId}
GET /metrics
```

Example player response:

```json
{
  "count": 1,
  "players": [
    {
      "id": "76561198012345678",
      "name": "PlayerOne",
      "deaths": 1,
      "health": {
        "current": 125.4,
        "max": 150
      }
    }
  ]
}
```

When the server does not have the player's health ZDO, `current` and `max` are
zero.

## Build

The project targets .NET Framework 4.8 and requires the BepInEx and Valheim
assemblies from a local game installation. Build with the .NET SDK:

```powershell
dotnet build -c Release -p:ValheimDir="C:\Program Files (x86)\Steam\steamapps\common\Valheim"
```

`ValheimDir` should point to the installation root containing `BepInEx` and
either `valheim_Data/Managed` or `valheim_server_Data/Managed`.
