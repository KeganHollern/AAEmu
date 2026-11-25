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

## Starter inventory on release 914

The offline audit joins each start act to its current quest and component.
The component identifies the client geometry. The compact also gives each act a sphere ID.
Game looks up a `SphereQuest` binding by quest ID when it registers a starter.
The start act then compares its own sphere ID with the acceptor ID.
The report records all candidates for that lookup.

| Geometry status | Components | Acts | Evidence |
| --- | ---: | ---: | --- |
| `single_binding` | 2 | 2 | Quest 49 has a client volume and sphere 5. Quest 578 has a source-backed supplement and sphere 149. Each quest has one matching sphere binding. |
| `component_only` | 9 | 33 | The client has volumes for each tutorial component. Each component uses 2 to 4 sphere IDs. The hint format omits the sphere ID. |
| `missing` | 419 | 419 | The client hints and Game supplements contain no volume for these components. Other components of the same quest have geometry in 13 cases. |
| Total | 430 | 454 | All 454 start acts have sphere metadata. |

For example, quest 2585 has 4 hints in zones 129, 179, 184 and 187.
The 4 sphere names refer to Gweonid, Solzreed, Falconry and Rainbow.
These area names support a geographical interpretation.
The compact includes no numeric link between a hint and an individual sphere.
Other tutorial quests have fewer hint volumes than sphere IDs.
The audit keeps the full set and gives no credit for an individual binding in these cases.

**World and access.** Of the 419 components with missing geometry, 415 have a quest zone in the main world.
The others belong to quests with a zone in Hadir Farm (2), Sal Temple (1), or Nachashgar (1).
For 14 of the 419 components, the compact marks the quest zone closed. These components span 5 closed zones.
The report records the zone from the quest context. The starter can lie in a different zone.
A closed quest zone alone cannot establish that the player cannot reach the starter.
The exploration category accounts for 376 missing components.

**Other ways to start a quest.** Four of the 419 quests have a separate reference in the compact.
Quest 41 has skill 10637 with accept-quest effect 13.
NPC 2003 names quest 4392 on combat engagement.
Item 28620 names quest 5491. Sphere 2236 has a `SphereAcceptQuest` list that names quest 5730.
Each start component still contains a single `QuestActConAcceptSphere` condition.
The starter boundary and successful player interaction still need independent evidence.
Quest 2895 uses sphere 1150, but the sphere binds to quest 5491.
The current quest lookup supplies no matching `SphereQuest` binding for 2895 or 5730.

**Current open-world evidence.** Quest 340 has a 10-point legacy polygon in zone 133.
The source repeats the same entity GUID and sets the area height to zero.
The 3-dimensional sphere test cannot represent that boundary.
The later centers for quests 815 and 1858 lie within 3 m of current quest objects.
Their exact r208022 boundaries and trigger behavior still need evidence.
The 14 legacy locations for quest 41 still lack confirmation.
These cases remain open. [Source comparison](https://github.com/KeganHollern/aaemu-cluster/issues/536#issuecomment-7123864645).

The run used Game `631604d85cf1ad11cacccb64e55be70262ca1600` as the source baseline.
The server compact SHA-256 was `636ca9ecfe777bc86542e4b828f930c7861d2b4639b1bcfff670c064fee9c1ac`.
The 69 client hint files and 18 world definitions matched byte for byte between the pristine and installed r208022 archives.
Their aggregate SHA-256 values were `687bef70ed4ba11a92a378cc43d01e65d61e644d5af97965a6767fec17ee0390` and `78479b57d0786227095a532a934416e24a2809877d538bdc9cfbed6cfef68bae`, respectively.
