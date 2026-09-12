# Quest sphere findings for r208022

The review on 2026-09-11 covers quests 5272, 5439, 5716, 5898, 5979, 6039, 6050, 6213, and 6216 from aaemu-cluster#89.
The supplements add 5 volumes for 4 available quests.
The other 5 quests have no normal start route in the current data.

The coordinates come from deployed NPC positions, deployed doodad positions, or the arena entry config.
The 30 m radius is an authored value from the issue guidance.
The exact client data does not contain the retail trigger radius for these objectives.

## Supported destinations

| Quest | Component | World | Zone key | Center X, Y, Z | Source |
| --- | --- | --- | --- | --- | --- |
| 5716, The Price of Labor | 24586 | `main_world` | 186 | 13630.061, 10353.12, 194.31546 | NPC 13734, Borm Gaffrion |
| 5979, Unleashing the Beast | 25757 | `instance_training_camp` | 198 | 1101.1, 401.5, 199 | Battlefield 7, first team entry |
| 5979, Unleashing the Beast | 25757 | `instance_training_camp` | 198 | 1229, 530, 199 | Battlefield 7, second team entry |
| 6213, A Quick Escape | 26636 | `main_world` | 144 | 13553.896, 15867.64, 176.9137 | Memory Tome doodad 1886 |
| 6216, Return to Headquarters | 26646 | `main_world` | 158 | 21037.6, 8314.3, 372.3 | Memory Tome doodad 7900 |

The main-world positions come from `AAEmu.Game/Data/Worlds/main_world/npc_spawns.json` and `doodad_spawns.json`.
The arena positions come from `AAEmu.Game/Data/battlefields.json`.
The exact client `game/worlds/main_world/world.xml` assigns each main-world position to the listed zone key.
Its cell and sector pairs are 5716: `(13,10)/(4,1)`, 6213: `(13,15)/(3,7)`, and 6216: `(20,8)/(8,1)`.

### The Price of Labor

NPC 13726, Pirate Blacksmith Kamlen, starts quest 5716.
His deployed position is `(15114.89,23076.37,106.2985)`.
The acceptance requirements are completed quest 5715 and equipped item 28799.
The quest text names Tryster's Garden below Ezna and Borm Gaffrion.
The only deployed NPC 13734 position supplies the sphere center.

The exact client file `game/worlds/main_world/map_data/npc_map/w_two_crowns.dat` also places NPC 13734 there.
Its 16-byte record at offset 12192 uses the format `<Ifff`.
The record gives `(13630.060546875,10353.1201171875,194.46800231933594)`.
The supplement uses the deployed NPC height, which differs by approximately 0.153 m.

Quest item 30179 uses skill 23987.
Requirement 44884 limits that skill to sphere 933.
Requirement 44885 also needs quest 5716 in progress.
The supplement supplies the location for both the quest objective and the skill requirement.

The song also has a separate spawn defect.
Effect 34790 uses `NpcSpawnerSpawnEffect` 3017, which refers to spawner 123214.
The server compact contains neither that spawner template nor a member row for it.
NPC 13734 already has an ambient position in the deployed data.
This sphere change does not change the song effect or the compact database.

### Unleashing the Beast

NPC 13389 starts quest 5979 from 6 main-world positions.
The quest needs level 40.
Its text tells the player to enter Drill Camp, then speak to Arena Manager NPC 14168.
The current arena code uses battlefield 7 and the 2 team entry positions in `battlefields.json`.

Both entries have a nearby report NPC.
The NPC positions are `(1091.61,390.974,198.54991)` and `(1240.21,539.041,198.61241)`.
Each position is less than 15 m from its team entry.
The exact client `instance_training_camp/world.xml` sets zone key 198 and origin `(0,0)`.
The supplement belongs to this instance world.

### A Quick Escape

Quest 1602 chains into quest 6213 through completion component 7697.
Quest 1602 starts at doodad 1747 after quest 1601.
Doodad 1747 and target NPC 6625 both have deployed positions.
Quest 6213 highlights doodad 1886 and names the Riverspan Memory Tome in its English text.
The unique deployed position of doodad 1886 supplies the sphere center.

### Return to Headquarters

Quest 1086 chains into quest 6216 through completion component 5435.
Quest 1086 starts from NPC 3435 at `(21021.482,8317.27,371.44318)`.
Quest 6216 needs level 9 and mother faction 149.
Its objective highlights doodad 7900 and names the Watermist Forest Memory Tome.
The unique deployed position of doodad 7900 supplies the sphere center.

## Unavailable quests

The NPC checks cover every `Data/Worlds/*/npc_spawns*.json` file.
The exact client NPC maps also contain none of the missing starter NPCs below.
A row in `npc_spawner_npcs` defines a template member.
It does not create a position in a world.

