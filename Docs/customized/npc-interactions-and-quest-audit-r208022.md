# NPC interactions and quest audit for r208022

Issues #530 and #133 contain unsupported audit claims. The exact client separates normal NPC skills from extended interactions.
It also resolves quest alias names locally. The authored quest-mail data have no active delivery trigger.

This record defines those limits. It does not claim complete support for every NPC service or the dormant quest-mail subsystem.
The baseline source is release `5b3c151bb770806140291833d4f8e39e3c8fabec`.

## Input identity

`client/history.txt` identifies version 208022. The original DLL and runtime dump have matching PE identity fields:
timestamp `0x543cb835`, image base `0x38ff0000`, entry RVA `0x8c5d6d`, and image size `0x1cb0a00`.
The review reused a local runtime dump. It did not make a new process capture.

| Input | SHA-256 |
| --- | --- |
| Original `x2game.dll`, 18483712 bytes | `3821c34366ff8b6f7aed95ba53d12216a5c31cb7c3407703d28eea639a621205` |
| Runtime `x2game.dumped.dll`, 30085120 bytes | `a1995455440cdeb6356682e801f322cd946f3de3ad6595fae818d7a139100ae0` |
| Stock r208022 compact | `784d362434a2a0fd0a29fbc1bbb7f771d9ffbe2c8568339ac54d6fdcf44d2ed7` |
| Server compact | `636ca9ecfe777bc86542e4b828f930c7861d2b4639b1bcfff670c064fee9c1ac` |
| X2UI `questcontext/common.alb` | `10f92a9708142b47495b88d41726f83ff39182d6c998a1e516a5974a95e17b58` |

Ghidra 12.1.3 supplied native decompilation. GNU objdump supplied instruction checks for the packet producer, callback arguments, skill sender, and alias lookup.
The research copies and exports remain in the cluster workspace's ignored task directory, `.tools-re/npc-services-315-530-133-20261003/`.
The `research-530` and `research-133` directories contain the queries, hashes, native addresses, and small evidence exports.

## Issue #530: normal NPC skills

The client uses the normal skill route for the listed salon, language, acquittal, and victory actions.

| Native address | Confirmed behavior |
| --- | --- |
| `396cf960` | Loads `id`, `npc_interaction_set_id`, and `skill_id` from `npc_interactions`. |
| `396cfb00` | Loads set IDs from `npc_interaction_sets` and groups their interaction rows. It does not read set names. |
| `393bb420` | Adds the NPC template's authored interaction skills to its action list. |
| `393be280`, `393c1280` | Dispatch the chosen NPC action. |
| `393c0ca0` | Checks the chosen skill against that NPC's interaction set. |
| `39397e50` | Sends the skill request with a Character caster and explicit NPC target. Both wire kinds are 0. |

This path uses the skill ID, not the `npc_interactions.id` row ID.
The right-click discovery flow is separate from the final cast.
Producer `393be030` sends opcode `0x68`, `CSStartInteraction`.
Handler `391d8f80`, named `OnNpcInteractionSkillList`, calls `393be850`, which feeds the action to `393be280`.
This review did not change or fully re-audit the discovery response layout.

The server already loads the grants in [SkillGrantGameData.cs](../../AAEmu.Game/GameData/SkillGrantGameData.cs).
[SkillCastAuthorization.cs](../../AAEmu.Game/Models/Game/Skills/SkillCastAuthorization.cs) checks the named NPC, its current world and instance, and its authored set.
[CSStartSkillPacket.cs](../../AAEmu.Game/Core/Packets/C2G/CSStartSkillPacket.cs) applies that authorization before the ordinary skill call.
[Skill.cs](../../AAEmu.Game/Models/Game/Skills/Skill.cs) resolves the explicit target and applies the skill range check.
Commit `1821e66e9a7e6119b7e16008c983b5149469f1a1` added the relevant grant checks on 2026-09-14.

Both compacts contain 114 interactions and 111 sets. Every interaction names a defined set.
The stock compact has 141 NPC references to positive set IDs. The server compact has 142.
Both contain row 87, set 59, whose skill 24594 is absent. This row cannot justify a new invented skill.

| Skill | Service | Set | NPC examples | Maximum range | Cast time |
| --- | --- | ---: | --- | ---: | ---: |
| 22780 | Salon | 12 | 13640 | 15 m | 0 ms |
| 21366 | Language | 1 | 12474, 13031 | 10 m | 100 ms |
| 21521 | Language | 5 | 12477 | 10 m | 100 ms |
| 21335 | Acquittal | 2 | 10894, 10895 | 10 m | 4000 ms |
| 23146 | Victory buff | 11 | 13660, 13674 | 4 m | 2000 ms |

These selected rows match in both compacts. All use target type 5, `AnyUnit`.
A common 5 m limit would conflict with the authored ranges.

## The separate `CSSelectInteractionEx` contract

