# Loot eligibility and dice for r208022

This change resolves cluster issues #508 and #509. It changes server loot state and checks. It also enforces the native 50-player summary limit. The pickup reader now consumes its complete native body. No database change is necessary.

## Server behavior

Loot generation publishes the item list, rule snapshot, and eligible set under the container lock. The rule snapshot also uses the team lock.

The tagger and nearby members of the tagging team form the eligible set when the NPC dies. The server keeps its 200 m party eligibility range. It now checks the world instance and all 3 position axes. A tagging team with no eligible members does not give private rights to a different player who kills the NPC.

Only an eligible player can open or take private loot. After the 180-second public transition, other nearby players can take unclaimed loot. A previous dice winner keeps the claim if a full bag, a closed connection, or a position change prevents the grant. The winner can reconnect and claim with the same character ID. A replacement `Character` object cannot answer a pending dice choice sent to the previous object.

New dice pools and rotation candidates use the current character objects for eligible IDs. The server checks an active connection, its active character, and online state before a reply or grant.

The direct pickup distance is less than 3 m for NPCs and less than 9 m for doodads. The server subtracts scaled actor radii from the 3D center distance. This approximates the native collision-shape distance. It does not load a second set of collision shapes for loot. The native doodad path can therefore differ for large or irregular models.

Automatic team distribution uses the 200 m eligibility range. A dice winner does not need to stand within the direct pickup distance. Rotation filters candidates by world, range, and quest. The loot master must still belong to the team and the eligible set at the time of the grant. `TeamManager` also rejects a new loot master who does not belong to the team.

A mandatory dice roll takes precedence over rotation or master distribution. Only the initial pool can answer. Each player can answer once while the roll is active. A reply after the roll ends cannot add a player or grant the item.

Need produces an inclusive value from 1 through 100. Pass produces -1. The highest positive value wins. For a tie, the server rolls again for only the tied highest players. Their initial Need choice still applies. The server sends the current dice notification and summary packets for each round. It does not request another client choice.

After 60 seconds, the server passes each unanswered player. If all players pass, a later eligible pickup does not restart the same roll. The public transition also finishes active rolls before it opens unclaimed loot. Item reservation and the container lock prevent repeated grants.

## Exact-client evidence

The local `client/history.txt` reports `version 208022`. The source and dump have the same PE timestamp, entry point, preferred base, and image size:

| Field | Value |
| --- | --- |
| PE timestamp | `543cb835` |
| Entry point | `008c5d6d` |
| Image base | `38ff0000` |
| Image size | `01cb0a00` |
| Source `x2game.dll` SHA-256 | `3821c34366ff8b6f7aed95ba53d12216a5c31cb7c3407703d28eea639a621205` |
| Runtime dump SHA-256 | `a1995455440cdeb6356682e801f322cd946f3de3ad6595fae818d7a139100ae0` |
| `loot/loot_dice.alb` SHA-256 | `d288bdf87649d430a55941d75973484580cd7a9508fa307b56b563e0a6a2e550` |

The dump was already present in the workspace. This task checked its PE identity against the source binary. It did not create a new runtime dump.

Addresses below use the dump image base. GNU `objdump -d -M intel` shows these functions without a relocated-address adjustment.

