# Native launcher API

Issue: `KeganHollern/aaemu-cluster#17`

AAEmu.Login hosts the authenticated HTTP API used by the native Rust/Tauri
launcher. TLS terminates at the deployment ingress; the Login pod continues to
serve plain HTTP only inside the cluster. Do not expose its HTTP listener
directly to the internet.

## Authentication and compatibility

`POST /launcher/v1/sessions` accepts a username and plaintext password over
HTTPS. It uses the same account, ban, and `AutoAccount` rules as the legacy
Trion login. Launcher authentication deliberately suppresses password-verifier
upgrades and creates automatic accounts with the legacy SHA-256 verifier so an
account remains usable by the old launcher during the transition.

The API returns random access and refresh tokens. Only SHA-256 token digests
are stored in MySQL. Access tokens are short lived; refresh tokens rotate on
every use and are revocable. The native launcher stores only the refresh token
in the operating-system credential vault. Passwords and reusable password
hashes must never be stored in launcher settings, logs, command-line arguments,
or environment variables.

The `launcher_sessions` and `launcher_launch_tickets` tables are introduced by
`SQL/updates/2026-08-14_aaemu_login_launcher_sessions.sql` and are also present
in the base Login schema. The same migration replaces the old non-unique
username index with a unique index so concurrent automatic registration cannot
create two identities for one name. Audit existing usernames for duplicates
before applying it. Production keeps automatic database updates disabled, so
this migration is an operator-controlled prerequisite for enabling the API.

## Endpoints

All routes use the `/launcher/v1` prefix.

| Method and path | Authentication | Purpose |
| --- | --- | --- |
| `GET /status` | none | Login and Game availability for the maintenance banner |
| `POST /sessions` | username/password | Create a retained launcher session |
| `POST /sessions/refresh` | rotating refresh token | Rotate and renew a session |
| `DELETE /sessions/current` | bearer access token | Revoke the current session |
| `GET /me` | bearer access token | Return the authenticated account identity |
| `GET /manifest` | bearer access token | Return the client compact version, size, and SHA-256 |
| `GET /assets/client.sqlite3` | bearer access token | Stream the baked client compact with HTTP Range support |
| `POST /launch-tickets` | bearer access token | Mint a short-lived, one-time Trion launch ticket |

The manifest and download refer only to the fixed, verified
`Data/client.sqlite3` baked into the Login image. There is no caller-selected
filesystem path. Startup verifies the file's SQLite header, exact configured
size, and SHA-256 before marking Login ready.

Play tickets are random 64-character hexadecimal values. The native launcher
places one in the existing Trion ticket password field. Login atomically
consumes it before trying the legacy password flow, checks that its launcher
session is still active and unbanned, and then uses the existing Login-to-Game
world-cookie path. The old SHA-256 password flow remains available.

## Failure behavior

Launcher routes fail closed. Missing or expired credentials return 401;
dependency failures and unavailable Game registration return 503. The launcher
must disable both Update and Play and display Maintenance for network errors,
timeouts, or 5xx responses. A definitive refresh-token 401 signs the user out.

Login and launch-ticket routes are rate limited, and compact downloads have a
global concurrency ceiling. Authentication errors are
generic and must not reveal whether an account exists or is banned. Tokens,
passwords, authorization headers, and ticket values must never be logged.

## Configuration

The `LauncherApi` section is disabled by default. A deployment enabling it must
set all of the following from reviewed image metadata:

- `LauncherApi__Enabled=true`
- `LauncherApi__ClientCompactPath=Data/client.sqlite3`
- `LauncherApi__ExpectedClientCompactSha256=<64 lowercase hex characters>`
- `LauncherApi__ExpectedClientCompactSize=<exact byte count>`

Token and ticket lifetimes have conservative defaults and can be overridden
through the remaining `LauncherApi` options.

The native launcher API and Korea challenge/second-factor authentication are
mutually exclusive. Login refuses startup when both are enabled; the v1 native
launcher intentionally supports the deployed EU/Trion authentication path only.
