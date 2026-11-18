# NPC discovery and action range in r208022

Issue [#779](https://github.com/KeganHollern/aaemu-cluster/issues/779) treats
`CSStartInteraction` as a request to use an NPC. That premise is incorrect for
the current client and deployed source `06f1c19aedf40f2afe28de982664f6c42362c58c`.
The packet asks for the action list. It changes no server interaction, target,
inventory, or currency state.

## Client sequence

The [existing exact-client record](npc-interactions-and-quest-audit-r208022.md)
identifies the original DLL and runtime dump. Its retained native exports show:

1. Producer `393be030` sends opcode `0x68` before the approach check.
2. Handler `391d8f80` calls `393be850`, which supplies the returned action to
   `393be280`.
3. `393be280` calls `393bf270` before it opens the selected action.
4. `393bf270` calls range helper `39093860`. Outside range, it calls
   `390a6af0` to approach the target and defers the action.

A fixed short range on discovery would reject a normal distant click before
the client can approach. The native evidence does not establish a new server
discovery-distance limit. No such limit is added.

## Server action checks

- `CSInteractNPC` requires the current live NPC in the same world and instance,
  within the existing 5 m three-dimensional service range.
- Bank and auction operations require that interaction and check its range again.
- NPC purchases use their existing 3 m check and validate the authored catalog.
- NPC skills use the authored grant and skill range. Some authored service
  skills have ranges of 10 m or 15 m.
- Mount attachment checks its own live target, seat, permissions, and range.
- Quest interaction checks its own visibility and model-edge range.

The old `Distance-check` TODO in the discovery packet was misleading. It is
replaced with the protocol explanation. Regression tests cover metadata-only
discovery, rejection of distant bank actions, normal approach and bank use,
unknown targets, and every truncated request length.

This review resolves the reported unrestricted NPC-use premise. It does not
claim a real-client pass for this release. The focused client check is to
right-click a distant banker, let the character approach, and use the bank
normally. Remote action rejection is also covered by server regression tests.