| Quest | Components | Current barrier |
| --- | --- | --- |
| 5272 | 22762 | The only starter, NPC 13010, has no position. Report NPC 13011 also has no position. |
| 5439 | 23509 | Acceptance component 23508 has no acts and no external start route. |
| 5898 | 25474, 25825, 25826 | The only starter, NPC 14200, has no position. The compact marks Golden Ruins as closed. |
| 6039 | 26000 | The only starter, NPC 14317, has no position. The quest also needs completed prologue quest 6037. |
| 6050 | 26033 | Its chain starts at quest 6046, whose only starter is missing NPC 14317. |

Quest 5439 has no incoming quest-chain edge or item, doodad, sphere, skill-effect, or NPC combat start route.
Its level requirement and quest loot item 28345 do not start the quest.
Its `sphere_quests` row 1281 uses objective trigger 1.

Quest 6050 follows the chain `6046 -> 6047 -> 6048 -> 6049 -> 6050`.
The acceptance component for quest 6050 refers to itself, as other chain targets do.
That self-reference does not create an independent start route.
The client contains `instance_prologue`, but its world file does not supply the missing Captain NPC.

Check these exclusions again if their starters, incoming chains, world positions, or zone availability change.
Do not assign coordinates from a quest title alone.

## Zone IDs

The issue lists `zones.id` values from the compact database.
Sphere supplements use world zone keys.
For example, compact zone 198 is Golden Ruins, with zone key 281.
The arena instead uses compact zone 144 and zone key 198.

The previous Borrowed Bravery supplement used compact zone ID 9.
The client world assigns its position to zone key 142.
This change corrects that field and keeps the position and radius.

## Source hashes

These SHA-256 values identify the source files for this review.
The compact files came from the cluster workspace and remained read-only.
The client files came from the local r208022 `game_pak`.

| Source | SHA-256 |
| --- | --- |
| `compact/server.sqlite3` | `636ca9ecfe777bc86542e4b828f930c7861d2b4639b1bcfff670c064fee9c1ac` |
| `compact/client.sqlite3` | `4f1ac86b2ae79fd35886d0cd7b1e5cccc3287a011a200667d97eb1c5bc4d79a4` |
| `Data/Worlds/main_world/npc_spawns.json` | `d301e3aa7f1d024e30b9337cf30ff5ab24d3af84759ceaa5146d9b3353678209` |
| `Data/Worlds/main_world/doodad_spawns.json` | `babfe406a4885c81f610ccd9cf40ab7e56c8e3d329bad6c33369024c3d3d2164` |
| `Data/battlefields.json` | `459bc7da58a4559631b614b8c84b4912a6d6234b18bf60dca21999516f53a2c8` |
| `Data/Worlds/instance_training_camp/npc_spawns.json` | `7b509d3691e66ea51e801304bca3b4edd789ff2100afc554351262fcac37b417` |
| Client `main_world/map_data/npc_map/w_two_crowns.dat` | `debe1ba6c783d5f6643e97534ee9ca9c55289c2de54c8999511e16b3d08e850d` |
| Client `main_world/world.xml` | `0d37d4d06c40f361a824ee95d2e56bb7acafae437e1c8a765eb50e3a35f5fd21` |
| Client `instance_training_camp/world.xml` | `5a75b027d61538bf5298a5c79c58dad8453fe344d0194dd2f15bb0fcdfce67f5` |

The server data paths in this table are relative to `AAEmu.Game`.
The client world paths are relative to `game/worlds`.

## Client geometry check

The full client corpus contains 2,479 volumes in 69 `quest_sign_sphere.g` files across 9 worlds.
The review checked all 69 file zone keys against each world's exact `world.xml`.
Every key exists in its own world.
All positions and radii parse, all values are finite, and all radii are positive.
The main world contains 2,453 volumes, and the other 8 worlds contain 26.

This check supports the parser change from the global zone lookup to each world's zone origin.
The current r208022 corpus does not trigger the new missing-zone error.

## Focused gameplay checks

No human gameplay check occurred during this research.
Do these checks with the released server changes.

1. Start quest 5716 and approach Borm Gaffrion in Tryster's Garden.
2. Make sure that the location objective completes.
3. Check the song separately, because its missing spawner is a separate defect.
4. Start quest 5979 and enter Drill Camp from each team.
5. Make sure that the objective completes on arrival and the nearby Arena Manager accepts the report.
6. Complete quest 1602, then use Recall or a portal to the Riverspan Memory Tome.
7. Make sure that quest 6213 completes and the next quest starts.
8. Complete quest 1086, then use Recall or a portal to the Watermist Forest Memory Tome.
9. Make sure that quest 6216 completes and the next quest starts.
