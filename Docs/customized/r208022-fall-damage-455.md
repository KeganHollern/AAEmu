# r208022 fall damage (#455)

The server now derives impact speed from accepted movement heights and server elapsed time.
The optional client `actor.fallVel` number no longer sets damage.
The server keeps the current damage curve and lethal threshold.
This change does not make client positions or collision contact fully authoritative.

## Exact-client evidence

The retained native dump matches the r208022 research baseline:

- Packed `client/bin32/x2game.dll` SHA-256: `3821c34366ff8b6f7aed95ba53d12216a5c31cb7c3407703d28eea639a621205`.
- `.tools-re/dumps/x2game.dumped.dll` SHA-256: `a1995455440cdeb6356682e801f322cd946f3de3ad6595fae818d7a139100ae0`.
- PE image base: `0x38ff0000`. Image size: `0x01cb0a00`. Timestamp: `0x543cb835`.
- Server compact SHA-256: `636ca9ecfe777bc86542e4b828f930c7861d2b4639b1bcfff670c064fee9c1ac`.

`FUN_397c0000`, at the `actor.fallVel` reference `0x397c0839`, serializes the field only when actor flags contain `0x80`.
The field maps speed from 0 to 128 metres per second onto the complete unsigned 16-bit range.
The native encoder rounds to the nearest integer. The decoder multiplies by `128 / 65535`.

`FUN_39117590` records the initial height and peak downward speed.
`CActor::UpdateFallDamage`, `FUN_39116f00`, limits that peak speed to `sqrt(max(0, startZ - currentZ) * 40)`.
The confirmed constant at `0x3999f668` is `40.0f`.
The new observer uses this height limit and the native field scale.
It does not infer a gravity setting from a modern client.

The audit's glider claim needs correction.
The exact compact contains 185 `fall_damage_immune` buffs and 55 `gliding` buffs.
These groups do not overlap.
The server now checks active authored immunity before damage or stun.
Active gliding controls movement history separately. It does not grant a new damage-immunity flag.

The native evidence does not establish the server damage thresholds `8600`, `15000`, and `32000`.
These values remain the current project damage rule.
They are encoded speed values, not direct metres per second.

Local research records are under `.tools-re/combat-movement-20260926/fall/`.
`native-initial.log`, `native-fall.log`, and `native-history.log` contain the native decompilation.

## Server behavior and limits

`CSMoveUnitPacket` observes movement only after owner checks, movement acceptance, and world-position updates.
The observer keeps the peak descent speed from accepted heights and a monotonic server clock.
It limits this speed with the native height bound.
Packet bursts cannot turn a short fall into a lethal fall.
Missing or exaggerated client speed numbers do not change that bound.

Terrain contact can finish a fall without the optional client report.
The terrain tolerance covers 2 position-encoding rounding errors. It is not a new collision radius.
Continuous terrain contact does not accumulate damage on slopes or stairs.
A reported landing can complete a sparse grounded-to-grounded fall.
A stationary airborne packet retains fall history.
Duplicate landing reports cannot apply the same fall twice.

The server does not have an actor contact query for every roof, brush, house, or doodad.
On those surfaces, the native landing report still supplies the contact event.
Its number never supplies damage.
An omitted report on unsupported geometry can delay damage.
A forged contact event can split a fall into shorter falls.
Sparse or delayed position samples can also underestimate the peak speed.
This change does not claim to solve those contact and movement-authority limits.

A follow-up must add actor support/contact queries for static geometry and current world objects.
It must define contact tolerances from native actor physics and check accepted vertical trajectories.
It must also check packet timing, sparse falls, forged contact events, and roofs without the native contact report.
This work belongs with the broader movement-authority work in #131.

Water, active gliding, authored immunity, server skill controllers, current impulses, and attachments clear fall history.
Teleport position locks, world changes, parent changes, direct transform transfers, death, and despawn also clear it.
Glider and immunity start/end changes clear history even when no movement packet arrives between them.
A new fall after glider removal can cause damage.
Mount movement uses the same observer. Rider movement does not create a second independent fall.
A lethal mount impact reaches riders after the mount's death cleanup, with each rider's own immunity checks.

The damage calculation no longer heals a unit for small positive speeds.
A unit with fewer than 20 maximum HP cannot divide by zero in the stun calculation.
Damage uses `KillReason.Fall`. The environment packet reports the HP loss, not damage that the HP floor prevented.
Normal falls still leave 5 percent HP, with a minimum of 1 HP.
The current lethal threshold still applies to server-derived impacts.
The current GM permission still prevents fall damage.

## Automated checks

`FallMovementTests` covers native encoding, omitted reports, sparse landings, slopes, stairs, jump apex pauses,
ledge landings, same-time samples, packet bursts, slow descent, duplicate reports, and resets.
`FallDamageTests` covers the damage curve, low HP, lethal damage, immunity, water, gliders, attachments,
teleport locks, world changes, death, and terrain contact through the unit entry point.
`UnitMoveFallPacketTests` checks the optional field boundary and a truncated field.
The focused filter passed 53 tests. The complete unit suite passed 4,432 tests and skipped 21 optional tests.
The optional checks need external client files or an explicit compact path.
The tests do not claim human gameplay validation.

## HUMAN VALIDATION (#573)

All checks below remain pending. Use the published release for gameplay checks.

- [ ] **HV-455-1.** Use a character without `IgnoreFallDamage` permission. Test a small jump, an ordinary damaging fall, slopes, and stairs. Check HP and fall stun. Do not use a lethal fall for the first check.
- [ ] **HV-455-2.** Use a glider and a safe water landing area. Check a normal glider landing and a water landing. Check a new fall after glider removal. The first 2 landings must cause no fall damage. The new fall must use its own descent.
- [ ] **HV-455-3.** Use a mount and a rider. Compare a small drop with a damaging drop. Check mount HP and rider HP. A lethal mount/rider check needs disposable test characters and controlled test conditions.
- [ ] **HV-455-4.** Use a known roof or house landing, a portal, and a recall point. Check a roof landing, then travel and take a small jump. Check that old fall height does not cause damage after travel.
- [ ] **HV-455-5.** Use controlled packet tooling on a test character. Omit `actor.fallVel` on a sampled fall onto loaded terrain. Then send the maximum numeric field for a short observed drop. Check that the first fall still causes damage and the short drop does not become lethal. This check does not test forged contact events on unsupported geometry.
