# Valheim Telemetry

A server-side BepInEx 5 plugin that exposes a REST API and Prometheus metrics
for a Valheim dedicated server.

## Features

- `GET /health` reports plugin status and version.
- `GET /api/v1/players` returns online player IDs, names, health, and deaths
  observed by the plugin.
- `GET /api/v1/players/{playerId}` returns one online player's data.
- `GET /api/v1/events` returns observed player join and leave events.
- `GET /metrics` exposes Prometheus metrics for server tick duration and stalls,
  process and system CPU, memory, garbage collection, network traffic, ZDO
  activity, socket send queues, connected peers, world, players, observed player
  deaths, and portals.
- Optional API-key authentication and configurable listener address and port.

## Player data limitations

Health and maximum health are read from the player's server-side ZDO when that
ZDO is available. The server does not receive the player's stamina, eitr, or
adrenaline fields; these values are intentionally not included in the API.
Obtaining them would require a client-side mod to send the values to the server.

Death counts record dead-state transitions observed while the plugin is
running and are stored in
`BepInEx/config/br.com.midgard.valheimtelemetry.players`. They survive plugin
and server restarts and are not loaded from the player's saved profile. The
`valheim_player_deaths{player_id,name}` metric includes known players whether
online or offline. The `valheim_player_health{player_id,name}` and
`valheim_player_health_max{player_id,name}` metrics are published while the
server has the player's health ZDO. The portal metric counts portal ZDOs
currently tracked by the server.

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
GET /api/v1/events?after_id=0
GET /metrics
```

The events endpoint returns up to the latest 1,000 events from the current
plugin session. Use `after_id` with the last event's `id` to poll only newer
events. Event IDs and history reset when the plugin restarts. Events are
detected from player-list snapshots; the first snapshot establishes a baseline.

## Lag metrics

- `valheim_server_tick_duration_seconds` is a cumulative histogram of server
  main-thread frame durations. `valheim_server_tick_stalls_total` counts frames
  lasting at least 100 ms. Both reset when the plugin restarts.
- `valheim_process_cpu_core_percent` measures the process against one logical
  core; `valheim_process_cpu_percent` remains normalized against all host cores.
  `valheim_system_cpu_percent` measures the whole machine and is `-1` until a
  baseline is available or when the platform counters cannot be read.
- `valheim_server_networked_objects`, `valheim_server_objects_instantiated`,
  `valheim_server_zdos_sent_per_second`, `valheim_server_zdos_received_per_second`,
  and `valheim_server_zdo_change_queue` expose the server's ZDO state and the
  latest rates reported by the game.
- `valheim_server_send_queue_bytes` is the largest send queue among connected
  peers. Check `valheim_server_send_queue_available`; it is `0` if any peer
  socket cannot be read.

Tick duration is a server-side frame-time proxy, not a measurement of client
latency. Per-player ping and packet loss are not exported because dedicated
server sockets do not reliably expose them.

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
assemblies. For a local build, `ValheimDir` must point to a game installation:

```powershell
dotnet build -c Release -p:ValheimDir="C:\Program Files (x86)\Steam\steamapps\common\Valheim"
```

`ValheimDir` should point to the installation root containing `BepInEx` and
either `valheim_Data/Managed` or `valheim_server_Data/Managed`.

## GitHub Release

Pushing a version tag such as `v1.2.0` runs the GitHub Actions release workflow.
It builds the plugin and attaches `ValheimTelemetry.dll` and
`ValheimTelemetry-v1.2.0.zip` to the GitHub Release. Release notes are stored in
`.github/release-notes/` with the matching tag as the filename.

The workflow runs on an Ubuntu-hosted runner. It downloads the Valheim Dedicated
Server assemblies with SteamCMD and BepInEx 5.4.23.2 for compilation; these
dependencies are not included in the published release. Push the tag to start
the workflow:

```powershell
git tag v1.2.0
git push origin v1.2.0
```
