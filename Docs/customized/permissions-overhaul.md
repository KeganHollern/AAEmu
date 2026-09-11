# Account permissions

## Scope

This change replaces numeric access levels with 3 account roles.
It resolves cluster issues 44, 433, 434, 435, 441, and the authentication boundary in 427.
Bot trials, mail spam reports, and the broader game security audit remain separate work.

| Role | Tools |
| --- | --- |
| Normal Player | Normal gameplay and read-only player commands |
| Moderator | Player support, diagnostics, movement tools, item and currency support, bans, mutes, and kicks |
| Admin | All Moderator tools, role changes, server control, raw packets, and permanent world or global changes |

Only an Admin can discipline a staff account.
Both staff roles can use support tools on normal players, including flight.
This policy replaces the older Admin-only wording in issues 433 and 434.

## Admin-only tools

Aliases use the same rule as their canonical command.
Both staff roles keep all other enabled staff tools.

| Command or action | Reason |
| --- | --- |
| `role` | Changes account authority |
| `scripts`, `reloadconfig`, `reloadauction` | Controls the server, save cycle, or shared data |
| `world`, `time`, `godmode`, `settradepackmaildelay`, `snow` | Changes shared world settings |
| `moveall` | Moves every online character |
| `packet` | Sends raw packets or reads a server file |
| `nwrite`, `despawnall` | Writes world spawn files or removes all world spawns |
| `npc save`, `doodad save`, `slave save`, `doodad remove` | Saves world definitions or permits whole-world removal |
| `feature set` | Changes shared feature flags |
| `zonestate` with arguments | Changes a shared conflict state |
| `sphere add`, `sphere remove` | Changes stored quest spheres |
| `shipbarrier reset`, `waterdebug reload`, `waterdebug reloadinfo` | Rebuilds shared world data |
| `testai load_path`, `testai clear_path_cache`, `testai clear_cache` | Reads a server path file or clears shared path data |
| `ics on`, `ics off`, `ics reload` | Changes the shared cash shop |
| `towerdef start`, `towerdef next`, `towerdef end` | Controls shared events |
| `moveto save`, `moveto go`, `moveto back` | Writes or reads server path files |
| `testhouse forcedemo`, `testhouse setdemosoon` | Forces house demolition |

The `testtransfer` command stays disabled for all roles.
Player support includes temporary spawns, house repair, inventory support, currency support, and movement of a selected player.
These support actions remain available to both staff roles.
Role permissions do not replace operation-specific approval for direct SQL, compact changes, or other persistent-data work.

## Authority

The Game account owns one role in `aaemu_game.accounts.role`.
Characters do not own or raise that role.
The server does not grant staff roles through account or character creation order.
Account ID 0 cannot own staff rights or create Game account state.
Each privilege check reads the current account role.
Invalid roles and failed role reads grant no staff rights.

An Admin uses `/role <account-id> NormalPlayer`, `/role <account-id> Moderator`, or `/role <account-id> Admin`.
The role setter locks both account rows and checks the actor's Admin role again before the update.
The next command or privilege check uses the changed role without a new login.

For a new database, the first account remains Normal Player.
After that account completes its first Game login, the operator checks its positive Game account ID.
The operator can grant the first Admin through the existing authorized SQL connection:

```sql
UPDATE aaemu_game.accounts SET role = 2
WHERE account_id = <confirmed_account_id> AND account_id > 0;
SELECT account_id, role FROM aaemu_game.accounts
WHERE account_id = <confirmed_account_id>;
```

This operation does not create an account, credential, or cluster access path.

Each registered command declares its permission.
The same declaration controls command execution, aliases, subcommands, and help.
An undeclared command cannot run.
Admin-only child commands remain restricted under a shared staff command.
Client flags and a target character cannot supply caller authority.

The hidden `/aaemu_shop` transport remains part of normal Character Info gameplay for all 3 roles.
It retains the server catalog, price, balance, bag-space, and transaction checks.
It does not grant items or currency as a staff command.
The public helper list contains only `help`, `online`, and `position`.

