# Skill range for objects and vehicles, issue #456

`Skill.Use` now applies maximum range to every target type.
The normal skill path and triggered labor path share the range calculation.
Distant object interactions stop before skill packets, mana, labor, or effects.
Weapon range, skill range modifiers, minimum range, and the current Remove Stone data correction remain active.

## Geometry

Ordinary objects retain their full 3D distance and caster model radius.
Ships use the nearest point of their oriented `ship_models` mass box.
This is the current server hull model, also used by `ShipController`.
The calculation includes rotation, vertical separation, scale, mass-center offset, and caster radius.
It does not use a large sphere around the ship pivot.

A fixed vehicle attachment uses its authored server attachment position in the vehicle's current world transform.
This includes ship rotation and scale.
Free-placed doodads retain their own position.

The server's attachment catalogue is incomplete.
The compact has 576 slave doodad bindings, of which 174 lack a matching server attachment entry.
It has 81 healing point bindings, of which 24 lack such an entry.
For a missing ship attachment, the range check uses the bounded owning hull.
It does not use the repair doodad's uninitialized world origin or restore the old unlimited range.
A future attachment catalogue correction can make those checks precise to each point.
Non-ship models retain the current model-radius or point calculation.

The mass box is a server approximation for skill distance, not a claim of exact native visual-mesh distance.
This change does not alter ship physics, attachment placement, or client assets.

## Evidence and tests

The source base is `ca6d15ad4ead116eecc2c479b7849a8b4f9fddf4`.
The server compact SHA-256 is `636ca9ecfe777bc86542e4b828f930c7861d2b4639b1bcfff670c064fee9c1ac`.
`SlaveManager.ApplyAttachPointLocation` and `Transform.GetWorldPosition` establish the local attachment frame and scale.
`ShipController.Build` establishes the mass-box axis mapping and center.
The client compact names the same ship models as the server compact.
Read-only client prefab inspection did not supply the missing attachment catalogue.
No SQL, compact, packet, or client-content change is needed.
This fix remains downstream until a separate upstream submission receives authorization.

The Release unit-test build passed.
All 15 `SkillRangeTests` passed.
They cover boundary distances, 3D separation, rotation, scale, hull corners, repair points, missing attachment data, weapon range, and skill modifiers.
The `SkillLaborTests` suite passed, including 2 new public `Skill.Use` cases for remote doodads and vehicles.
Those cases check that rejected interactions do not spend labor, mana, or materials, and do not start a cast.
All 46 `PeaceProtectionTests` also passed.
The combined release tests follow integration with the other combat fixes.

## Human validation

These checks remain pending and belong in HUMAN VALIDATION #573.

- [ ] **HV456-1.** Mine, harvest, and use a quest object nearby. Check rewards and normal labor use.
- [ ] **HV456-2.** Try the same interaction outside its range. Check that it gives no reward and spends no labor or materials.
- [ ] **HV456-3.** Repair a boat near a repair point away from the ship origin. Repeat after the boat turns and moves.
- [ ] **HV456-4.** Check that a distant ship or repair point cannot accept the skill. Repeat from well above the ship.
