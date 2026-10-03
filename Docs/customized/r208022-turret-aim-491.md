# r208022 turret aim, issue #491

The server now retains valid turret aim and sends it to nearby characters.
Initial and late visibility use the same retained state.
Previously, `CSTurretStatePacket` only wrote a debug log.
Both outgoing posture paths always sent zero pitch and yaw.

## Exact client evidence

The client history file identifies version `208022`.
Research used the retained runtime dump at
`/home/kegan/archeage/.tools-re/dumps/x2game.dumped.dll`.
No new runtime capture or human test supports this change yet.

| Property | Value |
| --- | --- |
| SHA-256 | `a1995455440cdeb6356682e801f322cd946f3de3ad6595fae818d7a139100ae0` |
| Size | `30085120` bytes |
| PE image base | `0x38ff0000` |
| PE timestamp | `1413265461` |
| Entry RVA | `0x8c5d6d` |
| Image size | `0x1cb0a00` |
| Source base | `244d1726e` |
| Server compact SHA-256 | `636ca9ecfe777bc86542e4b828f930c7861d2b4639b1bcfff670c064fee9c1ac` |

Task evidence is in
`/home/kegan/archeage/.tools-re/vehicles-486-491-20261002/research/turret/`.
`run-native.sh` and `DecompileTurretAim.java` export the functions below.
The isolated Ghidra project is `/tmp/aaemu-turret-491-project-20261002/peace.gpr`.
`native-identity.json` records independent PE reads of the float constants.
The large client dump and decompiler files remain local research files.

## C2G contract

`CSTurretState` uses level 1 and opcode `0x036`.
Its body contains exactly `11` bytes.

| Offset | Type | Meaning |
| --- | --- | --- |
| 0 | u24, little-endian | Unit object ID |
| 3 | f32 | Turret pitch, radians |
| 7 | f32 | Turret yaw, radians |

The object ID is a fixed 3-byte field, not a variable-length integer.
For object `0x123456`, pitch `0.25`, and yaw `-0.125`, the body is:

```text
56 34 12 00 00 80 3e 00 00 00 be
```

| Address | Confirmed role |
| --- | --- |
| `39356990` | Constructs opcode `0x036`, copies the unit ID and angles, then sends the packet |
| `397ab8c0` | Stores unit ID, pitch, then yaw in the native packet |
| `399cffe0 + 8` | Serializer pointer `397c7640` |
| `397c7640` | Serializes `unitId`, `pitch`, then `yaw` |
| `397b27d0` | Serializes the object ID with a 3-byte transfer |
| `39425490` | Produces mouse aim and clamps both angles to authored limits |
| `39425580` | Produces installed-turret keyboard aim |
| `3922aed0` | Inclusive minimum/maximum clamp |

The client limits sends to intervals of at least `200` milliseconds.
The server does not add a new timer or angular speed rule.
The server rejects truncated bodies and trailing bytes before it reads any field.

## Angle limits and initial state

The native loader at `39736bd0` reads `vehicle_models`.
Its copy function at `39703ff0` preserves the authored values.
The `models` table maps the unit model ID to the vehicle model row.

| Field | Native offset | Unit |
| --- | --- | --- |
| `installed_turret` | `0x2c` | Boolean |
| `turret_pitch_angle_max` | `0x58` | Degrees |
| `turret_pitch_angle_min` | `0x5c` | Degrees |
| `turret_pitch_angvel` | `0x60` | Degrees per second |
| `turret_yaw_angle_max` | `0x64` | Degrees |
| `turret_yaw_angle_min` | `0x68` | Degrees |
| `turret_yaw_angvel` | `0x6c` | Degrees per second |

Both producers multiply authored angle limits by the float at `3999cf98`.
Its bytes are `35 fa 8e 3c`, which represent `0.01745329238474369`.
The server uses this exact float for the conversion to radians.
It checks finite values and ordered limits before it accepts aim.
It rejects invalid requests without changing or clamping their values.

Mouse aim uses the inclusive authored yaw interval for every vehicle model.
Keyboard aim applies only when `installed_turret` is true.
Keyboard aim clamps yaw when `yawMax < yawMin + 360`.
For a full-circle span, keyboard aim uses these sequential operations:

```text
if (yaw >= pi) yaw -= tau;
if (yaw <= -pi) yaw += tau;
```

The keyboard result is in `(-pi, pi]` during normal frame updates.
The mouse path can also produce `-pi` or values in a wider authored interval.
The packet has no field that identifies the input path.
The server accepts the union of the two valid intervals and preserves the supplied value.
It does not normalize a valid mouse value to the keyboard interval.
Model `35` has authored yaw limits of `-360` and `360` degrees.
The implementation does not replace those limits with `-180` and `180`.

