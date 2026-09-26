# Instance PvP restrictions, issue #526

The shared combat guard now consumes `WorldInstance.AllowPvP`.
A false value blocks attacks between different player owners before faction and conflict-zone rules.
The guard also covers delayed HP damage, damage effects, hostile buffs, mana burns, and cast interruption.
NPC combat, self effects, friendly buffs, and the active duel pair retain their current rules.
Pets retain the stable character owner ID after logout or travel, including delayed effects.
Owned vehicles use the summoner's character ID before a child vehicle's parent database ID.
Fixed vehicle doodads use their parent vehicle owner. Player-owned doodads retain protection when their owner is offline.

## Evidence

The source base is `ca6d15ad4ead116eecc2c479b7849a8b4f9fddf4`.
The client is r208022, as recorded in `client/history.txt`.
The source `x2game.dll` SHA-256 is `3821c34366ff8b6f7aed95ba53d12216a5c31cb7c3407703d28eea639a621205`.
The runtime dump SHA-256 is `a1995455440cdeb6356682e801f322cd946f3de3ad6595fae818d7a139100ae0`.
This is the same dump and relocation identity as the earlier issue #284 research.
The server compact SHA-256 is `636ca9ecfe777bc86542e4b828f930c7861d2b4639b1bcfff670c064fee9c1ac`.

The compact confirms `pvp='f'` for zone groups 49, 62, 64, and 70 through 76.
The earlier source loads that value, but no combat path consumes it.

Native `0x390281c0` installs the current instance prohibition at relation-context offset `0x30`.
It obtains the instance record through `0x397a6cc0` and negates the byte at record offset `0x19`.
`0x39838700` copies that flag into the context.
`0x39837a40` returns that flag only when both units have player owners.
`0x39838880` clears the attack bit with reason 2 when that predicate succeeds.
The same function excludes the active duel relation, reason 7, before this prohibition.
The existing duel predicate is documented in `resurrection-duel-rules-447-448.md`.

The native flag order and owner condition are confirmed.
Matching that instance record byte to the compact PvP field follows the source loader and the instance context.
No packet, client content, SQL, or compact change is needed.
This fix remains downstream until a separate upstream submission receives authorization.

## Automated validation

The Release unit-test build passed.
All 49 `PeaceProtectionTests` passed, including 15 new instance cases.
Tests cover absent zone protection, forced attack, retaliation, Retribution, null factions, owned objects, and child vehicle ownership.
They also cover pet ownership after logout or travel, reused owner object IDs, instance entry and exit, active duel boundaries, PvE, self effects, hostile buffs, mana damage, and cast interruption.
The combined release tests follow integration with the other combat fixes.

## Human validation

These checks remain pending and belong in HUMAN VALIDATION #573.

- [ ] **HV526-1.** Enter a Library instance with an opposing-faction player. Check direct attacks, damage-over-time effects, and hostile buffs.
- [ ] **HV526-2.** Repeat with a battle pet. Check delayed damage after its owner leaves the instance or disconnects. Check that ordinary combat against an NPC still works. With 2 test players, apply pet damage over time before the target enters a no-PvP instance. Then disconnect the pet owner. Check that the protected target takes no further pet damage.
- [ ] **HV526-3.** Complete a duel in a no-PvP instance. Check that only the active pair can fight.
- [ ] **HV526-4.** Leave the instance and check normal PvP eligibility in a suitable conflict zone.
