# Dungeon loading and event lifecycle

Fixes [aaemu-cluster#290](https://github.com/KeganHollern/aaemu-cluster/issues/290).

The dungeon loader now awaits every task returned by the five asynchronous spawn
groups. A spawn failure faults the loader before event registration, readiness,
or queued player movement. Loading completion owns event registration; the
constructor no longer registers events before their doodad dependencies exist.
Readiness and queue draining share the dungeon entry lock.

Dungeon registration and each event's per-world subscription are idempotent.
Partial registration is detached if subscription fails, and teardown removes the
recorded subscriptions even after repeated calls. Doodad-spawn unsubscribe now
removes its handler. Room state remains separate for each world, and room-clear
ticks start after loading; a previously cleared room no longer prevents later
rooms from being checked. A destroyed dungeon cannot be recreated or marked
ready by its loading task.

## Validation

`DungeonLifecycleTests` passed all 23 cases on .NET 10. The tests hold each of
five spawn results last, fail each group while others are still pending, exercise
actual queued player movement, and check room binding before readiness. They
also cover repeated and concurrent subscriptions, single action execution,
partial-registration cleanup, destruction during loading, repeated teardown,
and independent room state across two worlds.

```sh
dotnet run --project AAEmu.UnitTests/AAEmu.UnitTests.csproj -- \
  --treenode-filter '/*/*/DungeonLifecycleTests/*'
```

This is a server-only change. It needs no client files, protocol change, SQL
migration, or compact database update. The existing combat-start and combat-end
Indun handlers remain logging-only placeholders; their subscription lifecycle is
covered, while their unimplemented actions are outside this fix.

## Focused gameplay checks

- Enter a dungeon while its initial objects are loading; confirm entry occurs
  after spawning and that the expected doors, NPCs, and room anchors are present.
- Trigger a configured doodad-spawn or NPC event and confirm its action occurs
  once. Leave a configured room, then verify its no-players action occurs once.
- Repeat entry and teardown, and run two copies of the same dungeon; confirm
  actions do not multiply and room state does not carry between instances.

These gameplay checks still require human validation after publication through
the normal image and Keel delivery path.
