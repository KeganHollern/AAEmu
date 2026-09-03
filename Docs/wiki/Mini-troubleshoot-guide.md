# Mini Troubleshoot Guide

- Audience: Contributors, players, and testers
- Last verified against: `develop` on February 28, 2026
- Prerequisites: None

Use this page for common startup and connection problems.

## Common issues

### Missing tables or SQL errors on startup

Game server: confirm you imported `SQL/aaemu_game.sql` (manual path), or let
Aspire initialize `aaemu_game` (Aspire path).

Login server: confirm the `aaemu_login` database exists. The Go login server
creates the tables at first start when the `users` table is missing. It does
not create the database. If the log shows `Unknown database`, create an empty
`aaemu_login` database and restart the login server.

### Login server exits with a configuration error

A required `AAEMU_LOGIN_*` variable is missing or invalid.
`AAEMU_LOGIN_SECRET_KEY`, `AAEMU_LOGIN_MYSQL_HOST`, `AAEMU_LOGIN_MYSQL_USER`,
and `AAEMU_LOGIN_GAME_SERVERS` are required. See `server/README.md` in
`aaemu-cluster`.

### Server list is empty in client

Verify the `host` and `port` in `AAEMU_LOGIN_GAME_SERVERS` are reachable by
the client, and that the game server registered. The login server log shows
`game server registered` on success.

### Server list shows Maintenance

The game server did not register with the login server. Check:

1. the Go login server is running and listens on `1234`,
1. the game `LoginNetwork` host and port point at that listener,
1. the game `SecretKey` equals `AAEMU_LOGIN_SECRET_KEY`.

### Crash after selecting server

Usually this means wrong game host or port in `AAEMU_LOGIN_GAME_SERVERS`.

### Aspire does not start services

Ensure Docker or Podman is installed and running, then relaunch
`AAEmu.Aspire.AppHost`.

### Game cannot load world assets

Verify `compact.sqlite3` is in `AAEmu.Game/Data` and `game_pak` path is
correct.

### Linux file descriptor errors

Increase OS file descriptor limits for the server process.

## Important change

Do not rely on MySQL `aaemu_login.game_servers` as a source of server listings.
The login server is now the Go binary in `KeganHollern/aaemu-cluster`
(`server/cmd/login`). Server listings come from its `AAEMU_LOGIN_GAME_SERVERS`
environment variable.

## Related

- [FAQ](FAQ)
- [Installation & Setup](Installation-&-Setup)
- [Working with the Config.json files and server listings](Working-with-the-Config.json-files-and-server-listings)