| Conclusion | Evidence | Confidence |
| --- | --- | --- |
| Native NPC pickup uses a 3 m shape-distance limit | Loot-bag update near `394facb4` selects owner type 1, passes float 9 from `39ac9b70` to `39093860`, which takes its square root and compares the shape distance. A failed check calls bag close at `394fad0f`. | Confirmed |
| Native doodad pickup uses a 9 m shape-distance limit | The same update selects owner type 2 and calls `394f9480`. It passes float 9 to `39093800`, which compares that value directly. | Confirmed |
| `CSLootItemPacket` contains a full loot ID and count | Native producer `394f9790` sets opcode `8f` and passes the item ID and its count to `397abf10`. Serializer `397ba0c0` writes the ID through the 8-byte helper and the count through the 4-byte helper. | Confirmed |
| `CSLootDicePacket` uses a full loot ID and a boolean | X2Loot registration `3948b069` names `DoDiceAction`. Its callback `3948a5c0` calls producer `394fa770`. The producer sets opcode `91`, constructs the ID and choice through `397abf60`, and sends the packet. Serializer `397ba120` writes the ID then the boolean. | Confirmed |
| One client choice consumes the pending dice item | Producer `394fa770` finds the pending key, sends the choice, and removes that pending entry. | Confirmed |
| Need is true and Pass is false | `loot_dice.alb` callbacks at source lines 97-109 pass those boolean values to `DoDiceAction`. | Confirmed |
| The client passes after 60000 ms | The callback at source lines 112-126 compares elapsed UI time with 60000 and calls `DoDiceAction` with false. | Confirmed |
| Dice notification uses a signed result and the current loot chat event | Native `OnLootDiceNotify` at `391d9a10` reads the signed result at packet offset `150`, builds the item link, and emits event `80`, `LOOT_DICE`. | Confirmed |
| A summary holds at most 50 players | Factory `391aea90` allocates `118` bytes and uses parser `397ab250` then `397cf9a0`. The payload stores IDs at `+0c` and dice at `+d4`, a difference of 200 bytes for 50 IDs. The wire interleaves `u32 characterId` and `i8 dice` after `u64 lootId` and `i32 count`. | Confirmed |
| A summary clears the pending pickup flag for the loot ID | Native `OnLootDiceSummary` at `391d9b50` calls `394f9410`. That function matches the full loot ID and clears the bag entry flag. | Confirmed |
| Automatic tie rounds can use the current notification and summary lifecycle | Notifications report each server result. Summaries clear the pending pickup flag. Neither handler needs a second client choice. | Inferred from confirmed handlers |
| The 200 m team eligibility range matches the deployed server policy | Original loot commit `8bbcb6b45` introduced `MaxLootingRange = 200f`. Other tagging and XP paths use this constant. | Confirmed server policy, not a native rate claim |

The `CSLootDicePacket` body contains 9 bytes: `u16 itemIndex`, `u16 ownerType`, `u24 ownerObject`, `u8 upperObjectByte`, `bool need`. These fields form the full 8-byte loot ID followed by the choice. The native `CSLootItemPacket` body contains the full loot ID followed by its 4-byte item count. The old reader consumed only 11 bytes. The corrected reader consumes all 12 bytes and rejects extra data. It preserves the current whole-entry grant rule. The server item determines the quantity. This task does not define a partial-pickup or sentinel-count contract. Both readers preserve the full owner object ID and reject invalid owner types. The dice reader accepts only the native boolean choices 0 and 1. `ObjectIdManager.LastId` is `0x00FFFFFE`, so current server object IDs have a zero upper byte. The output packets retain this form.

## Tests and client checks

`LootingContainerTests` covers private/public access, native pickup limits, scaled actor radii, 3D boundaries, invalid coordinates, different instances, and stale item references. It also covers an injected roll, duplicate replies, 3-player highest rolls, repeated ties, all-pass, the 60-second task, concurrent replies, and winner claim retries. Packet tests cover valid Need/Pass and pickup bodies, invalid loot identities, truncation before every byte, and exact summary consumption.

The automated tests use deterministic dice values and synthetic loot items. They do not change a live database. No human client test occurred during this work.

After deployment, do these focused client checks:

1. Kill an NPC outside a party. Check that another character cannot take its private loot.
2. Move beyond the corpse pickup distance. Check that the loot bag closes and pickup fails.
3. Set a party dice threshold. Make 3 characters choose Need and compare the winner with the highest result.
4. Let one character leave the choice unanswered for 60 seconds. Check that the server treats this as Pass.
5. Fill the winner's bag. Free one slot and take the item with that character.
6. Check that an unclaimed corpse becomes public after 180 seconds.
7. Set a party loot master. Check a normal grant, then remove the master from the party and repeat.
8. Disconnect a dice winner before the last reply. Reconnect that character and check that its reserved item remains available.