The native numeric reader is `3953a310`, which calls `3952a8c0`.
For text values, `3952a8c0` calls the numeric parser at `395153c0`.
That parser starts at zero and reads a numeric prefix.
Text `f` contains no numeric prefix and produces zero.
Some authored `turret_yaw_angle_max` cells contain this text.
`SQLiteWrapperReader.GetFloat` uses the same SQLite numeric conversion.
The compact test passes all 63 rows through the production turret settings reader.

The constructor at `390ec4e0` initializes native yaw and pitch fields to zero.
The fields are at model offsets `0x2cc` and `0x2d0`.
`39430ac0` also clears the local turret input angles during world startup.
Each new server unit starts with this confirmed neutral state.
Of the 63 compact models, 62 permit neutral aim in their authored input limits.
Model `8000001` fixes pitch at `-60` degrees and yaw at `360` degrees.
Its initial native state remains zero until an input update applies the authored limits.
The server preserves that distinction between initial state and accepted input.

## Operator authority

The server resolves the requested vehicle only inside the actor's active world.
Both the actor and vehicle must be alive in the same world and instance.
The vehicle must remain registered and must not have retired its attachments.
The actor's transform parent and attachment point must match the vehicle's exact seat record.
The seat record must contain the same character object.
The server takes the actor attachment lock before the vehicle attachment lock.
This is the same order as `BindSlave` and `UnbindSlave`.

The native installed-turret caller at `394332d0` reads the actor's bound unit and attachment point.
It invokes aim before it resolves the seat-specific skill through `394257f0`.
Its condition permits non-Driver seats.
The server does not add an unsupported Driver-only rule.
An attached ship cannon is a separate vehicle unit.
An attachment to its parent ship does not authorize aim on the cannon.
Server-owned route transfers are not player-operated vehicles and do not accept this request.

## G2C contract and state

`SCUnitModelPostureChanged` uses level 1 and opcode `0x103`.
Turret posture has kind `8`, the loot boolean, pitch, then yaw.
Its changed-state body contains `13` bytes:

```text
56 34 12 08 00 00 00 80 3e 00 00 00 be
```

| Address | Confirmed role |
| --- | --- |
| `391aef60` | Factory for opcode `0x103` |
| `399b37bc + 8` | Parser pointer `397c6730` |
| `397c6730` | Reads the unit ID and shared model posture |
| `397bda20` | Reads the posture kind and loot boolean, then dispatches kind `8` |
| `397b1df0` | Reads pitch, then yaw as floats |
| `397c9480`, call at `397c96b6` | Initial unit-state parser uses the same posture reader |
| `391da0f0` | `OnUnitModelPostureChanged` resolves the unit and invokes unit slot `0x20` |
| `390b3680` | Passes the posture to the model slot `0xd0` |
| `390c3e10` | Copies changed posture and invokes model slot `0x270` |
| `390bdec0` | Passes kind `8` pitch and yaw to model slot `0x254` |
| `390d6a10` | Stores pitch and yaw in the vehicle model |
| `390d14b0` | Uses yaw for `bone_yaw` and pitch for `bone_pitch` |

Vehicle vtable `399a5ba8` confirms the three model slots above.
The initial unit setup at `390b96cf` also passes posture to model slot `0xd0`.
The visual consumer uses the angles in trigonometric calculations without a degree conversion.

The server retains the two angles in one immutable state object.
An atomic reference read gives each packet a complete pair.
`Unit.ModelPosture` reads that state for both outgoing packet paths.
An accepted change broadcasts `SCUnitModelPostureChangedPacket` from the vehicle.
A duplicate value sends no extra packet.
`Slave.AddVisibleObject` sends `SCUnitStatePacket` with the retained angles to a later observer.
Aim is transient unit state and does not add a database field.

## Tests and human checks

`TurretAimTests` covers exact body size, malformed lengths, non-finite values, authored limits, and native yaw boundaries.
It also covers compact numeric conversion, both seat records, world authority, retirement, observer updates, and initial visibility.
The tests use the real packet handler, production settings reader, and normal broadcast path.
The focused Release run passed all 19 cases with 0 failures and 0 skips.

Human validation remains necessary:

1. Operate a ship cannon and change its horizontal and vertical aim.
2. Check the visible cannon direction from another character.
3. Move the observer outside visibility and return while the cannon retains a nonzero aim.
4. Check an installed turret at both pitch limits and across the full-circle yaw boundary.
5. Leave the seat and make sure that the next operator can change the aim.

These changes address turret posture.
They do not change projectile trajectory, damage, movement, or cannon skill authorization.
