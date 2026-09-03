---
name: aaemu-setup
description: >
  Guide anyone (player or contributor) through getting AAEmu running and
  playable: asset inventory, Human-in-the-Loop downloads, config, then either
  Docker/Podman + Aspire or non-Docker host MySQL + standalone Game. The login
  server is the Go binary from aaemu-cluster on both paths.
  Use when the user wants to set up AAEmu, install/run the server, play on a
  local server, get the client/launcher working, choose Docker vs non-Docker,
  or mentions game_pak, compact.sqlite3, Aspire, Config.Local, or Maintenance.
---

# AAEmu setup (players and contributors)

Guide the human end-to-end. Do not assume they are a developer. Prefer plain
language; use technical detail only when needed for the chosen path.

## Non-negotiables

1. **Two pure run paths only** — no hybrids:

| Environment | Path | Database | Login / Game |
| --- | --- | --- | --- |
| Docker Desktop **or** Podman available | **A – Aspire** | MySQL **container** (AppHost) | Login: Go server from `aaemu-cluster/server`, run on the host. Game: started by Aspire as a host project |
| **No** container runtime | **B – Standalone** | **Host MySQL 8 only** | Host processes. Go login server first, then Game |

Non-Docker means **zero** containers, including MySQL. Never invent “Docker only for the database” for Path B.

2. **Human-in-the-Loop (HitL) for large game assets** — client and compact DB
   archives live on MEGA/Drive (~multi‑GB). The agent must **not** silently
   re-download them. Always inventory first; only ask the human to download
   what is missing.

3. Official human docs: `Docs/wiki/Installation-&-Setup.md`,
   `Dependencies-and-Downloads.md`, `Client.md`, `Aspire-Development-Guide.md`.

## Workflow (always in this order)

### Step 0 — Detect audience and path

Ask if unclear:

- Goal: **play** on a local server, **contribute/code**, or both?
- Is **Docker Desktop or Podman** installed and usable?

Default: Path A if OCI works; Path B if user has or wants no Docker.

### Step 1 — Inventory assets (never blind-download)

Use the matching shell for the host (same checks, same exit codes):

| Shell | Inventory | Fetch launcher if missing | Open missing download pages (HitL) |
| --- | --- | --- | --- |
| **PowerShell** (Windows) | `powershell -File .agents/skills/aaemu-setup/scripts/Test-AaemuAssets.ps1` | add `-FetchLauncherIfMissing` | add `-OpenMissingDownloadPages` |
| **Bash** (Linux / macOS / WSL / Git Bash) | `bash .agents/skills/aaemu-setup/scripts/test-aaemu-assets.sh` | add `--fetch-launcher` | add `--open-missing` |

Examples:

```powershell
# Windows PowerShell
powershell -File .agents/skills/aaemu-setup/scripts/Test-AaemuAssets.ps1
powershell -File .agents/skills/aaemu-setup/scripts/Test-AaemuAssets.ps1 -FetchLauncherIfMissing
```

```bash
# Linux / macOS / WSL
bash .agents/skills/aaemu-setup/scripts/test-aaemu-assets.sh
bash .agents/skills/aaemu-setup/scripts/test-aaemu-assets.sh --fetch-launcher
chmod +x .agents/skills/aaemu-setup/scripts/test-aaemu-assets.sh   # optional once
```

Interpret the report:

| Status | Action |
| --- | --- |
| **OK** | Do not download or re-extract that asset |
| **MISSING** | HitL: give the human the link, wait, re-scan |
| **PARTIAL** | Archive present but not extracted (or wrong layout) — extract only |

Canonical local layout (gitignored `.client_files/`):

```text
.client_files/
  ArcheAge 1.2 (r208022) for AAEmu/     # game_pak + bin32/archeage.exe
  launcher/AAEmu.Launcher/              # AAEmu.Launcher.exe
  *.7z / *.zip                          # optional kept archives
AAEmu.Game/Data/compact.sqlite3
```

**MEGA / Drive (client, compact.sqlite3):** open the wiki links for the human
(or browser). Wait until they confirm the file is saved (prefer
`.client_files/`). Re-run the inventory script. Extract if needed.  
**Do not** loop re-downloads when `game_pak` / `archeage.exe` / `compact.sqlite3`
already satisfy the script checks.

**Launcher (GitHub):** safe to auto-fetch with `-FetchLauncherIfMissing` /
`--fetch-launcher` when absent; still skip when present.

**Note:** The official game client/launcher are **Windows** binaries. Bash
inventory still validates server-side assets (`game_pak`, `compact.sqlite3`) on
Linux hosts that only run Login/Game.

