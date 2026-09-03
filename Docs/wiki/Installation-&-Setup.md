# Installation & Setup

- Audience: Contributors, players, and testers
- Last verified against: `develop` on February 28, 2026
- Prerequisites: `.NET 10 SDK`, Go toolchain, a clone of
  `KeganHollern/aaemu-cluster`, required AAEmu dependencies/downloads, and
  MySQL for manual track

This page now has two setup paths. On both paths the login server is the Go
binary from `aaemu-cluster` (`server/cmd/login`). AAEmu no longer contains a
login server project.

1. `Track A (Preferred)`: Aspire local development workflow.
1. `Track B`: Manual setup workflow.

## Track A (Preferred): Aspire workflow

Use this path if you want the fastest contributor onboarding.

### Requirements

1. Install `.NET 10 SDK`.
1. Install the Go toolchain.
1. Install an OCI-compliant runtime (Docker Desktop or Podman).
1. Clone [AAEmu](https://github.com/AAEmu/AAEmu) (`develop` branch recommended).
1. Clone `KeganHollern/aaemu-cluster`. The login server is in `server/`.
1. Download required files from [Dependencies and Downloads](Dependencies-and-Downloads):
   - `compact.sqlite3`
   - ArcheAge 1.2 client
   - AAEmu Launcher
1. Place `compact.sqlite3` in `AAEmu.Game/Data`.
1. Set your `game_pak` path.

### Set `game_pak` path

Recommended: put the path in `AAEmu.Game/Config.Local.json` so it overrides
other game config files.

### Launch with Aspire

1. Open the solution in your IDE.
1. Select launch profile `AAEmu.Aspire.AppHost: http`.
1. Run in Debug.

Expected startup sequence:

1. MySQL container starts.
1. `aaemu_game` is initialized with idempotent SQL.
1. Game service starts and connects to the login server at `login-host`
   and `login-port` (defaults `127.0.0.1` and `1234`).
1. Aspire dashboard opens with service state and logs.

### Start the Go login server (Aspire track)

Aspire does not host the login server. Run it on the host from
`aaemu-cluster/server`:

1. Create an empty `aaemu_login` database in the Aspire MySQL container.
   Read the container host port and root password from the `db` resource in
   the dashboard.
1. Set the `AAEMU_LOGIN_*` environment variables. See
   `aaemu-cluster/server/README.md` for the full list. Minimum:
   `AAEMU_LOGIN_SECRET_KEY`, `AAEMU_LOGIN_MYSQL_HOST`,
   `AAEMU_LOGIN_MYSQL_PORT`, `AAEMU_LOGIN_MYSQL_USER`,
   `AAEMU_LOGIN_MYSQL_PASSWORD`, `AAEMU_LOGIN_GAME_SERVERS`.
1. Run `go run ./cmd/login`.
1. If the game service started before the login server was ready, restart
   the `game-server` resource in the dashboard.

The login server creates the `aaemu_login` tables at first start when the
`users` table is missing.

For full details, see [Aspire Development Guide](Aspire-Development-Guide).

## Track B: Manual setup workflow

Use this path if you do not want to use Aspire.

### Manual requirements

1. Install MySQL 8.x.
1. Install `.NET 10 SDK`.
1. Install the Go toolchain.
1. Clone [AAEmu](https://github.com/AAEmu/AAEmu) (`develop` branch recommended).
1. Clone `KeganHollern/aaemu-cluster`. The login server is in `server/`.
1. Download required files from [Dependencies and Downloads](Dependencies-and-Downloads):
   - `compact.sqlite3`
   - ArcheAge 1.2 client
   - AAEmu Launcher
1. Place `compact.sqlite3` in `AAEmu.Game/Data`.

### Database setup (manual)

1. Create two schemas in MySQL:
   - `aaemu_login` (leave it empty)
   - `aaemu_game`
1. Import:
   - `SQL/aaemu_game.sql`

Do not import a login SQL file. The Go login server creates the `aaemu_login`
tables at first start when the `users` table is missing.

Do not insert rows into `aaemu_login.game_servers`.
Game server listing is configured through the login server environment
variable `AAEMU_LOGIN_GAME_SERVERS`.

### Login server configuration (manual)

The Go login server reads environment variables only. It has no
`Config.json`. Set at least these values before you start it:

```bash
AAEMU_LOGIN_SECRET_KEY=test
AAEMU_LOGIN_AUTO_ACCOUNT=true
AAEMU_LOGIN_MYSQL_HOST=127.0.0.1
AAEMU_LOGIN_MYSQL_PORT=3306
AAEMU_LOGIN_MYSQL_USER=your_user
AAEMU_LOGIN_MYSQL_PASSWORD=your_password
AAEMU_LOGIN_MYSQL_DATABASE=aaemu_login
AAEMU_LOGIN_GAME_SERVERS='[{"id":1,"name":"AAEmu.Game","host":"127.0.0.1","port":1239,"hidden":false}]'
```

`AAEMU_LOGIN_SECRET_KEY` must equal the game server `SecretKey`.
The login server listens on `1237` (client), `1234` (internal game link),
`8080` (launcher API and health), and `9090` (metrics).
See `aaemu-cluster/server/README.md` for every variable.

### Game server configuration (manual)

Create or edit `AAEmu.Game/Config.Local.json`.
Because `Config.Local.json` is loaded last, it overrides all other game config
JSON files.

At minimum, set database and login network values for your machine.
`LoginNetwork.Host` and `LoginNetwork.Port` must point at the login server
internal listener (default `127.0.0.1` and `1234`).

Set `game_pak` source in either:

- `AAEmu.Game/Configurations/ClientData.json`, or
- `AAEmu.Game/Config.Local.json` as an override.

### Build and run (manual)

1. Build:

```bash
dotnet build
```

1. Start the Go login server: run `go run ./cmd/login` in
   `aaemu-cluster/server` with the environment variables set.
1. Start the game server: run `dotnet run --project AAEmu.Game` or use your
   IDE.

Start the login server before the game server.

### Launcher setup

1. Open AAEmu Launcher.
1. Set `Path to Game` to your `archeage.exe` in the client `bin32` folder.
1. Set login credentials.

If `AAEMU_LOGIN_AUTO_ACCOUNT=true`, accounts are created on first login.

## Docker workflow

If you prefer containers without Aspire orchestration, see
[Docker Installation Guide](Docker-Installation-Guide).

## Related

- [Home](Home)
- [Dependencies and Downloads](Dependencies-and-Downloads)
- [Aspire Development Guide](Aspire-Development-Guide)
- [Working with the Config.json files and server listings](Working-with-the-Config.json-files-and-server-listings)
- [Mini troubleshoot guide](Mini-troubleshoot-guide)
