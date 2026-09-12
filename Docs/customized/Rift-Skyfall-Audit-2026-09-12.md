# Rift skyfall and ownership audit (2026-09-12)

## Scope and evidence

Follow-up to the in-game report: the Ynystere portal appears and the boots notice
fires, but enemies / falling impact effects are missing. Audited against
deployment base `c73605275cb8683213ce331897c7540687e161a3` and the read-only server
compact SHA-256 `636ca9ecfe777bc86542e4b828f930c7861d2b4639b1bcfff670c064fee9c1ac`.
No compact, MySQL, client package, automatic-schedule, or Keel settings change.

Upstream rift PRs 1448 and 1052 were checked: both are closed; 1448 is unmerged
and proposes a different Crimson-specific runtime, not this correction. This
change stays downstream with the fork's shared tower-defense ownership model.

## Findings and corrections

- The August height fix grounded **both** ends of the Crimson falling projectile.
  The zero-height-parameter launch now stays at controller altitude. Only the
  observed `500000` impact marker resolves to terrain. Other large positive and
  negative offsets are no longer mistaken for impacts. The general meaning of
  the compact field remains unconfirmed; this is not a claim that all plot
  height parameters are vertical search ranges.
- Fourteen later-wave Crimson controller placements were on the ground. Each
  now uses that site's existing first-wave controller altitude, without changing
  X/Y, the site anchor, objectives, or enemy landing positions.
- A missing terrain sample formerly fell back to `anchor + 500m`. Impact nodes
  now reject that case with an explicit geodata/heightmap error instead of
  creating invisible airborne soldiers. World terrain availability and actual
  client particle rendering still require the deployed in-game check.
- Pending summon plots could outlive event cleanup. Placement tokens now share
  a cancellable lifetime with their descendants. Cancellation is serialized
  against ownership registration, cleanup cancels before taking its removal
  snapshot, and late NPC registrations are rejected/despawned. Captured plot
  state notices ownership removal even if another plot replaced ActivePlotState.
  Deferred labor-batch summons recheck cancellation. Owned NPCs have no respawn
  delay from their initial publication onward.
- Normal NPC deletion also unregisters ownership. Previously, corpse deletion
  could leave a recycled object ID in the event registry until the entire event
  ended, so a later spawn could collide with stale ownership.
- Step cleanup prefixes now include their delimiter: target 1 cannot match
  target 10. Existing `despawn_on_next_step=false` behavior remains intentional;
  lingering older-wave enemies do not count toward the new generation.
- `/tower_def list` now includes alive owned NPC counts by template and active
  plot count. This distinguishes an active controller from actual soldiers.
- Crimson's final compact phase has a 3600-second timer, but its event-level
  kill fields are NPC 1 / count 0. The hard deadline expires before that phase
  can finish. The manifest now declares `completionTarget`: two NPC 8850 captains
  for Cinderstone/Ynystere and one NPC 8952 final boss for Auroria. These match
  the audited final plot outputs. This is an explicit downstream completion
  policy, not a silent compact rewrite. Custom events can use the same optional
  field; omitted fields preserve compact behavior. The override participates in
  restart compatibility hashing and appears in GM diagnostics.

## Data path verified

Crimson Ynystere controller 8052 casts skill 15298 / plot 143. Node 1241 chooses
an aerial point 15m from the controller; 1249 uses that point as projectile
source and resolves the ground impact; 1242 reuses the exact impact target after
800ms and SpawnEffect 963 summons NPC 8834. The sibling branch summons 8826.
Both spawn effects explicitly use the original controller as source, preserving
event ownership and faction. The loop creates 25 of each against objectives of
23. Subsequent infantry waves provide 20/20 against 18/18 and 20/10 against
18/8. Captain arrivals use 1000ms and the Auroria final boss uses 1200ms.

Grimghast does not use the Crimson projectile graph. Its controller OnSpawn
skills invoke NpcSpawnerSpawnEffect with site-scoped authored formations. Each
infantry wave provides 15 of each required template and the final formation
provides one of each boss. All 163 placements remain initially inactive.

Run the repeatable read-only audit from the source checkout:

```text
python Tools/rift_audit.py <path-to-cluster>/compact/server.sqlite3
```

It follows controller OnSpawn skills, bounded plot loops, impact/summon edges,
and site-scoped spawner effects, and rejects unreachable kill objectives. It
audits all six Crimson and both Grimghast combat sites. It deliberately rejects
unsupported conditional graphs rather than assuming they succeed. It is a
static data check, not a replacement for server execution or rendered VFX.

Automated coverage includes the actual launch/impact/reused-target selector
sequence with an injected terrain sample, all Crimson controller elevations,
Grimghast infantry counts, late summon rejection, cross-occurrence isolation,
duplicate ownership rejection, and cancellation of replaced plot states.

## Focused in-game acceptance

1. Start `/tower_def start rift.crimson.ynystere ynystere-1` near
   `(21382, 12611, 223)`. The intro is intentionally 170 seconds; use
   `/tower_def next rift.crimson.ynystere` once to skip it when testing.
2. After the boots notice, watch for aerial launch, downward travel, ground
   explosion, then soldiers. The first arrivals start roughly 2.8 seconds after
   the controller starts; the two branches alternate over about 52 seconds.
3. `/tower_def list` should show NPC 8834 and 8826 counts rising. Kill 23 of each
   normally and verify the next wave starts without a GM advance. Test all later
   waves so a first-wave-only fix cannot pass acceptance. Killing both final
   captains (the final boss in Auroria) must end the event and remove its portals.
4. End the event while projectiles are in flight. No delayed soldiers should
   remain or appear afterward. Restart and ensure old enemies cannot credit the
   new occurrence. Repeat at Ynystere-2, all three Cinderstone sites, and Auroria.
5. Start `/tower_def start rift.grimghast.ynystere grim-ynystere`. Its initial
   timer is intentionally 600 seconds; `next` can skip the intro. Verify both
   15-member formations, natural kill progression, both bosses, and cleanup.
   Repeat with `rift.grimghast.cinderstone grim-cinderstone`.

Do not enable recurring schedules as part of this test release. Their separate
activation/content-classification issue remains open. This audit does not claim
the original plan's full custom Content Studio authoring, soak, restart, and
client acceptance milestones have all been completed.
