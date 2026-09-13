# r208022 character creation and account slots

This change resolves aaemu-cluster issues #436 and #437. The server selects the
starting level, skillsets, and body items. It checks the requested appearance
before it allocates character IDs or starter items. Account creation uses a
maximum of 6 non-deleted characters.

## Client and compact evidence

The client history reports `version 208022`. The native dumps predate this task.
This task did not capture a new client process.

| Artifact | SHA-256 |
| --- | --- |
| Original `client/bin32/x2game.dll` | `3821c34366ff8b6f7aed95ba53d12216a5c31cb7c3407703d28eea639a621205` |
| `.tools-re/dumps/x2game.dumped.dll` | `a1995455440cdeb6356682e801f322cd946f3de3ad6595fae818d7a139100ae0` |
| `compact/server.sqlite3` | `636ca9ecfe777bc86542e4b828f930c7861d2b4639b1bcfff670c064fee9c1ac` |
| `compact/client.sqlite3` | `4f1ac86b2ae79fd35886d0cd7b1e5cccc3287a011a200667d97eb1c5bc4d79a4` |

The original and dumped x2game PE headers agree. The timestamp is `543cb835`,
image base is `38ff0000`, entry point is `008c5d6d`, and image size is `01cb0a00`.
Native addresses below use this image base. Neither compact changed.

## Creation request

`CSCreateCharacter`, opcode `0x22`, level 1, has this body. Numeric fields use
little-endian byte order. The request must end after `introZoneId`.

| Order | Wire field | Server use |
| --- | --- | --- |
| 1 | Length-prefixed UTF-8 name, maximum 128 encoded bytes | Normal name rules |
| 2 | Race byte, gender byte | Exact creatable template |
| 3 | 7 unsigned 32-bit body item IDs | Consume and ignore |
| 4 | Customization type byte | Values 0 through 3 |
| 5 | Hair color ID, unsigned 32-bit, when type is at least 1 | Model and asset check |
| 6 | Skin color ID and legacy `ModelId`, 2 unsigned 32-bit fields, when type is at least 2 | Model-matched skin and zero legacy field |
| 7 | Face fields, when type is 3 | Table, length, and numeric checks |
| 8 | 3 ability bytes | First ability only |
| 9 | Level byte | Consume and ignore, start at level 1 |
| 10 | Signed 32-bit `introZoneId` | Consume and ignore, use template spawn |

The normal producer is `391a4390`. It sets opcode `0x22` at `391a45d5` and
uses constructor `397ae280`. Its vtable is `399b5868`, with serializer
`397c4bf0` at vtable offset `+8`. The serializer writes all 7 body items and
calls customization serializer `3961f710`. The final signed 32-bit field at
object `+1ac` uses the `introZoneId` label at `39a139a4`. The normal producer
sets it to `-1`. The previous server reader left these final 4 bytes unread.

The face body contains these fields:

1. Read the movable decal ID, weight, scale, rotation, and 2 signed 16-bit positions.
2. Read 4 fixed decal ID and weight pairs.
3. Read diffuse, normal, and eyelash map IDs, then the normal weight.
4. Read 5 unsigned 32-bit packed colors.
5. Read the 16-bit modifier length, then exactly 128 modifier bytes.

The face body has 218 bytes. Type 3 adds 13 bytes before this body.
Serializer `3961f710` calls movable-decal helper `3961f680`. At `3961f8b9`,
it supplies `0x80` as the modifier size. The bounded creation reader checks
this length before it allocates the array. Other customization readers retain
their current contracts.

The legacy `ModelId` property does not identify `characters.model_id` on this
wire. Initializer `390b5120` sets custom object `+0c` to zero. Normal preset
copy `39354b80` changes hair at `+4`, skin at `+8`, and face at `+10`, but leaves
`+0c` unchanged. Creation accepts this confirmed zero value. This change makes
no claim about the purpose of a nonzero value in another client path.

## Appearance and starting state

X2UI `loginstage/common.alb`, source line 237, defines 6 starting choices:
Fight 1, Death 5, Wild 6, Magic 7, Vocation 8, and Love 10. The server also needs
the corresponding ability pack. Ability2 and Ability3 always use None 11.
Every new character starts at level 1.

The server reads `characters.creatable`. It rejects invalid race or gender
bytes before the byte-sized template key can wrap. The 8 creatable pairs use
models 10, 11, 16, 17, 18, 19, 20, and 21. Rows with `creatable=f` cannot create
characters.

The body defaults use the first allowed `item_body_parts` row in each model
slot, ordered by row ID. The query excludes NPC and beauty-shop-only items.
Hair color identifies the hair asset. The server selects the corresponding
allowed item in body slot 24. All 72 login presets select their expected hair
item through this relation.

Hair, skin, face map, and decal IDs must belong to the selected model. NPC-only
rows cannot supply creation values. A fixed decal cannot select a movable
decal, or the reverse. Optional face maps and decals can use zero. Colors are
packed RGBA values, so each unsigned 32-bit value is valid. Decal positions use
the full signed 16-bit coordinate representation.

