# Mount authority and seat entry in r208022

This change resolves cluster issues #325, #480, and #481. The server checks the
character, mount, skill list, and seat before it changes state or starts a skill.

## Client identity and evidence

The client is ArcheAge r208022, as recorded in `client/history.txt` in the cluster
workspace. The original `bin32/x2game.dll` SHA-256 is
`3821c34366ff8b6f7aed95ba53d12216a5c31cb7c3407703d28eea639a621205`.
The unpacked image SHA-256 is
`a1995455440cdeb6356682e801f322cd946f3de3ad6595fae818d7a139100ae0`.
Native addresses below use image base `0x38ff0000`.

Research files are in the ignored cluster path
`.tools-re/combat-20260913/mounts/`. They contain bounded disassembly extracts,
the pet action bar Lua disassembly, and the model asset manifest. These are
static client and source checks. They are not a packet capture or a gameplay test.

The unchanged server compact SHA-256 is
`636ca9ecfe777bc86542e4b828f930c7861d2b4639b1bcfff670c064fee9c1ac`.
The unchanged client compact SHA-256 is
`4f1ac86b2ae79fd35886d0cd7b1e5cccc3287a011a200667d97eb1c5bc4d79a4`.

## Native contracts

| Path | Native evidence | Server behavior |
| --- | --- | --- |
| Mount caster | `397c86f9` serializes caster kind 3, the object ID, then `mountSkillType` as a 32-bit field at `397c8730`. | The wire mount skill ID must resolve to the requested base skill. An arbitrary base skill or another mount row cannot grant access. |
| Pet skill list | `3933b9d0` checks the selected mate skill collection and required level. `3933ba62` through `3933ba84` checks the rider seat. | The active mate must own the mount skill ID. The mate level must meet `skills.ability_level`. |
| Pet commands without a rider | `3933ba7d` accepts seat 0 or 1 when no attached skill row exists. | The owner can command an unmounted battle pet. A passenger cannot use that owner command. |
| Rider skill | The client reads `mount_attached_skills`. `3920b7c9` through `3920b88c` selects a rider row by the current attachment point. | The server selects the rider skill from the exact mount row and the actual occupied seat. |
| Vehicle skill list | `39347130` selects the active replacement buff at unit offset `5fe4`. With no replacement, it selects the slave template list. `393471df` checks the seat. | The operator must occupy that vehicle seat. Skills without rider rows need the driver seat. |
| Replacement buff | The table loader at `395b1480` reads `buff_mount_skills`. `39389a29` sets the replacement ID on buff start. `39389c1e` clears it on buff end. | A grant buff replaces the vehicle list. The latest grant start selects the list. The end of any grant buff clears it, including overlap. The server also checks that the selected buff is active. |
| Pet entry | `393642f0` selects seat 1 for the owner and seat 2 for another player. `3909b070` sends opcode `0xa7`. At `3909b11f`, squared XYZ distance must be less than 9. | Pet entry needs a living character, a living mate, an authored seat, and distance less than 3 meters in the same instance. |
| Pet approach | `3935aa10` uses squared distance 625 before the movement controller starts. | The 25-meter approach radius does not grant entry. |
| Vehicle entry | `393464a0` sends opcode `0x31` after `39346290`. That check calls `39093860` with squared range 16. | Vehicle entry needs a living character and mountable vehicle in the same instance. The entry point must be less than 4 meters away. |
| Pet exit | `39361e80` uses the local character's attached mate and seat. `39361fc0` uses the owner's selected mate to remove a passenger with reason 11. | An occupant can leave. The owner can remove a passenger. A stranger cannot remove another occupant. |
| Equipment changes | `394e8c14` constructs opcode `0xa9`. `397d1340` serializes owner, mate TL, passenger, flag, count, then item pairs. `397d13ca` caps the count at 2. | The packet accepts at most 2 changes and reads the whole body before any move. The authenticated character supplies ownership. |
| Equipment result | `397af340` serializes the change list then the `success` boolean at `397af363`. | Rejected owned changes send the authoritative slots with `success=false`. The old packet writer always sent true. |
| Equipment eligibility | `394e4494` checks level bounds. `397cf020` checks item tag 29. `397cf060` checks tag 1259. `394e4513` compares the underwater flag. | The item must have the mate equipment tag. Its underwater tag must match the actor model. The mate slot pack, item slot, and mate level must match. |

