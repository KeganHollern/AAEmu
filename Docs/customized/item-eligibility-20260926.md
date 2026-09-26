# Item eligibility and appearance rules

This change addresses cluster issues #466, #467, and #472. The source base is
`b11df2e729055d61098d0660f153882b204c7876`.

## Data and scope

The read-only r208022 server compact has SHA-256
`636ca9ecfe777bc86542e4b828f930c7861d2b4639b1bcfff670c064fee9c1ac`.

The following facts come from that compact and the current server data loader:

- `items.grade_enchantable` permits regrading on 4,564 equipment templates.
  Another 3,685 equipment templates are gradable but do not permit regrading.
- `item_look_convert_required_items` defines 27 conversion groups. Holdable and
  wearable links select the conversion group for each loaded template.
- `dyeable_items` contains 37 targets. A default dye value of zero does not
  replace the membership check.
- Dye skill 22727 has no plot and costs zero labor.
- Gender value 0 means unrestricted. Values 1 and 2 match male and female.

No compact data, SQL schema, or client packet body changes. The existing
`CSConvertItemLook` reader still reads the target and image instance IDs as
2 unsigned 64-bit fields. This change validates their server-side meaning.
`CSChangeItemLook` remains outside the explicit acceptance scope of #472.
Its unconfirmed fields are not assigned new meanings.

## Changes

New equipment moves enforce the minimum level, maximum level when present,
and gender requirement. Rejection sends the corresponding equipment error
before the move or bind operation. Empty slots and NPC equipment keep their
current behavior.

Saved equipment uses the same explicit restoration policy as saved mate
gear. Startup preserves the saved slot, flags, owner, and details. It does not
perform a new equip operation. An invalid saved slot stops startup before a
later save can remove its container link. This policy does not permit a new
invalid equip after login.

Regrading loads and checks `GradeEnchantable` and `Gradable`. The current
safe scroll-type switch still rejects weapon, armor, and accessory mismatches.
The shared settlement restores labor, scrolls, charms, money, and equipment
when the action fails.

Appearance conversion requires 2 different owned equipment instances with
the same loaded conversion group. It requires the authored converter and
its complete material count. It rejects reserved inputs. Powder, image
consumption, and appearance changes use one inventory mutation. A failed
material debit restores all earlier changes before it sends success events.

Dyeing checks the target against `dyeable_items` and rejects non-equipment,
reserved, or equipped targets. It checks the dye source against the current
skill. Zero-labor dye skills now use the shared settlement. The dye count and
new color commit together. The color marks the item dirty and survives the
normal item-details serialization. The client still receives the Dyeing task
type after success.

## Automated checks

- The Release build completed with no errors.
- All 27 `ItemEligibilityTests` passed. These tests cover equip boundaries,
  gender, saved gear, regrade rejection, invalid appearance pairs, partial
  powder failure, exact material consumption, dye rejection, failed commits,
  and zero-labor dye execution through `Skill.ApplyEffects`.
- All 62 `InventoryMutationTests` passed, including valid regrade settlement.
- All 33 `MountSkillAuthorizationTests` passed, including saved mate gear.
- No live data or compact data changed during these tests.

## Human validation

These checks remain pending in cluster issue #573, HUMAN VALIDATION.
Use spare items for actions that consume materials.

- For #466, try gear above the character's level and a costume for the other
  gender. Confirm a clear rejection and unchanged inventory. Equip permitted
  gear, reconnect, and confirm its saved slot and details.
- For #467, try a non-enchantable item and a scroll for another equipment type.
  Confirm no loss of the scroll, charm, money, or labor. Regrade one permitted
  spare item. Confirm one payment and the displayed result after reconnect.
- For #472, try mismatched appearance categories and an insufficient powder
  count. Confirm unchanged items and material counts. Convert one permitted
  pair, then reconnect and confirm the appearance and exact material cost.
- For #472, try a non-dyeable target. Confirm that the dye remains. Dye one
  permitted item in the bag. Confirm that one dye is consumed and the color
  remains after reconnect.
