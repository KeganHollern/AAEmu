# Housing collision with living actors

`CryActorGeometry.Create` uses the selected `GameStance` and uniform actor scale.
The caller supplies the actor position as the instance transform.
The collider stays upright, along the world Z axis.

The native dimensions are:

| Shape property | Authored value |
| --- | --- |
| Radius | `size_x * scale` |
| Cylinder half-height | `size_z * scale` |
| Center Z, relative to the entity | `(height_collider - height_pivot) * scale` |
| Shape type | `use_capsule` selects capsule or cylinder |

Capsule endcaps add one radius above and below the cylinder.
`size_y`, `model_offset`, and `view_offset` do not change this collider.
The caller selects the active stance. It must not substitute `ActorModel.Radius` or `ActorModel.Height`.

## Exact client evidence

The r208022 X2Game function `390cb440` writes scaled stance dimensions to the actor script table.
The direct data path `3911b950` writes the same dimensions from `game_stances`.
`39115820` writes the selected stance into `pe_player_dimensions`.

CryPhysics `3502f5d0`, `CLivingEntity::SetParams`, handles dimensions when the parameter type is `1`.
At `3502ff52`, it copies `sizeCollider.x` into the radius field.
At `3502ff62`, it copies `sizeCollider.z` into the cylinder half-height field.
At `3503002c` and `3503003a`, the capsule flag selects `35146e40` or `3514a140`.
At `3503005c`, it subtracts `heightPivot` from `heightCollider` for the part offset.
The capsule constructor adds the radius to the half-height for its bounds.

The original `cryphysics.dll` SHA-256 is
`f9c52c405c60c3bb8da9ebf9a45c34bae8149c9e0b31a7e9260f2eb11822f6e2`.
The unpacked analysis image SHA-256 is
`f28f160a0f9491ae6f8b38b8ca2fbf2e7c69921acadcc7dfead4e44e41d2720a`.
The evidence is in `.tools-re/housing-20260912/limits-farms/native-living*.log` and the corresponding native functions.

Decoration overlap uses entity mask `0x1f`, which includes living actors (`8`).
It supplies no skipped entities. This includes the source player.
The decoration ray mask is `7`, which excludes living actors.
House construction has a separate unit filter and must use its own confirmed rules.
