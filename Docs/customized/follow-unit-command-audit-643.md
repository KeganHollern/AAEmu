# FollowUnit command reference audit, issue 643

The deployed command handler contains a FollowUnit stub, but no active content references the affected command sets.
Issue #643 identifies unused command data as player content. This audit makes no movement change.

## Inputs

- AAEmu source: `1b35411e93185946a4757c29cba8748d7d626838`.
- Server compact SHA-256: `636ca9ecfe777bc86542e4b828f930c7861d2b4639b1bcfff670c064fee9c1ac`.
- Client compact SHA-256: `4f1ac86b2ae79fd35886d0cd7b1e5cccc3287a011a200667d97eb1c5bc4d79a4`.
- Client revision: `r208022`, from `client/history.txt`.
- Audit date: 2026-10-06 UTC.

Official upstream `6e2f40734` still contains the same FollowUnit warning and guard stub.
This result does not depend on a missed upstream fix.

## Confirmed references

`ai_commands` contains only 3 rows with `cmd_id=1`:

| Command | Command set | Parameters |
| --- | --- | --- |
| 34 | 1, `test_npc` | `param1=1`, `param2=0` |
| 187 | 20, `Q851_도망친병사` | `param1=1`, `param2=0` |
| 222 | 33, `Q1307_벌목꾼을 잡아와라` | `param1=1`, `param2=0` |

No `quest_components.ai_command_set_id` selects 1, 20, or 33.
No `npc_control_effects` row selects those command sets through `category_id=5` and `param_int`.
The only tracked world-script command sets are 185, 188, 192, and 195 in the Sharpwind Mines script.
The GM test command constructs its own FollowPath sequence. It does not select these sets.

The production command queue receives data through `NpcControlEffect`.
`WorldScriptController` also uses that effect for its authored command sets.
Neither path reaches the 3 FollowUnit rows.

## Current quests

Quest 851 uses component 3274 with `npc_ai_id=2`, NPC 5055, and skill 13613.
Its `ai_command_set_id` is null.
Talk act 4730 uses detail 353 and NPC 5055, with alias 219.
This separate quest-component FollowUnit mode does not call the command-set handler.

Quest 1307 uses component 6454 with normal `npc_ai_id=1` and a null `ai_command_set_id`.
Monster-hunt act 13711 uses detail 547. It asks for 5 kills of NPC 3134, with alias 265.
Its old command-set name does not describe the current objective.

## Scope and result

The issue's claim about retail follow duration is unconfirmed.
Unused server rows do not establish a target selector, follow duration, or completion rule.
The FollowUnit stub remains available for later research if active content needs it.
No gameplay check is needed for this audit because it changes no runtime behavior.

A separate, focused issue can assess quest-component FollowUnit for quests 851 and 1039.
Quest 1039 uses component 5093 and NPC 4729.
`QuestComponentTemplate.NpcAiId` loads those values, but `Quest.SetNpcAggro` handles only AttackUnit.
That separate work needs a quest interaction trace and confirmed stop rules before a movement change.

## Reproduction queries

Run these queries against the read-only server compact:

```sql
SELECT * FROM ai_commands WHERE cmd_id = 1;
SELECT * FROM quest_components WHERE ai_command_set_id IN (1, 20, 33);
SELECT * FROM npc_control_effects
WHERE category_id = 5 AND param_int IN (1, 20, 33);
SELECT id, quest_context_id, component_kind_id, npc_ai_id, npc_id,
       skill_id, ai_command_set_id
FROM quest_components WHERE quest_context_id IN (851, 1307);
```

Search tracked world scripts and command producers:

```sh
rg -n 'CommandSetId' AAEmu.Game/Data --glob '*.json'
rg -n 'EnqueueAiCommands|GetAiCommands' AAEmu.Game --glob '*.cs'
```
