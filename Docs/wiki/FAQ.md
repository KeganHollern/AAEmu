# FAQ

- Audience: Contributors, testers, and players
- Last verified against: `develop` on February 28, 2026
- Prerequisites: None

## Project basics

### What is AAEmu

AAEmu is an open source server emulator for ArcheAge 1.2.

### Which client version is supported

`develop` targets ArcheAge 1.2 (`r208022`).

### How can I help

Join the community on [Discord](https://discord.gg/aaemu) and contribute code,
testing, or issue reports.

### Where should I ask support questions

Use both official support channels as needed:

- Real-time troubleshooting:
  [Discord](https://discord.gg/aaemu)
- Searchable long-form support:
  [GitHub Discussions](https://github.com/AAEmu/AAEmu/discussions)

See [Getting Help](Getting-Help) and [Help Us Help You](Help-Us-Help-You).

## Setup and local development

### Preferred way to run locally

Use [Aspire Development Guide](Aspire-Development-Guide).

### Can I still use manual setup or Docker

Yes. Both are still supported:

- [Installation & Setup](Installation-&-Setup)
- [Docker Installation Guide](Docker-Installation-Guide)

### Where is the login server

The login server is not in this repository. It is the Go binary
`server/cmd/login` in `KeganHollern/aaemu-cluster`. Run it from
`aaemu-cluster/server` with `go run ./cmd/login`. Its configuration is
environment variables (`AAEMU_LOGIN_*`), documented in `server/README.md` in
that repo.

### Do I import a login SQL file

No. Create an empty `aaemu_login` database. The Go login server creates the
tables at first start when the `users` table is missing.

### Do I still insert game servers into MySQL `aaemu_login.game_servers`

No. Server listings are defined in the login server environment variable
`AAEMU_LOGIN_GAME_SERVERS`, a JSON array such as
`[{"id":1,"name":"Local","host":"127.0.0.1","port":1239}]`.

### Config precedence after the recent PR wave

For game server config, `Config.Local.json` is loaded last and overrides all
other game config files.

## Client and launcher

### Where can I find the launcher

Use the latest release:
[AAEmu Launcher releases](https://github.com/ZeromusXYZ/AAEmu-Launcher/releases)

### Where can I find client downloads

Use the client list on [Client](Client).

If you need a directory of many client versions, use:
[MEGA client directory](https://mega.nz/folder/C3Q0WQjT#vRUethZLPiYSo2B4nE_etg).

For the full dependency checklist (including `compact.sqlite3` and launcher),
see [Dependencies and Downloads](Dependencies-and-Downloads).

## Configuration and networking

### Which ports matter by default

Common defaults:

- `1237`: login public (Go login server)
- `1234`: login internal game link (Go login server)
- `8080`: login launcher API and health (Go login server)
- `9090`: login metrics (Go login server)
- `1239`: game public
- `1250`: game stream
- `1280`: optional game Web API
- `1281`: game liveness and readiness checks

### What should the `AAEMU_LOGIN_GAME_SERVERS` host be for local use

Use `127.0.0.1` for local machine tests.
For LAN or external players, set a reachable address.

### Which secret must match

The game server `SecretKey` (in `AAEmu.Game/Config.json` or
`Config.Local.json`) must equal the login server `AAEMU_LOGIN_SECRET_KEY`.
If they differ, the game server cannot register and clients see Maintenance.

## Troubleshooting

### Login server exits at startup with a configuration error

A required `AAEMU_LOGIN_*` variable is missing. `AAEMU_LOGIN_SECRET_KEY`,
`AAEMU_LOGIN_MYSQL_HOST`, `AAEMU_LOGIN_MYSQL_USER`, and
`AAEMU_LOGIN_GAME_SERVERS` are required. See `server/README.md` in
`aaemu-cluster`.

### Client crashes or cannot enter world

Check:

1. the Go login server and the game server are both running,
1. `AAEMU_LOGIN_GAME_SERVERS` host/port are reachable,
1. `game_pak` path and `compact.sqlite3` are valid.

### First stop for common setup failures

Use [Mini troubleshoot guide](Mini-troubleshoot-guide).

## Related

- [Home](Home)
- [Installation & Setup](Installation-&-Setup)
- [Working with the Config.json files and server listings](Working-with-the-Config.json-files-and-server-listings)
- [Getting Help](Getting-Help)
