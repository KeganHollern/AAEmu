# Working with Config Files and Server Listings

- Audience: Contributors, players, and testers
- Last verified against: `deployment/r208022` on August 17, 2026
- Prerequisites: Basic JSON editing and AAEmu project structure familiarity

## Overview

The login server is the Go binary `server/cmd/login` in
`KeganHollern/aaemu-cluster`. It stores server listings in the environment
variable `AAEMU_LOGIN_GAME_SERVERS` instead of MySQL
`aaemu_login.game_servers`.

This allows consistent behavior across manual, Docker, and Aspire workflows.

## Login server configuration (environment variables)

The Go login server reads environment variables only. It has no `Config.json`
or `Config.Local.json`. The full reference is `server/README.md` in
`aaemu-cluster`. The values that a local setup needs:

| Variable | Default | Meaning |
| --- | --- | --- |
| `AAEMU_LOGIN_SECRET_KEY` | required | Shared secret. Must equal the game server `SecretKey`. |
| `AAEMU_LOGIN_AUTO_ACCOUNT` | `false` | Create an account on first login of an unknown username. |
| `AAEMU_LOGIN_CLIENT_LISTEN` | `0.0.0.0:1237` | Game-client login listener. |
| `AAEMU_LOGIN_INTERNAL_LISTEN` | `0.0.0.0:1234` | Game-server link listener. The game `LoginNetwork` must point here. |
| `AAEMU_LOGIN_HTTP_LISTEN` | `0.0.0.0:8080` | Launcher API and health listener. |
| `AAEMU_LOGIN_METRICS_LISTEN` | `0.0.0.0:9090` | Prometheus `/metrics` listener. |
| `AAEMU_LOGIN_MYSQL_HOST` | required | MySQL host. |
| `AAEMU_LOGIN_MYSQL_PORT` | `3306` | MySQL port. |
| `AAEMU_LOGIN_MYSQL_USER` | required | MySQL user. |
| `AAEMU_LOGIN_MYSQL_PASSWORD` | empty | MySQL password. |
| `AAEMU_LOGIN_MYSQL_DATABASE` | `aaemu_login` | Schema name. The database must exist. The server creates the tables. |
| `AAEMU_LOGIN_GAME_SERVERS` | required | JSON array of game servers. See below. |

### `AAEMU_LOGIN_GAME_SERVERS` schema

`AAEMU_LOGIN_GAME_SERVERS` is a JSON array of server entries with this shape:

```json
[
  {
    "id": 1,
    "name": "AAEmu.Game",
    "host": "127.0.0.1",
    "port": 1239,
    "hidden": false
  }
]
```

Field meanings:

- `id`: unique game server id. The game server registers with this id.
- `name`: display name in server selection.
- `host`: address reachable by clients.
- `port`: client connection port.
- `hidden`: hide this entry from the listing. Hidden servers can still
  register.

Shell example:

```bash
AAEMU_LOGIN_GAME_SERVERS='[{"id":1,"name":"AAEmu.Game","host":"127.0.0.1","port":1239,"hidden":false}]'
```

## Game server configuration and precedence

The game server supports `Config.Local.json` as the final override layer.

Effective load order:

1. `AAEmu.Game/Config.json`
1. `AAEmu.Game/Configurations/*.json` (all matching files)
1. `AAEmu.Game/Config.Local.json` (loaded last)

If the same setting exists in multiple places, `Config.Local.json` wins.

### Login link settings in the game server

Two game settings must agree with the login server:

- `SecretKey`: must equal `AAEMU_LOGIN_SECRET_KEY`.
- `LoginNetwork.Host` and `LoginNetwork.Port`: must point at the login server
  internal listener (`AAEMU_LOGIN_INTERNAL_LISTEN`, default port `1234`).

Example `AAEmu.Game/Config.Local.json`:

```json
{
  "SecretKey": "test",
  "LoginNetwork": {
    "Host": "127.0.0.1",
    "Port": 1234
  }
}
```

Under Aspire, the AppHost parameters `login-host` and `login-port` set these
two `LoginNetwork` values. Under Docker Compose, the installer scripts set
`host.docker.internal` and `1234`.

## `game_pak` configuration

Set `game_pak` source in one of these places:

- `AAEmu.Game/Configurations/ClientData.json`, or
- `AAEmu.Game/Config.Local.json` for local override.

For contributor workflows, prefer `Config.Local.json`.

## Dungeon instance creation throttle

The game server limits how quickly one character can create new player-owned
dungeon instances in the same dungeon zone. The default is three successful
creations in a rolling 15-minute window. Joining a party's instance,
re-entering an existing instance, reconnecting, and entering a system instance
do not consume this allowance.

Override the defaults in `AAEmu.Game/Config.Local.json` when needed:

```json
{
  "Dungeons": {
    "CreationLimit": 3,
    "CreationWindowMinutes": 15
  }
}
```

Set either value to `0` to disable the creation throttle. The creation history
is held in memory and resets when the game server restarts.

## Migration note from old setup docs

Older instructions referenced `aaemu_login.game_servers` and SQL inserts, a
C# `AAEmu.Login` project with `Config.json`, `GameServers` JSON, and
`GameServers__0__*` environment variables. None of these exist now.

Use the Go login server from `aaemu-cluster` and its
`AAEMU_LOGIN_GAME_SERVERS` variable instead.

## Related

- [Installation & Setup](Installation-&-Setup)
- [Aspire Development Guide](Aspire-Development-Guide)
- [Mini troubleshoot guide](Mini-troubleshoot-guide)
- [FAQ](FAQ)