Producer `393b3120` constructs C2G opcode `0x6b`, with vtable `399d2db8`.
Constructor `397ac0d0` stores 3 fields. Serializer `397c7980` writes an 11-byte body:

| Offset | Field | Confirmed meaning |
| --- | --- | --- |
| 0 | Unsigned 24-bit `targetId` | The Character's selected target, from offset `+0x1aa0`. |
| 3 | Signed 32-bit `interactionEx` | The active extended-interaction object's virtual type value. |
| 7 | Signed 32-bit `var1` | The selected UI interaction value. Its meaning depends on the type. |

Callback `3947dac0` implements `X2Interaction.SelectInteraction`.
It uses this packet only when a separate extended-interaction object exists at manager offset `+0x48`.
The producer also needs active state at `+0x38`. It clears local interaction state after the send.
The separate NPC selection callback, `3947db70`, calls `393be280` directly.

The numeric `interactionEx` enum and its full original-server effects remain unknown.
The trace does not prove that the packet is harmless or obsolete.
It does prove that `var1` cannot be treated as a general NPC skill ID.
The current [CSSelectInteractionExPacket.cs](../../AAEmu.Game/Core/Packets/C2G/CSSelectInteractionExPacket.cs) remains inert until a concrete feature supplies that missing contract.

Issue #530 needs corrected packet classification and regression coverage for the normal NPC route.
It does not justify a new extended-interaction service dispatcher.
A failed final salon, language, acquittal, or victory effect needs its own effect-specific evidence and work.

## Issue #133: quest alias names

Native `398899b0` loads `SELECT id, name FROM quest_act_obj_aliases` and resolves localized names.
Native `39856220` looks up an alias in that cache.
Native `392e1cd0` registers these text macros:

| Macro | Consumer |
| --- | --- |
| `qst_obj_alias` | `392dccd0` |
| `qst_obj_interact_alias` | `392dcd50` |
| `qst_report_alias` | `392dd880` |

Each consumer reads the alias cache and appends the name to text.
Native `393774c0` also adds alias names to the objective Lua table.
`GetQuestObjectiveText` at `394a25b0` and `GetQuestJournalObjectiveText` at `394a3f20` use that formatter.
The journal function adds objective counts as separate fields.

X2UI `common.alb` calls `GetActiveQuestObjectiveText` at authored line 522 and sets objective widget text at line 539.
It calls `GetQuestJournalObjectiveText` at line 1360 and applies UI macros to the returned summary.
The alias table has only `id` and `name`, with no progress counter.
Other objective tables contain 2746 nonzero alias references, but there are zero standalone `QuestActObjAlias` acts.

[QuestActObjAlias.cs](../../AAEmu.Game/Models/Game/Quests/Acts/QuestActObjAlias.cs) already describes this client text role.
[QuestManager.cs](../../AAEmu.Game/Core/Managers/QuestManager.cs) loads `UseAlias` and `QuestActObjAliasId` on objective templates.
A server alias-name loader is not needed for the claimed missing counter.

## Issue #133: authored quest mail

Read-only queries give identical results in the stock and server compacts:

| Table or condition | Count |
| --- | ---: |
| `quest_act_obj_aliases` | 2746 |
| `quest_acts.act_detail_type = 'QuestActObjAlias'` | 0 |
| `quest_mails` | 1 |
| `quest_mail_sends` | 0 |
| `sphere_quest_mails` | 0 |
| `quest_mail_attachment_items` | 0 |
| `quest_mail_attachments` | 2, both empty |
| `quest_act_obj_send_mails` | 0 |
| `unit_reqs.owner_type = 'QuestMailSend'` | 0 |

The audit also examined every mail-named column and polymorphic text type column.
The only other mail-related records are 7 ordinary `DoodadFuncNaviOpenMailbox` entries.
The sole mail template has ID 1, name `영원의 섬으로`, NPC 13144, attachment set 2, and zero money.
Its comment is `인던 체험 이벤트`. No authored send condition or sphere trigger references it.
Both attachment-set names are `테스트`, and neither contains an item.

Native `39892200` loads mail templates. Native `39892430` separately loads send conditions and their `QuestMailSend` unit requirements.
The single template row does not establish a quest with a lost reward.
The server does not fully support this dormant authored quest-mail subsystem.
New behavior needs an active trigger, its send rules, and its packet and transaction contracts.

Ordinary reward overflow already uses a separate path.
[QuestRewards.cs](../../AAEmu.Game/Models/Game/Quests/QuestRewards.cs) calls `MailItemRewards` when the bag lacks space.
That path calls `MailManager.TryCreateQuestRewardMails`, preserves undelivered reward counts after failure, and sends `SCQuestRewardedByMailPacket`.
It does not use `quest_mails`.

The evidence supports closure of #133 as not planned because the audit premise is unsupported.
No quest source or compact change is needed for that conclusion.
This static review does not count as a human gameplay pass.
