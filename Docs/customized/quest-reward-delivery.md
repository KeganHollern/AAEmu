# Pending quest reward delivery

Quest completion now waits for every mandatory item reward to be delivered.
An invalid item reference or a failed inventory/mail operation leaves the quest
active. Items already granted or successfully mailed are removed from its
pending quantities, so retrying delivers the remainder. Base XP, copper, and
quest-item cleanup wait until mandatory item delivery succeeds.

Aggro ranking can explicitly exclude item rewards. This is a valid completion
outcome, and does not produce a delivery failure. The earned item eligibility
and reward ratio are retained when a pending quest is saved and loaded.

## Diagnose the failure

After static data loads, `QuestManager.Initialize` checks all ordinary and
selective item-reward references. An invalid reference produces a diagnostic
with the exact authored identifiers:

```text
Invalid quest reward reference: quest=<questId>, component=<componentId>, act=<actId>, item=<itemId>, count=<count>. Delivery stays pending.
```

The check rejects a missing item template, a nonpositive template stack limit,
or a negative reward count. Other quests remain available. At runtime, a failed
active reward emits:

```text
Quest reward delivery pending: quest=<questId>, owner=<ownerId>, step=<step>, item=<itemId>. No completion is recorded.
```

The runtime error is reported once while that failure remains pending. An item
value of zero can identify an inventory/mail operation failure rather than a
specific missing template. Inspect the surrounding item or mail diagnostic and
record the quest, owner, component/act if available, and source/content revision.
The player receives the existing mail-failure error rather than a success-only
completion notification.

## Correct content and retry

Use the diagnostic's quest/component/act IDs to inspect the authored reward and
its item template in the tracked compact data. Confirm the intended reward from
authoritative source data before changing the reference. A similar item name is
not evidence for a replacement ID.

Prepare and review the specific content correction through the existing AAEmu
and cluster release process, then publish a forward release through GitHub
Actions and Keel. If the correction also changes launcher-managed client
content, use the existing coordinated client-content release workflow. The
server validation and retry-state changes described here do not require local
client binaries, Lua extraction, a changed quest packet, or a client patch.

For an inventory or mail failure, resolve the reported cause and reconnect the
character. Loading the active quest queues evaluation of its saved pending
state. A quest still waiting in its Ready step may require the report action
again. A quest already in Reward resumes delivery of its remaining items;
previous successful grants are not queued again. Keep the active quest intact
while correcting the failure rather than manually marking it completed or
replacing its reward with an unverified item.

## Persistence and deployment

`SQL/updates/2026-09-07_aaemu_game_quest_reward_state.sql` widens `quests.data`
from `TINYBLOB` to `BLOB`. This preserves existing values and gives the active
quest record room for its pending reward quantities, cleanup items, selected
reward, earned reward flags/ratio, and applied act/component effect IDs. Existing
records without the new suffix remain readable.

The deployment lineage uses `Connections__AutoApplyUpdates=true`; the normal
application updater applies this migration before the new active-quest records
are saved. It needs no separate in-cluster migration job or manual SQL step.
Direct cluster inspection or intervention still follows the cluster repository's
Kegan-only operator rule.

## Focused gameplay checks

- Complete an ordinary quest with bag space and confirm its expected item,
  XP/copper, cleanup, and one completion notification.
- Complete a quest with insufficient bag space and confirm the expected mail
  attachments, including rewards spanning multiple mails.
- In a controlled failure/retry test, grant part of a multi-item reward, save
  and reconnect, then confirm only the remaining quantities are delivered.
- Exercise an invalid reward reference and confirm the quest remains active,
  no unrelated reward or cleanup runs first, and the diagnostic identifies the
  authored reference. Publish the reviewed correction and retry.
- Complete an aggro-ranked quest without item eligibility and confirm it can
  finish with its earned non-item reward behavior.

Automated tests cover these failure and persistence paths. Gameplay on the
r208022 client remains a separate validation step; publication alone does not
claim that it has been performed.