## Moderation and audit

Login owns ban and mute state, expiry, reasons, and moderation history.
The authenticated private Game link carries staff requests and current moderation state.
A ban stops login and closes an online Game session.
A mute blocks chat until expiry or removal.
A kick saves the character and closes the socket.

Authorization occurs immediately before Game sends a moderation request.
An authorized request can complete after a later role change.
Login serializes moderation actions on the target account row and keeps an immutable action history.
Game reads the current restriction before entry and after its private Login link reconnects.
Old state cannot replace a newer restriction.

The command audit records the actor, source address, target context, arguments, and result.
It writes one structured event and one database row for each command.
The row starts before the command, and the final result updates that same row.
If the audit cannot start, the command does not run.
An unavailable moderation result records `unconfirmed`, because Login can commit before its reply is lost.
If audit completion fails, the original `started` row remains and the log records `audit-finish-failed`.
Scheduled game work records acceptance of the schedule, not the result of later gameplay.
Client console reports remain reports, not authorization or evidence of command execution.

The loopback Web API command route returns HTTP 403 and records the transport peer address.
It cannot use a supplied character name as staff authority.
Other loopback operator API routes remain outside the account command interface.

## Migration and release

The prior selected Game source is `6c5740ce85a1bc8e347ec07f6a1b10929949e9d4`.
The cluster base is `ebb91ba5487473d65e49fb4aba0d22f44df8629c`.
The user authorized the permission SQL changes and the normal release path.

The preflight found valid Login and Game accounts 2, 3, and 4 at access level 100.
Their new role is Admin.
Account 0 has no Login user or character. Its new role is Normal Player.
No character has more access than its account.
The migration must reject unreviewed character-only elevations before it removes the old columns.

Record SQL hashes, tests, source review, image checks, publication, and live checks before completion.
Keep Keel active throughout the release.
Do not create a new cluster access path.

This is a downstream permission policy for the r208022 deployment and paired Go Login server.
The upstream comparison at `00ed43a0` contains no newer role or command-manager change for reuse.
The selected deployment keeps its prior upstream base and does not import unrelated upstream changes.

### SQL review

| Update | SHA-256 | Application |
| --- | --- | --- |
| `2026-09-10_aaemu_game_account_roles.sql` | `35f6d1cd443d196d755df94db6e043087b370b6ab08c8378d49dd8442efe5aa9` | Game startup updater, after the old Game writer stops |
| `2026-09-11_aaemu_game_command_audit.sql` | `3fc71c465009cb5b72b092ccdfe99956e162048022e028e2d50e314a3343247d` | Game startup updater |

The role update maps levels below 50 to Normal Player, 50 through 99 to Moderator, and 100 or more to Admin.
Account 0 always maps to Normal Player.
The update removes only the obsolete permission columns and preserves all account balances and characters.
The command audit update creates a new table without changing other tables.
Neither compact snapshot changes.

The protected local backup at `2026-09-11T02:44:24Z` includes all schemas, routines, events, and triggers.
Its compressed size is 319022 bytes and its SHA-256 is `1976a36caf7bd3112ed0469bd53723c7e6ce630d1134a354f4d30facd335944b`.
The backup stays outside Git. The compression and dump completion checks passed. A restore test did not run.

The paired cluster change adds the Login moderation schema before the new website starts.
It grants the current website database user SELECT access only to the new `users.ban_until` column.
Game and Login use the same new private protocol, so entry can pause until both new images are ready.

### Local checks before the source PR

- The complete solution build passed.
- All 2613 Game unit tests passed.
- All 110 GameMySql tests passed against disposable local MySQL 8.0.36 schemas.
- Runtime script compilation passed with 0 errors and 0 warnings.
- All 48 command metadata tests and all 6 delayed world-command audit tests passed.
- Independent reviews checked account authority, command paths, audit outcomes, private protocol, and SQL.
- The reviews corrected uncertain role commit results and a Login registration lock that delayed moderation.

Gameplay still needs focused checks after the published release reaches the cluster.
