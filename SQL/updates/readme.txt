When a code change needs a MySQL schema change, add an update script here. Apply the same change to the initial SQL file in the parent folder.

Use this file name format:

YYYY-MM-DD_aaemu_XXXX*.sql

Use "login" or "game" for "XXXX". Use lowercase file names. The server uses the complete file name as the update version in its `updates` table.

Do not rename or edit a released update script. A rename creates a new update version. An edit does not run again after the original version has `installed=1`. Add a new update script for a correction.

At startup, each server selects its update files and sorts them by file name. The server does not run a file whose ledger row has `installed=1`.

Set `Connections:AutoApplyUpdates` to `true` for an unattended server. The environment variable form is `Connections__AutoApplyUpdates=true`. The server applies each pending file in order and stops at the first failure.

When `AutoApplyUpdates` is `false`, an interactive server asks for `YES` or `SKIP`. An unattended server fails startup instead of reading standard input.

Each attempt updates the ledger with a UTC attempt time. A successful update uses `installed=1`. A failed update uses `installed=0` and stores the error in `last_error`. The server retries that file at the next startup. It does not attempt later files after a failure.

When a server creates the `updates` table for the first time, it marks all update files in that release as installed. This preserves databases that existed before the update system.

Keep the date prefix because file name order is update order.
