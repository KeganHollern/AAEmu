# Stablemaster pet treatment for r208022

This change covers the pet treatment part of cluster issue #315.
Vehicle repair, vehicle equipment, names, customization, and target controls remain separate work.

## Client contract

The client confirmation sends `CSRepairPetItems`, opcode `0xb5`.
Its body contains one unsigned little-endian 24-bit NPC object ID.
It contains no item list, price, or recovery amount.
Native producer `39440d80` checks the selected NPC's stablemaster capability.
Serializer `397c79d0` writes the 3-byte body.
The normal interaction request records the selected NPC before the confirmation.

Native quote function `39447a40` uses eligible injured summon items from the bag and bank.
It excludes temporary interface slot reservations. It does not exclude a pet because that pet is summoned.
The price uses the raw level byte from the item's 6-byte detail data.
The other detail fields are signed 32-bit experience and a 1-byte injury flag.

The client calculates each item's price separately:

```csharp
var power = (float)Math.Pow(level, (double)2.3f);
var subtotal = level * 0.5f + power;
var copper = (int)MathF.Floor(subtotal + 0.5f);
```

The total is the sum of those rounded prices.
For example, levels 1, 10, and 50 cost 2, 205, and 8109 copper.
Float conversions affect the result. The regression test contains all 256 level-byte results.

The stock skill and buff descriptions confirm injury removal through stablemaster treatment.
They do not define the HP or MP result. The confirmation and result packets do not define that result either.
The candidate preserves current HP and MP, with a minimum of 1 HP for old dead records.
The user decision on that proposed server rule remains pending. It is not a confirmed retail rule.

## Server behavior

The service needs a living character and the current stablemaster interaction within the common 5-metre service range.
The 5-metre limit is the current server service rule, not a newly proven client limit.
The server checks the current NPC object, world, instance, and stablemaster capability.
It finds the character's injured summon items in the bag and bank and excludes trade reservations.
It checks each item's owner, container, slot, and identity before treatment.

The payment, item injury flags, and saved pet state use one existing economy transaction.
A known database rollback restores the wallet and pet state.
An uncertain commit retains prepared state and uses the existing consistency-failure path.
Success notifications follow a known successful commit.
Treatment clears the active pet's injury and downed state as well as its item flag.

The first inventory snapshot copies each owned pet's saved level and experience into its summon item.
Spawn also copies those fields before the normal item update.
Previously, only an experience gain copied these fields.
A new pet could retain item level 0, which made the client quote 0 and refuse to open the treatment confirmation.

## Evidence and validation

The research used the exact r208022 DLL and runtime dump recorded in
[the NPC and quest audit](npc-interactions-and-quest-audit-r208022.md#input-identity).
The detailed native record remains in the cluster workspace at `.tools-re/npc-services-315-530-133-20261003/research-315/contract.md`.
Its price vectors, disassembly, input hashes, and stock content queries remain beside that record.

Automated checks cover the request body, prices, service authority, ownership, reservations, payment, and recovery state.
The MySQL checks reload the stored wallet, item detail, and pet records after success and injected SQL failures.
Human checks belong in cluster HUMAN VALIDATION issue #573 after release.
No static review or automated result counts as a human gameplay pass.
