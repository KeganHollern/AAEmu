# Aspire Development Guide

- Audience: Contributors
- Last verified against: `develop` on February 28, 2026
- Prerequisites: `.NET 10 SDK`, Go toolchain, OCI runtime, a clone of
  `KeganHollern/aaemu-cluster`, and required downloaded dependencies

## Why this guide exists

The preferred way to run AAEmu locally is now the Aspire AppHost project.
It simplifies local startup by orchestrating MySQL and the game server.
The login server is the Go binary from `aaemu-cluster` (`server/cmd/login`).
It runs on the host outside Aspire.

Aspire is optional. Manual and Docker workflows are still supported.

## Prerequisites

1. Install `.NET 10 SDK`.
1. Install the Go toolchain.
1. Install an OCI-compliant container runtime:
   - Docker Desktop, or
   - Podman.
1. Clone the `AAEmu` repository (recommended branch: `develop`).
1. Clone `KeganHollern/aaemu-cluster`. The login server is in `server/`.
1. Download required files from
   [Dependencies and Downloads](Dependencies-and-Downloads):
   - `compact.sqlite3`
   - ArcheAge 1.2 client
   - AAEmu Launcher
1. Place `compact.sqlite3` in `AAEmu.Game/Data`.
1. Configure `game_pak` path in game configuration.

### Recommended way to set `game_pak`

Set the path in `AAEmu.Game/Config.Local.json` so your local machine override
wins over all other game config files.
`Config.Local.json` is loaded last and overrides all previous game config JSON sources.

## First run (preferred local workflow)

1. Open the AAEmu solution in your IDE.
1. Select launch profile `AAEmu.Aspire.AppHost: http`.
1. Run in Debug.

Expected behavior:

1. Aspire starts the MySQL container.
1. Aspire initializes `aaemu_game` using an idempotent SQL script.
1. Game server starts after MySQL is ready and connects to the login server
   at `login-host` and `login-port`.
1. Aspire dashboard opens and shows service health/state.

## Run the Go login server

Aspire does not start the login server. Start it yourself from
`aaemu-cluster/server`:

1. Create an empty `aaemu_login` database in the Aspire MySQL container.
   Read the container host port and root password from the `db` resource in
   the dashboard.
1. Set the `AAEMU_LOGIN_*` environment variables. Minimum:
   `AAEMU_LOGIN_SECRET_KEY` (equal to the game `SecretKey`),
   `AAEMU_LOGIN_MYSQL_HOST`, `AAEMU_LOGIN_MYSQL_PORT`, `AAEMU_LOGIN_MYSQL_USER`,
   `AAEMU_LOGIN_MYSQL_PASSWORD`, and `AAEMU_LOGIN_GAME_SERVERS`, for example
   `'[{"id":1,"name":"Local","host":"127.0.0.1","port":1239}]'`.
   Set `AAEMU_LOGIN_AUTO_ACCOUNT=true` for local accounts.
1. Run `go run ./cmd/login`.
1. If the game server started before the login server was ready, restart the
   `game-server` resource in the dashboard.

The login server creates the `aaemu_login` tables at first start when the
`users` table is missing. It listens on `1237` (client), `1234` (internal
game link), `8080` (launcher API and health), and `9090` (metrics).
See `aaemu-cluster/server/README.md` for the full configuration reference.

## What Aspire wires automatically

Aspire passes runtime configuration through environment variables, including:

- Game DB connection settings.
- Game `LoginNetwork` host and port from the AppHost parameters `login-host`
  (default `127.0.0.1`) and `login-port` (default `1234`).

Override the parameters when the login server listens somewhere else. Add a
`Parameters` section to `AAEmu.Aspire.AppHost/appsettings.Development.json`
or to the AppHost user secrets, for example:

```json
{
  "Parameters": {
    "login-host": "192.168.1.10",
    "login-port": "1234"
  }
}
```

Server listings come from the login server `AAEMU_LOGIN_GAME_SERVERS` value.
Local startup does not require game server listing rows in MySQL.

## Debugging with Aspire

Running AppHost in Debug still allows breakpoints in `AAEmu.Game`.
The Go login server is a separate process. Attach a Go debugger to it if needed.
Use the dashboard and project logs to identify startup sequencing and
dependency issues.

## Health and readiness

The Go login server includes health endpoints on port `8080`:

- `/health/live`
- `/health/ready`

In Aspire, monitor game readiness from dashboard state and resource logs.

## Common issues

- OCI runtime not running: start Docker Desktop or Podman first.
- `compact.sqlite3` missing: place it in `AAEmu.Game/Data`.
- Invalid `game_pak` path: set it in `Config.Local.json` and re-run.
- Port conflict on `1237`, `1234`, `8080`, `9090`, `1239`, or `1250`: free the
  port or adjust local setup.
- Missing server list in client: verify the login server
  `AAEMU_LOGIN_GAME_SERVERS` value, not MySQL `game_servers`.
- Client stuck on Maintenance: the game server did not register. Check that
  the game `SecretKey` equals `AAEMU_LOGIN_SECRET_KEY` and that `login-host`
  and `login-port` point at the login server internal listener.

## Related

- [Home](Home)
- [Dependencies and Downloads](Dependencies-and-Downloads)
- [Installation & Setup](Installation-&-Setup)
- [Working with the Config.json files and server listings](Working-with-the-Config.json-files-and-server-listings)
- [Mini troubleshoot guide](Mini-troubleshoot-guide)
