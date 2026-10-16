# Independent weapon timing (#580)

Player mainhand and offhand attacks now have independent due times. NPC basic
attacks use the confirmed native weapon interval. This completes the bounded
follow-up to [weapon and animation timing #577](r208022-attack-timing-577.md).

## Native evidence

The r208022 unpacked image has SHA-256
`a1995455440cdeb6356682e801f322cd946f3de3ad6595fae818d7a139100ae0` and
image base `0x38ff0000`. Addresses below are virtual addresses at that base.
The original packed image identity is recorded in the #577 document.

| Address | Evidence |
| --- | --- |
| `0x398b49e0` | Player interval reads the selected holdable. Offhand slot 16 doubles its base interval before raw speed conversion. |
| `0x398b4330`, `0x398b43d0` | NPC intervals use the common fist holdable and melee/ranged raw attributes. NPC offhand does not double its interval. |
| `0x398aa290`, `0x394bdc10` | Convert interval attributes 36, 41, and 46 to seconds for the client display. |
| `0x391d57f0` to `0x3939a810` | Skill-start packet handler sets the autoattack state. It does not maintain repeat deadlines. |
| `0x391d5c70` to `0x39398410` | Skill-stop packet handler clears autoattack state. |
| `0x393989e0`, `0x393be4e0` | Autoattack request checks the target and sends the request through the game interface. |
| `0x394b6a80` | The NPC-stop-autoattack binding reports that this operation is unsupported by the client. |

Independent disassembly checked the NPC division because the decompiler aliased
its raw-bonus local incorrectly. The confirmed conversion is
`baseMilliseconds * 1000 / (1000 + max(-999, rawBonus))`, with integer truncation.
Current attribute limits and the existing positive server interval floor still apply.

Both compacts define holdable 0 as `fist`, code `fst_n_n`, with a 1500 ms interval.
The native common-fist global is 0 in the checked image. No literal write to that
global was found. Runtime mutation of the global was not tested.

The client proves interval values and packet lifecycle. It does not prove the
retail server's first-hit, pause, equipment-change, or NPC repeat policy. The
scheduling choices below extend the current server rules.

## Player scheduling

One task owns both hands. It schedules the earliest due hand through the current
TaskManager queue lock. Each hand consumes only its own due attempt. Each shot
keeps a separate Skill instance, target, timeline, and animation counter.

The initial client request still starts its selected attack. Repeated mainhand
attacks follow the mainhand interval. A newly enabled offhand starts its own
full interval. Shields and an empty offhand do not add an offhand cycle.
Weapon and speed changes retain the start of each current cycle. Values refresh
at task callbacks, as in #577. They do not add immediate buff-change callbacks.

Casting, GCD, relation, and range checks still gate each hand. A blocked due
attempt is skipped. Late callbacks perform at most one attempt per hand and
start the next cycle from that callback. Missed attacks never accumulate.
Target changes preserve timing and use the current target on the next callback.
Each hand uses its own equipped weapon range.

Mode changes retire the old task before starting the new one. Explicit stop,
death, and rejected scheduling clear only the task that the character still
owns. A stale or overlapping callback cannot replace the current timer.

## NPC scheduling

Only standard basic skills 2, 3, and 4 use the new gate. `NpcAi` records each
accepted start and reads the current native interval before the next attempt.
Rejected attacks do not consume an interval. Combat lifecycle transitions clear
the old deadlines. Target changes do not clear them.

The generic attack path replaces its 1500-1550 ms random fallback for these
skills. Positive `BaseSkillDelay`, per-AI post-skill delays, existing GCD,
cooldowns, cast waits, and command-set timing remain independent gates.
The weapon interval is a minimum start-to-start interval, not a replacement for
those gates. Thus an authored delay or GCD can still limit a hasted NPC.

The server compact assigns skill 2 to 12,503 NPC templates and skill 4 to 4.
Other base skills retain their timing. An autoattack slot alone is not a safe
classifier: ordinary bow skill 10431 has no autoattack slot, while Shield Bash
11051 has a mainhand slot and a 15-second cooldown. This change does not infer
new rules for those skills or replace dungeon-specific behavior.

## Inputs and checks

- Client compact SHA-256: `4f1ac86b2ae79fd35886d0cd7b1e5cccc3287a011a200667d97eb1c5bc4d79a4`.
- Server compact SHA-256: `636ca9ecfe777bc86542e4b828f930c7861d2b4639b1bcfff670c064fee9c1ac`.
- Tests cover different hand speeds, shared deadlines, speed and equipment changes,
  shields, range, pause, mode changes, stale callbacks, delayed impacts, NPC
  acceptance and rejection, authored delays, and lifecycle reset.
- Real-client timing checks belong in [HUMAN VALIDATION #573](https://github.com/KeganHollern/aaemu-cluster/issues/573).

This is a downstream r208022 change. No SQL, compact, packet layout, or client
content change is needed. Human checks confirm this server's behavior. They do
not prove the original retail server's scheduling policy.
