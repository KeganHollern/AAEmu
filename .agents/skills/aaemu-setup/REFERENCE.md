# AAEmu setup — reference

## Downloads and HitL

Wiki sources of truth:

- `Docs/wiki/Dependencies-and-Downloads.md`
- `Docs/wiki/Client.md`

| Asset | Approx size | Source | Automation |
| --- | --- | --- | --- |
| Client 1.2 `r208022` | multi‑GB (archive ~8GB+) | MEGA / Google Drive (wiki) | **HitL only** — inventory + user download |
| Compact / `compact.sqlite3` | tens of MB archive | MEGA (wiki) | **HitL only** — then copy into `AAEmu.Game/Data/` |
| AAEmu Launcher | ~few MB | GitHub releases | Optional script fetch if missing |

### Inventory scripts (PowerShell + Bash)

Same behavior on both shells: inventory only by default; never pull MEGA multi‑GB
packages. Exit `0` when all OK; exit `1` when anything MISSING/PARTIAL.

| Action | PowerShell | Bash |
| --- | --- | --- |
| Report only | `powershell -File .agents/skills/aaemu-setup/scripts/Test-AaemuAssets.ps1` | `bash .agents/skills/aaemu-setup/scripts/test-aaemu-assets.sh` |
| Fetch launcher if missing | add `-FetchLauncherIfMissing` | add `--fetch-launcher` |
| Open missing download pages | add `-OpenMissingDownloadPages` | add `--open-missing` |
| Custom repo root | `-RepoRoot 'D:\path\AAEmu'` | `--repo-root /path/AAEmu` |

```powershell
# Windows PowerShell — from repo root
powershell -File .agents/skills/aaemu-setup/scripts/Test-AaemuAssets.ps1
powershell -File .agents/skills/aaemu-setup/scripts/Test-AaemuAssets.ps1 -FetchLauncherIfMissing
powershell -File .agents/skills/aaemu-setup/scripts/Test-AaemuAssets.ps1 -OpenMissingDownloadPages
```

```bash
# Linux / macOS / WSL / Git Bash — from repo root
bash .agents/skills/aaemu-setup/scripts/test-aaemu-assets.sh
bash .agents/skills/aaemu-setup/scripts/test-aaemu-assets.sh --fetch-launcher
bash .agents/skills/aaemu-setup/scripts/test-aaemu-assets.sh --open-missing
```

Optional: `chmod +x .agents/skills/aaemu-setup/scripts/test-aaemu-assets.sh` then run as `./.agents/skills/aaemu-setup/scripts/test-aaemu-assets.sh`.

**Skip rules (do not re-download):**

- Extracted client: `game_pak` exists and size is large (script default ≥ 1 GB) **and** `bin32/archeage.exe` exists  
- Compact: `AAEmu.Game/Data/compact.sqlite3` exists and size ≥ 1 MB  
- Launcher: `AAEmu.Launcher.exe` under `.client_files/launcher/`  
- Archives under `.client_files/*.7z` count as “archive present” but still need extract if runtime files are missing  

**Platform note:** Playing the official client requires Windows (or Wine, unsupported here). Linux hosts commonly run **Path A/B servers only**; bash still validates `game_pak` + `compact.sqlite3` for the game process.

### Expected paths after extract

```text
.client_files/ArcheAge 1.2 (r208022) for AAEmu/game_pak
.client_files/ArcheAge 1.2 (r208022) for AAEmu/bin32/archeage.exe
.client_files/launcher/AAEmu.Launcher/AAEmu.Launcher.exe
AAEmu.Game/Data/compact.sqlite3
```

Nested extract folders: move contents up so the paths above resolve.

### Launcher settings (`settings.aelcf`)

- `pathToGame` → absolute `archeage.exe`
- `serverIPAddress` → `127.0.0.1`
- `loginType` → `trino_1_2`

## Ports

| Port | Role |
| --- | --- |
| 1237 | Go login server **public** (client), `AAEMU_LOGIN_CLIENT_LISTEN` |
| 1234 | Go login server **internal** (game registration), `AAEMU_LOGIN_INTERNAL_LISTEN` default |
| 1235 | Suggested alternate internal if 1234 busy |
| 8080 | Go login server launcher API and health, `AAEMU_LOGIN_HTTP_LISTEN` |
| 9090 | Go login server Prometheus metrics, `AAEMU_LOGIN_METRICS_LISTEN` |
| 1239 | Game public |
| 1250 | Game stream |
| 1280 | Game Web API (optional) |
| 3306 | Host MySQL (Path B) |
| 15133 | Aspire dashboard (Path A, http profile) |

Server list for the client comes from the login server **`AAEMU_LOGIN_GAME_SERVERS`** JSON, not MySQL `game_servers` rows.

## Config.Local templates

### Go login server environment (Path B, optional port remap)

The login server is `server/cmd/login` in the `KeganHollern/aaemu-cluster`
repo. It has no JSON config. Set environment variables, then run
`go run ./cmd/login` from `aaemu-cluster/server`:

```bash
AAEMU_LOGIN_SECRET_KEY=test
AAEMU_LOGIN_AUTO_ACCOUNT=true
AAEMU_LOGIN_INTERNAL_LISTEN=0.0.0.0:1235   # only if 1234 is busy
AAEMU_LOGIN_MYSQL_HOST=127.0.0.1
AAEMU_LOGIN_MYSQL_PORT=3306
AAEMU_LOGIN_MYSQL_USER=root
AAEMU_LOGIN_MYSQL_PASSWORD=YOUR_HOST_MYSQL_PASSWORD
AAEMU_LOGIN_MYSQL_DATABASE=aaemu_login
AAEMU_LOGIN_GAME_SERVERS='[{"id":1,"name":"AAEmu.Game","host":"127.0.0.1","port":1239,"hidden":false}]'
```

Full variable table: `aaemu-cluster/server/README.md`, section Configuration.

### Game (`AAEmu.Game/Config.Local.json`)

Path A often only needs `ClientData`. Path B needs DB + LoginNetwork + ClientData.

```json
{
  "SecretKey": "test",
  "LoginNetwork": {
    "Host": "127.0.0.1",
    "Port": 1235
  },
  "Connections": {
    "MySQLProvider": {
      "Host": "127.0.0.1",
      "Port": "3306",
      "User": "root",
      "Password": "YOUR_HOST_MYSQL_PASSWORD",
      "Database": "aaemu_game"
    }
  },
  "ClientData": {
    "Sources": [
      "REPO_ROOT\\.client_files\\ArcheAge 1.2 (r208022) for AAEmu\\game_pak",
      "REPO_ROOT\\.client_files\\ArcheAge 1.2 (r208022) for AAEmu"
    ]
  }
}
```

Replace `REPO_ROOT` with the absolute repo path. Keep the Game `SecretKey` equal to `AAEMU_LOGIN_SECRET_KEY`. Keep `LoginNetwork.Port` equal to the `AAEMU_LOGIN_INTERNAL_LISTEN` port (`1234` default, `1235` in the remap example above).

Game load order: `Config.json` → `Configurations/*.json` → **`Config.Local.json`**.

`Config.Local.json` is copied to `bin` on build when present in the project folder.

## Path A — Aspire

- `dotnet run --project AAEmu.Aspire.AppHost --launch-profile http`
- MySQL container + volume managed by Aspire (password in user secrets)
- Aspire starts only the Game. The Go login server runs outside Aspire on the host. Neither one is a Docker app container
- AppHost parameters `login-host` (default `127.0.0.1`) and `login-port` (default `1234`) set the Game `LoginNetwork`
- Dashboard token is printed at startup

## Path B — Host MySQL

```text
[ ] MySQL 8 host service up
[ ] aaemu_game created and SQL imported. aaemu_login created empty (Go login server creates the tables)
[ ] AAEMU_LOGIN_* environment for the Go login server. Config.Local on Game
[ ] Game SecretKey equals AAEMU_LOGIN_SECRET_KEY. Internal ports match and free
[ ] compact.sqlite3 + game_pak OK (inventory script)
[ ] Go login server, then Game
[ ] Log: game server registered
```

```bash
mysql -u root -p -e "CREATE DATABASE IF NOT EXISTS aaemu_login; CREATE DATABASE IF NOT EXISTS aaemu_game;"
mysql -u root -p aaemu_game  < SQL/aaemu_game.sql
```

Do not import a login SQL file. The Go login server creates the `aaemu_login`
tables at first start when the `users` table is missing.

## Process / log hygiene

**Windows (PowerShell agents):**

- Detach launcher/server windows (`Win32_Process.Create`) so agent Job Objects do not kill them.
- Tee logs: `go run ./cmd/login 2>&1 | Tee-Object -FilePath .server_files/logs/login.log` (from `aaemu-cluster/server`, with the `AAEMU_LOGIN_*` variables set)

**Linux / macOS (bash agents):**

- Run the Go login server and Game in separate terminals or `tmux`/`screen`, or:

```bash
mkdir -p .server_files/logs
# In aaemu-cluster/server, with the AAEMU_LOGIN_* variables exported:
go run ./cmd/login 2>&1 | tee REPO_ROOT/.server_files/logs/login.log
# In this repo:
dotnet run --project AAEmu.Game  2>&1 | tee .server_files/logs/game.log
```

- Launcher/client play remains a Windows-side step if the human has no Windows client host.

## Troubleshooting

| Symptom | Likely cause |
| --- | --- |
| **Maintenance** on server list | Game not registered (port 1234 conflict, bad LoginNetwork, Game SecretKey differs from AAEMU_LOGIN_SECRET_KEY) |
| Aspire dies at start | Docker/Podman not running |
| Missing data / sqlite errors | No `compact.sqlite3` |
| Bad client data | Wrong `ClientData.Sources` |
| Lost characters after path switch | Path A and Path B databases are separate |
| Multi‑GB download again | Agent skipped inventory — always run `Test-AaemuAssets.ps1` or `test-aaemu-assets.sh` first |

Success line (login server JSON log):

```text
"msg":"game server registered"
```

## Not supported as “the” path

- Docker MySQL + host apps labeled as non-Docker setup  
- Aspire without an OCI runtime  
- Committing client/launcher/sqlite into git  