Weights must be finite and within 0 through 1. X2UI `customizing/cosmetic.alb`,
source lines 553 through 556, maps its scale slider to `0.3 + 0.02 * value`.
`cosmetic_view.alb` sets that slider to 0 through 85. The accepted scale is
0.3 through 2 when a movable decal exists. An absent decal can also use zero.
Rotation uses 0 through 360 degrees. NaN and infinite floats are rejected.

Face modifiers are signed per-target weights. They are not preset row IDs.
Native `GetFaceTargetMinValue` and `GetFaceTargetMaxValue` bind at `3905e097`
and `3905e0f3`. Their helpers at `39354a60` and `39354aa0` use the face controller
limits, with fallback values of -100 and 100.

The 8 model files under `game/objects/characters/*/*/face/*_targets.xml` define
specific target limits. The derived numeric data is in
[`r208022-face-slider-limits.json`](../../AAEmu.Game/Models/Game/Char/Data/r208022-face-slider-limits.json).
Each entry records its source path and SHA-256. The extracted XML files stay
in the ignored research directory.

Some valid presets exceed their interactive slider limits. Nuian male login
preset 303, for example, sets target 60 to 100 while its slider maximum is 60.
The server combines each model's slider range with values from its
`custom_face_presets` and login `total_character_customs` rows. This permits
valid preset blends and rejects values outside that combined range. Unused
targets stay zero unless a valid preset supplies a value. The read-only audit
checked all 288 face presets and all 72 login presets. All 72 login presets
pass the complete appearance policy, including floats, colors, and coordinates.

## Account slots and failure packet

The slot limit is 6. `CharacterCreationSlots.Create` acquires a MySQL advisory
lock scoped to the database and account. It counts rows with `deleted=0` before
it calls the creation callback. The callback holds the lock through the normal
character and starter-item save. Different accounts use different locks.
Separate Game processes use the same lock for the same database and account.
The lock wait limit is 10 seconds.

A pending deletion still consumes a slot, including after its scheduled delete
time. Only completed deletion with `deleted=1` releases the slot. A failed
callback or exception releases the advisory lock. The change needs no schema
update.

The native world-server slot failure is reason **9**. `SCCharacterCreationFailed`,
opcode `0x3d`, level 1, contains one unsigned byte. Factory `391a8f50` sets vtable
`399b31d4`, and serializer `397b3060` handles the byte at object `+c`.
Diagnostic helper `3961bbc0` maps reason 9 to the world-server character limit.
Its string is at `399f9ab4`. The regional handler at `391d3330` can show a generic
failure message for this reason. No special client text is claimed.

Issue #437 described `SCGetSlotCount` as the used slot count. The native client
shows that it is the **expanded slot count**, which it adds to the Login limit.
The correct body remains zero because Login already advertises 6 default slots.
A body of 6 would incorrectly advertise 12 slots.

`SCGetSlotCount`, opcode `0x1e2`, level 1, contains one unsigned byte. Factory
`391b6920` uses vtable `399b41f8` and serializer `397b0040`. Handler `391e1740`
stores the byte at Login model `+15be8`. Lua `GetExpandedCharacterCount` at
`394cd0b0` returns this field. `GetCurrentTotalCharactersLimit` at `394ccef0`
adds it to the default byte at Login model `+80`. `GetTotalCharactersDefaultLimit`
at `394ccf90` returns the default. The named server constant documents this
confirmed meaning.

## Tests and client checks

`CharacterCreationRulesTests` checks appearance IDs, model limits, invalid
floats, preset blends, native lengths, all truncation points, trailing data,
ignored client state, and both one-byte response bodies. Manager tests check
invalid race, gender, creatable flags, and starting abilities before account,
ID, or item actions.

`CharacterCreationSlotPersistenceTests` uses the disposable MySQL fixture.
It checks the seventh creation, pending deletion, completed deletion, concurrent
requests for the final slot, separate accounts, and lock release after failures.
The release record contains the final build and test commands and results.

Human client validation remains open. Use the published release for these checks:

1. Create characters with all 4 races and both genders.
2. Change hair, skin, face presets, face sliders, decals, and colors before creation.
3. Check the selected appearance in the lobby and after world entry.
4. Check level 1, one starting skillset, and the correct starter items.
5. Create 6 characters and check that a seventh creation fails.
6. Request deletion and check that the slot stays occupied until deletion completes.
7. Reconnect and check that the lobby still shows a total limit of 6.

No human client validation is claimed by this change.

## Compact loader correction (2026-09-13)

The first account access rollout failed during `CharacterCreationRules.Load`.
`item_body_parts` row 465 has a NULL `item_id`, model 20, asset 14436, and slot 24.
Row 428 supplies item 25263 for the same model and asset. Both rows pass the player and beauty-shop filters.
The loader now excludes NULL item mappings and keeps the first usable mapping in ID order.
Both recorded compact snapshots contain these rows. Neither snapshot changes.

The exact-row regression reproduces the NULL exception before the correction.
It checks successful loading and item 25263 after the correction.
An explicit test loads every creation table from the complete read-only compact.
Set `AAEMU_CHARACTER_CREATION_TEST_COMPACT` to the snapshot path and select
`CharacterCreationRulesTests` to run this check.
The complete loader audit found no other NULL numeric reads. All 360 preset modifiers contain 128 bytes.
