# Quest starter geometry findings

The wider review restores the starter volume for quest 578, Burnt Castle Jailbreak.
Its start component is 2319, act 6736, and accept-sphere detail 100.
The detail uses sphere 149.
SphereQuest detail 149 maps that sphere to quest 578.
The start requirements need level 28 and mother faction 148.

## The supported volume

The exact client file is `game/worlds/main_world/zone/148/mission_mission0.xml`.
Its SHA-256 is `d2b68cd2978a5b0df763a398dd7b26491fba76525ede65ebafcdfd387e931329`.
Entity GUID `477765EE9CF2FD25` has local position `(2184.9167,2294.2473,148.44249)`.
It has a zero sphere-center offset and radius 5 m.
Its Group16 `value1` is 149 and `value2` is 0.
The file repeats this entity, so the supplement contains one volume.

The exact world XML gives the source zone origin as `(13,10)` cells.
Each cell is 1,024 m wide.
The resulting world center is `(15496.9167,12534.2473,148.44249)` in `main_world`.
The current sector uses zone key 257.
The supplement retains the source radius of 5 m.

Current NPC 2445 has a deployed position at `(15496.29,12533.41,147.98663)`.
Its distance from the sphere center is approximately 1.141 m.
The sphere name also refers to the prison near this captive.
These independent data links support the authored starter volume.
The Group16-to-sphere mapping is a data inference, not a recovered native enum definition.

The tests load the supplement through the production manager with the sphere-to-quest mapping.
They check the acceptor ID, entry event, exact boundary, and a point outside the boundary.
They also check that the current NPC position lies inside the 3-dimensional sphere.

## Other starter gaps

The compact contains 454 loaded AcceptSphere acts across 430 components.
Client hint geometry covers 10 of those components.
The new quest 578 supplement covers one more component.
The other 419 components need further availability and geometry checks.
This count does not establish that all 419 components belong to playable quests.

The missing set includes 376 exploration components in category 79.
Of these, 375 have no same-quest client hint.
Quest 5170 has an interaction hint on another component.
The source does not identify that hint as its starter volume.

The review checked 5,439 other world XML, G, and TXT files.
It found 740 Group16 AreaSphere records and 170 Group16 AreaShape records.
The checked legacy records contain no matching volume for the exploration starter IDs.

The following findings need further work:

- Quest 41, sphere 101, has 14 legacy spheres with radius 5 m. Current placement evidence does not establish all 14 locations.
- Quest 340, sphere 120, uses a 10-point polygon. An exact result needs polygon support.
- Quest 815, sphere 211, has a legacy point approximately 1,351 m from the current quest objects.
- Quest 1858, sphere 204, has a legacy point approximately 2,050 m from its current report NPC.
- 14 missing starters have same-quest hints on other components. None has an explicit sphere-ID link from the hinted act.

[Issue 536](https://github.com/KeganHollern/aaemu-cluster/issues/536) records the further geometry work and acceptance criteria.
No guessed locations or polygon approximations enter the current supplements.
