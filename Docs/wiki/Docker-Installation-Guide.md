# Docker Installation Guide

- Audience: Operators and contributors
- Last verified against: `develop` on February 28, 2026
- Prerequisites: Docker runtime, Git, Go toolchain, a clone of
  `KeganHollern/aaemu-cluster`, required AAEmu data files

## When to use this guide

Use this guide when you want a containerized AAEmu game server without Aspire
orchestration. The compose file starts MySQL, Adminer, and the game server.
The login server is the Go binary from `aaemu-cluster` (`server/cmd/login`).
It runs on the host, not in a container.

If you want the preferred contributor startup flow, use
[Aspire Development Guide](Aspire-Development-Guide).

## Prerequisites

1. Install Git.
1. Install Docker Desktop (Windows) or Docker Engine + Compose (Linux).
1. Install the Go toolchain.
1. Clone `KeganHollern/aaemu-cluster`. The login server is in `server/`.
1. Place required files where scripts expect them:
   - `compact.sqlite3`
   - ArcheAge `game_pak`

## Initial install

1. Clone `https://github.com/AAEmu/AAEmu`.
1. Change directory to `Scripts`.
1. Run:
   - Windows: `docker-install-local.ps1`
   - Linux: `docker-install-local.sh`

## Update an existing install

1. Change directory to `Scripts`.
1. Run:
   - Windows: `docker-update-local.ps1`
   - Linux: `docker-update-local.sh`

## Launch

From project root:

- Detached mode: `docker compose up -d`
- Dev/watch mode: `docker compose watch`

Then start the Go login server on the host. See the next section.

### Start the Go login server (host)

The installer scripts write `host.docker.internal` and `1234` into the game
`LoginNetwork` settings. The game container reaches the login server on the
host through that name.

1. Create an empty `aaemu_login` database in the compose MySQL service.
   Use Adminer at `http://localhost:8081` (server `db`, user `root`, the
   `DB_PASSWORD` from `.env`) or the `mysql` client on host port `3306`.
1. In `aaemu-cluster/server`, set the environment variables and run the
   server:

```bash
cd aaemu-cluster/server
AAEMU_LOGIN_SECRET_KEY=test \
AAEMU_LOGIN_MYSQL_HOST=127.0.0.1 AAEMU_LOGIN_MYSQL_PORT=3306 \
AAEMU_LOGIN_MYSQL_USER=root AAEMU_LOGIN_MYSQL_PASSWORD=YOUR_DB_PASSWORD \
AAEMU_LOGIN_AUTO_ACCOUNT=true \
AAEMU_LOGIN_GAME_SERVERS='[{"id":1,"name":"AAEmu.Game","host":"127.0.0.1","port":1239}]' \
go run ./cmd/login
```

The login server creates the `aaemu_login` tables at first start when the
`users` table is missing. `AAEMU_LOGIN_SECRET_KEY` must equal the game
`SecretKey` in `.server_files/AAEmu.Game/Config.json`.
See `aaemu-cluster/server/README.md` for every variable.

## Important configuration notes

### Host ports

- `1237`: login server client port (host).
- `1234`: login server internal game link (host, reached by the game
  container as `host.docker.internal:1234`).
- `8080`: login server launcher API and health (host).
- `9090`: login server metrics (host).
- `8081`: Adminer (compose, mapped from container port `8080`).
- `3306`: MySQL (compose).
- `1239` and `1250`: game server (compose).

### Server listing source

Server listings come from the login server environment variable
`AAEMU_LOGIN_GAME_SERVERS`, a JSON array, for example:

```text
AAEMU_LOGIN_GAME_SERVERS='[{"id":1,"name":"AAEmu.Game","host":"127.0.0.1","port":1239,"hidden":false}]'
```

Set `host` to an address that game clients can reach.
Do not depend on MySQL `aaemu_login.game_servers` inserts.

## Troubleshooting

- Docker API or daemon not available: start Docker before running commands.
- Installation script fails on Windows policy: adjust execution policy for your
  user if needed.
- Services start but client cannot connect: verify the
  `AAEMU_LOGIN_GAME_SERVERS` host/port and exposed compose ports.
- Game log shows no login connection: verify the Go login server is running
  on the host and listens on `1234`, and that `host.docker.internal` resolves
  in the game container.

## Related

- [Home](Home)
- [Aspire Development Guide](Aspire-Development-Guide)
- [Installation & Setup](Installation-&-Setup)
- [Mini troubleshoot guide](Mini-troubleshoot-guide)
