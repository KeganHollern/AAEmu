# NPC assistance and shared threat for r208022

This change resolves the general assistance work in cluster issue #320.
It also supplies the group combat rules for #527.
The change adds no dungeon scripts, compact rows, SQL updates, or packets.

## Exact-client and data evidence

The source base is `d60b70d0984456c634c45dd32ac4ea975ac1808a`.
The server compact SHA-256 is `636ca9ecfe777bc86542e4b828f930c7861d2b4639b1bcfff670c064fee9c1ac`.
The client compact SHA-256 is `4f1ac86b2ae79fd35886d0cd7b1e5cccc3287a011a200667d97eb1c5bc4d79a4`.
The client `x2game.dll` SHA-256 is `3821c34366ff8b6f7aed95ba53d12216a5c31cb7c3407703d28eea639a621205`.
The reused native dump SHA-256 is `a1995455440cdeb6356682e801f322cd946f3de3ad6595fae818d7a139100ae0`.
The dump uses image base `0x38ff0000`, timestamp `0x543cb835`, entry RVA `0x8c5d6d`, and image size `0x1cb0a00`.

The server compact contains 130 aggro links and 643 NPC link rows.
These rows refer to 572 distinct NPC templates.
All NPC and link references resolve.
One NPC/link pair occurs twice, so the loader removes duplicate membership.
The client compact does not contain these 2 link tables.
All 572 linked NPCs have matching relevant flags and factions in the client and server compacts.

Native functions `FUN_396ce880` and `FUN_396cea20` load the link tables.
Function `FUN_396cfd30` builds each NPC's vector of links.
This evidence confirms many-to-many membership, not the complete server combat algorithm.

The authored comment for link `109` describes one direction of help.
The wizard `10095` helps the yaksa `3690`, but the yaksa does not help the wizard.
The wizard is passive, accepts links, uses special rule `None`, and disables the sight check.
The yaksa rejects links.
The receiver flags explain this direction without an aggression requirement.
Links `99`, `87`, and `30` provide more passive-helper examples.
They cover adult turtles, male lions, and farmers.

The old sight condition rejected every helper with its sight flag disabled.
The old helper path also did not read authored link membership.
The new path corrects both defects.

## Assistance rules

The loader publishes one complete NPC-to-link snapshot after each successful load.
A failed load keeps the previous snapshot.
Reload removes memberships that no longer occur in the data.
A pair check reads only the 2 NPCs' membership sets.
It does not scan all link rows or all NPCs.

An idle helper needs all these conditions:

- The source, helper, and attacker are alive and present in the same world instance.
- The source and helper own their current AI objects.
- The helper accepts aggro links and has no current threat or combat state.
- The helper is within its own authored help distance from the source.
- If the helper enables the sight check, `CanSeeTarget` accepts the attacker.
- The helper can attack the attacker under the current faction and protection rules.
- The pair shares an authored link, shares an active group with a supported aggro rule, or matches a special relation rule.

The supported relation rules remain `FactionHelp`, `FriendlyHelp`, `NeutralHelp`, and `EveryoneHelp`.
An unrelated aggressive NPC with rule `None` does not help solely because it is aggressive.
Explicit membership permits a passive helper, but it does not bypass receiver acceptance.
The helper enters combat through an atomic initial-threat operation.
Repeated requests cannot add repeated help threat to an active fight.
The next helper AI tick processes the target notification.

The current `CanSeeTarget` checks visibility and stealth.
It does not test a ray through world geometry.
This release does not claim a new geometric sight contract.
The exact rules for `aggro_link_special_guard` and `aggro_link_special_ignore_npc_attacker` remain unconfirmed.
This release preserves their previous treatment.
It does not invent new guard or NPC-attacker rules from their names.

## Approved group combat rules

The user approved the distinction because the exact client does not define it:

- `None` adds no group combat link.
- `AggroLink` alerts eligible nearby group members through the assistance rules above.
- `AggroShare` also combines effective threat across eligible members of one group occurrence.

Shared threat is an occurrence rule. It does not combine separate occurrences of the same group template.
Active members receive the same damage and heal totals for each attacker.
Copies bypass damage tags and quest credit.
Source damage applies attacker and receiver multipliers once.
A single heal event contributes once per group, even when several members subscribe to that event.
The eligible member with the lowest member row ID supplies the heal receiver multiplier.
The normal 0.6 heal factor applies once.
These are explicit server rules, not recovered retail calculations.

Shared copies use group membership directly.
They do not need a second authored static link or the nearby-help distance.
Dead, removed, returning, protected, or faction-blocked members do not receive a copy.
Base combat and flytrap combat use object ID as the tie rule for equal total threat.
Their normal visibility, movement, and skill-controller restrictions still apply.
These restrictions can delay a member's target change.

Each NPC keeps its own threat entries and event subscriptions.
The group holds shared values under one combat lock.
Buff and attack checks occur before that lock because buff callbacks can enter combat code.
The mutation gate checks current membership, world identity, and life state again.
Quest, packet, and AI callbacks run after combat locks.
A delayed update must still own its original local threat entry.
It cannot restore an entry that cleanup already removed.
Heal and death subscriptions use atomic delegate updates.
Concurrent combat, NPC skill, and buff subscriptions cannot overwrite each other.

A replacement member receives the current shared values after world publication.
It receives one subscription per target and no copied damage credit.
Removal clears subscriptions through retained target references, even after a target leaves the world registry.
Direct deletion retires combat separately from the spawner's removal flag.
A later spawner removal can still clear its population records and release its object ID.
Retirement clears shared values and each former member's local combat records.
Object ID reuse cannot attach a new attacker to the old attacker's subscriptions.
A late death callback from the old attacker cannot remove the new attacker.

## Checks and human validation

The unit tests use the real compact link `109` and synthetic negative cases.
They cover direction, reload, missing references, distance, sight, factions, instance separation, and removal.
Group tests cover separate occurrences, shared totals, healing, return state, target identity, and replacement members.
The local full test run also checks unrelated combat, spawn, quest, and content behavior.

Human validation remains in cluster HUMAN VALIDATION #573.
Check a passive authored helper, the reverse no-help direction, distance limits, and combat exit after a reset.
Use an explicit group placement to check `AggroLink`, `AggroShare`, and member refill.
A captured individual NPC placement is not proof of a runtime group.
The shared target change check needs 2 controlled attackers.