Details and URLs: [REFERENCE.md](REFERENCE.md#downloads-and-hitl).

### Step 2 — Machine prerequisites

- **Both paths:** .NET **10** SDK (`dotnet --version`).
- **Both paths:** Go toolchain (`go version`) and a clone of
  `KeganHollern/aaemu-cluster`. The login server is `server/cmd/login` in that
  repo. Its README (`server/README.md`) lists all environment variables.
- **Path A:** Docker Desktop or Podman **running**.
- **Path B:** MySQL **8** installed and running **on the host** (service),
  `aaemu_game` schema imported once (`SQL/aaemu_game.sql`), `aaemu_login`
  database created empty. Do not import a login SQL file. The Go login server
  creates the login tables at first start.

Help non-developers install these with OS-appropriate steps; do not skip
waiting for services to actually start.

### Step 3 — Local config (gitignored)

Write/update (templates in [REFERENCE.md](REFERENCE.md#configlocal-templates)):

- `AAEmu.Game/Config.Local.json` — at least `ClientData.Sources` (+ DB/LoginNetwork on Path B)
- Go login server: environment variables, not a JSON file:
  `AAEMU_LOGIN_SECRET_KEY`, `AAEMU_LOGIN_MYSQL_HOST/PORT/USER/PASSWORD`,
  `AAEMU_LOGIN_AUTO_ACCOUNT`, `AAEMU_LOGIN_GAME_SERVERS`
  (+ `AAEMU_LOGIN_INTERNAL_LISTEN` if port remap)

Use absolute paths under this repo for `game_pak`. Set the Game `SecretKey`
equal to `AAEMU_LOGIN_SECRET_KEY`. Keep the Game `LoginNetwork.Port` equal to
the login server internal port (default `1234`). Rebuild after creating
`Config.Local.json` so it copies to output.

### Step 4 — Start servers

**Both paths, Go login server first** (from the `aaemu-cluster` clone):

```bash
cd aaemu-cluster/server
AAEMU_LOGIN_SECRET_KEY=test \
AAEMU_LOGIN_MYSQL_HOST=127.0.0.1 AAEMU_LOGIN_MYSQL_PORT=3306 \
AAEMU_LOGIN_MYSQL_USER=root AAEMU_LOGIN_MYSQL_PASSWORD=YOUR_MYSQL_PASSWORD \
AAEMU_LOGIN_AUTO_ACCOUNT=true \
AAEMU_LOGIN_GAME_SERVERS='[{"id":1,"name":"Local","host":"127.0.0.1","port":1239}]' \
go run ./cmd/login
```

The server waits for MySQL, creates the `aaemu_login` tables when the `users`
table is missing, then listens on 1237 (client), 1234 (internal), 8080
(launcher API), and 9090 (metrics). The `aaemu_login` database must already
exist (empty is fine). See `aaemu-cluster/server/README.md` for every variable.

**Path A** (same `dotnet` commands on PowerShell or bash):

```bash
dotnet run --project AAEmu.Aspire.AppHost --launch-profile http
```

Share the dashboard login URL/token from the console. Only MySQL is a container.
The Go login server and Game are normal host processes. Aspire creates only
`aaemu_game`. Create an empty `aaemu_login` database in the Aspire MySQL
container once, then point `AAEMU_LOGIN_MYSQL_*` at that container. Read its
host port and root password from the `db` resource in the dashboard. If the
Game started before the login server was ready, restart the `game-server`
resource. AppHost parameters `login-host` (default `127.0.0.1`) and
`login-port` (default `1234`) tell the Game where the login server listens.

**Path B:**

```bash
dotnet build
dotnet run --project AAEmu.Game
```

Prefer visible consoles + tee to `.server_files/logs/` when helping a human
watch progress. Detach Windows GUIs so agent shells do not kill them.

### Step 5 — Launcher and first login

1. Start `.client_files/launcher/AAEmu.Launcher/AAEmu.Launcher.exe` (detached).
2. Path to Game → `.../bin32/archeage.exe`; server IP → `127.0.0.1`.
3. Account: `AAEMU_LOGIN_AUTO_ACCOUNT=true` creates the account on first login.

### Step 6 — Verify “ready to play”

- [ ] Login port **1237** listening  
- [ ] Game ports **1239** and **1250** listening  
- [ ] Login log contains `game server registered`  
- [ ] Client server list is **not** stuck on Maintenance  

If Maintenance: almost always game failed to register (often **port 1234**
taken, or `SecretKey` differs from `AAEMU_LOGIN_SECRET_KEY`). Remap
`AAEMU_LOGIN_INTERNAL_LISTEN` and the Game `LoginNetwork.Port` together. See
REFERENCE.

## Agent behavior

- Explain what you are doing in short human-facing steps; pause for HitL
  downloads and software installs.
- Prefer inventory script over guessing file presence.
- Never commit `.client_files/`, `Config.Local.json`, `*.sqlite3`, `.server_files/`.
- Do not re-download multi‑GB assets when checks already pass.
- Path A and Path B use **different** databases by default — characters do not
  carry over unless the human migrates data.

More detail: [REFERENCE.md](REFERENCE.md).
