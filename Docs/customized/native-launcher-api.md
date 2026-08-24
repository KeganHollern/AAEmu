# Native launcher API

Issues: `KeganHollern/aaemu-cluster#17`, `KeganHollern/aaemu-cluster#70`

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

Session, status, and launch-ticket routes use the `/launcher/v1` prefix.

| Method and path | Authentication | Purpose |
| --- | --- | --- |
| `GET /status` | none | Login and Game availability for the maintenance banner |
| `POST /sessions` | username/password | Create a retained launcher session |
| `POST /sessions/refresh` | rotating refresh token | Rotate and renew a session |
| `DELETE /sessions/current` | bearer access token | Revoke the current session |
| `POST /launch-tickets` | bearer access token | Mint a short-lived, one-time Trion launch ticket |

An enabled launcher API also maps these authenticated raw-byte routes:

| Method and path | Authentication | Purpose |
| --- | --- | --- |
| `GET /launcher/v2/manifest` | bearer access token | Return the pinned canonical signed-manifest bytes unchanged |
| `GET /launcher/v2/manifest.minisig` | bearer access token | Return the pinned detached Minisign bytes unchanged |
| `GET /launcher/v2/assets/<sha256>` | bearer access token | Stream a manifest-listed representation with HTTP Range support |

The v2 provider loads one fixed release directory once during startup. It
bounds and pins the raw manifest and signature, rejects duplicate JSON keys,
builds a lowercase content-hash allowlist from `representation.blob`, verifies
every listed blob's exact size and SHA-256, and rejects missing or extra blob
entries. It retains the exact verified file handles for delivery, so a later
pathname replacement cannot change the bytes served under a pinned ETag. Route
text is never used as a filesystem path.

Production CI verifies the detached Minisign signature before a release can be
selected. Login deliberately does not sign, execute, parse sparse payloads, or
install content; it pins and serves reviewed immutable bytes. The Rust launcher
is the runtime signature and metadata trust consumer.

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

Login and launch-ticket routes are rate limited, and content downloads have a
global concurrency ceiling. Authentication errors are
generic and must not reveal whether an account exists or is banned. Tokens,
passwords, authorization headers, and ticket values must never be logged.

## Configuration

The `LauncherApi` section is disabled by default. A deployment enabling it must
select a reviewed signed release and set all of the following:

- `LauncherApi__Enabled=true`
- `LauncherApi__ContentV2__ReleasePath=Data/client-patches/releases/<sequence>`
- `LauncherApi__ContentV2__ExpectedManifestSha256=<exact lowercase SHA-256>`
- `LauncherApi__ContentV2__ExpectedMinisigSha256=<exact lowercase SHA-256>`

Token and ticket lifetimes have conservative defaults and can be overridden
through the remaining `LauncherApi` options.

Production activation must bake that release directory into the Login image's
read-only root filesystem; a writable content mount is not an accepted release
configuration.

Missing content, a zero/uppercase pin, a reparse/symlinked path component, a
digest mismatch, or an invalid blob catalog fails startup and readiness. The
provider never hot-reloads a release.

The native launcher API and Korea challenge/second-factor authentication are
mutually exclusive. Login refuses startup when both are enabled; native
launcher sessions intentionally support the deployed EU/Trion authentication
path only.