The native vehicle range helper measures collision shapes through `3989bf90`.
The server uses the authored seat position or the attachment doodad position.
It rotates and scales model offsets into world coordinates. If a direct driver
entry has no authored offset, it uses that vehicle's position. Child cannons use
the child vehicle position, not the parent ship position. This server check
prevents entry from another part of a large ship through a remote seat.

`MateSeatGameData` reads the exact actor aliases from
`model_attach_point_strings` and seat attachments from the model CDF. Actor
aliases differ from prefab aliases. Missing seats do not grant entry. The
separate seat loader tests cover actor aliases, absent seats, and asset paths.

## Server changes

`MateManager` checks both the account character ID collection and `OwnerObjId`
for commands and equipment. A removed mate or a reused TL ID does not grant
access. Target commands only select a target in the mate's world instance.

`MountSkillAuthorization` checks the exact mount instance, current occupant,
attachment parent, life state, and world instance. It rejects ordinary NPCs,
other players' pets, unknown mount skills, mismatched skill pairs, and wrong
seats. It returns the authored rider skill only after these checks pass.

Entry uses a lock for the character and the mount so two requests cannot claim
the same seat. Rejected entry does not remove buffs or change transforms. Exit
requests for an unrelated vehicle do not clear the character's current seat.
Removal retires root and child vehicle seats before detach. Late entry checks
the exact active mount again after it gets the locks. `MountRetirementTests`
checks 6 cases, including real entry calls blocked after the initial lookup.

The equipment packet uses server inventory and mate container types. Item IDs
and template IDs must match the current slots. Every pair passes the slot,
snapshot, reservation, and destination checks before the first move. A repeated
slot fails without a move. Native `397cdfc0` appends original snapshots.
`394e8dbb` reserves their slots only after the list is complete. Its count-2
weapon removal branch selects an already empty inventory slot through
`397d1920`, so the second pair does not need the first move to make space. An ordinary inventory item does
not cause an equipment cast exception. The response uses the authenticated
owner ID. `MateEquipmentContainer` checks eligibility on every container entry,
so another inventory transfer path cannot bypass the pet equipment rules.

## Tests and gameplay checks

`MountSkillAuthorizationTests` covers owner commands, stale identity, exact skill
pairs, pet level, rider state, death, instance boundaries, replacement buffs,
passenger rights, distance boundaries, equipment eligibility, rotated vehicle
seats, and concurrent passenger entry. Existing zone restriction packet tests
now use an owned mate with a real driver attachment and the wire mount skill ID.

The tests also cover stale seat occupants and inactive, finishing, finished, and expired stance buffs.
Invalid owned equipment packet tests check the failure body and unchanged inventory.
Two-change tests cover valid removal, a bad second slot, stale snapshots,
reserved gear, and repeated slots. The optional read-only compact test checks
all 64 buff mount rows and every slot in the 3 mate slot packs.
The combined build and test result is recorded in the release evidence.

After publication, check these client paths:

1. Equip and remove pet gear, including a swap between occupied slots.
2. Try the same packets with another player's mate TL ID.
3. Command an unmounted battle pet and use a mounted rider skill.
4. Enter and leave a horse passenger seat. Remove the passenger as its owner.
5. Try entry while distant, dead, or in another instance.
6. Enter a ship helm, a child cannon, and a vehicle passenger seat.
7. Change a tank stance and check that the action bar uses its replacement skills.
8. Remove the stance buff and check that the base vehicle skills return.

These paths still need human gameplay checks after publication.
