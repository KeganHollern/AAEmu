# Merchant sale batches — issue #304

Issue: <https://github.com/KeganHollern/aaemu-cluster/issues/304>

The former sale handler could accept the same inventory slot repeatedly, move
its item to buyback once, and credit its refund for every repeated entry. The
sale handler now rejects the entire malformed batch before changing inventory
or money.

## Current behavior

- Decode the existing packet completely, including up to 255 entries. Keep the
  secondary object field and per-entry trailing `uint` unconfirmed; neither
  supplies an authoritative quantity. Sales use each server stack's full count.
- Resolve the requested NPC from the character's current world. Require an
  active merchant with the matching object ID, world and instance, within the
  existing `CSBuyItemsPacket` three-metre horizontal interaction range.
- Reject repeated `(container type, slot)` pairs or item IDs. Every entry must
  match the current bag/equipment slot, exact registered item object, container,
  owner, item template, positive bounded stack count, sellability and grade.
  Unsupported, missing or stale entries fail the whole batch.
- Calculate each grade-adjusted unit refund using checked wide arithmetic,
  truncate it before multiplying by the stack count, and cap the complete batch
  at the signed `int` money-delta limit. `MerchantRefund` supplies the same
  calculation to selling and the existing buyback executor. This also prevents
  large-refund overflow or floating-point rounding from making buyback cheaper
  than the preceding sale.
- Under `SaveManager.PersistenceSyncRoot`, stage every exact item move to
  buyback and the wallet credit with `InventoryMutation`. A failed move or
  credit restores all earlier moves, slot order, dirty flags and money. The
  packet reports errors only after disposal has restored the batch.
- Once preparation succeeds, queue the existing item-row deletions and then
  publish the item and money notifications. Callbacks see every sold item in
  buyback, the final wallet and all deletion marks. Item IDs and details remain
  intact for buyback. Notifications use the shared helper's 30-task batches.

This change uses the existing periodic save and buyback deletion queue. It does
not add an immediate database settlement API or schema update. Buying an item
back marks it dirty in a persisted container; the existing save deletes the
queued old row before saving that current item again.

## Validation

Based on shared inventory helper `d4eb4bc6c` and its wallet prerequisite.

- Release build of `AAEmu.UnitTests`: passed, zero errors; no warnings in the
  changed feature files.
- Merchant namespace tests: **59 passed, 0 failed, 0 skipped**. This includes
  **42 new sale cases** and 17 existing purchase cases.
- `git diff --check`: passed.
- Independent code review: no remaining blockers after the shared sell/buyback
  refund calculation and round-trip regressions were added.

The new cases exercise the actual packet and executor, including 255 duplicate
entries, 255 distinct sales and bounded notifications, repeated IDs, stale
slots, invalid later entries, merchant identity/instance/range, stack and grade
validation, later move failure and exception, wallet/product/batch overflow,
error visibility after rollback, final state at callbacks, replay and twelve
concurrent copies. Real sale–buyback–sale tests cover `int.MaxValue`, the
floating-point precision boundary `16,777,217`, and per-unit truncation.

Commands, with the local .NET 10 SDK and cached NuGet packages:

```sh
dotnet build AAEmu.UnitTests/AAEmu.UnitTests.csproj -c Release --no-restore --disable-build-servers -m:1 -p:UseSharedCompilation=false -p:NuGetAudit=false
dotnet AAEmu.UnitTests/bin/Release/net10.0/AAEmu.UnitTests.dll --treenode-filter '/*/AAEmu.UnitTests.Game.Models.Merchant/*/*'
```

## Focused checks after publication

Human gameplay validation has not been performed.

1. At a merchant, sell a mixed batch of bag stacks and equipped sellable items.
   Check the total refund, inventory removal and buyback entries.
2. Buy an item back and sell it again. Verify that buying it back reverses its
   sale refund exactly, and retains its stack, grade and custom details.
3. With an open merchant interaction, move outside the three-metre range and
   attempt a sale where the client permits it. Verify that no items or money
   change.
4. After a normal save, relog and confirm the wallet and retained inventory
   reflect the sale, with no sold item reappearing from its old database row.
