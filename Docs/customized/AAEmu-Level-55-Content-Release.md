# Levels 51–55 and level-55 skills

This release enables the existing r208022 levels 51–55 and level-55 skill-tree content without replacing or reauthoring client assets. Levels 51–55, their experience thresholds, skill points, passive trees, icons, effects, and plot graphs are already present in the pristine r208022 compact. The level-55 portion of the unified Content Studio project changes only the ten root skills from hidden to visible, while release checks prove the progression and skill graphs remain intact.

## Unified Content Studio project

- Canonical project: `Content/projects/custom/project.json`
- Active local configuration: `Content/content-studio.json`
- Portable configuration template: `Content/content-studio.example.json`
- Baseline: `r208022`
- Complete output artifact: `.content-studio/build/compact.custom.sqlite3`
- Build report: `.content-studio/build/content-build-report.md`
- Audit queries: `.content-studio/build/content-build-audit.sql`

There is no separate level-55 project, registry, configuration, or artifact. The ten level-55 `record` plans and seven `assertion` plans live beside recipes and all other custom work under `Content/projects/custom/`. Every build therefore contains the complete intended release and is the only artifact that should be published to either target.

The level-55 release contains ten sparse `record` manifests. Each manifest changes only `skills.show` from `f` to `t`; it does not copy or replace any skill row.

| Skillset | ID | Enabled skill |
|---|---:|---|
| Battlerage | 23587 | Behind Enemy Lines |
| Witchcraft | 23588 | Fiend's Knell |
| Defense | 23589 | Fortress |
| Auramancy | 23934 | Mirror Warp |
| Occultism | 23591 | Death's Vengeance |
| Archery | 23592 | Snipe |
| Sorcery | 23593 | Gods' Whip |
| Shadowplay | 23594 | Throw Dagger |
| Songcraft | 23595 | [Perform] Grief's Cadence |
| Vitalism | 23596 | Whirlwind's Blessing |

The internal Gods' Whip stages 23646–23649 remain hidden and non-learnable.

## Levels 51–55 progression

The server already uses a player cap of 55 in `ExperienceManager`. It loads the complete ordered `levels` table from the unified compact, derives character and active-skillset levels from total experience, caps both at level 55, advertises that cap to the client through the feature set, and persists character and skillset experience normally.

The unified release verifies these r208022 totals before a build can succeed:

| Level | Total character XP | Total mate XP in source table | Total skill points |
|---:|---:|---:|---:|
| 51 | 8,082,000 | 2,146,250 | 24 |
| 52 | 16,698,960 | 2,276,300 | 25 |
| 53 | 36,282,960 | 2,411,500 | 26 |
| 54 | 80,346,960 | 2,551,950 | 27 |
| 55 | 179,307,360 | 2,697,750 | 28 |

Mate progression remains intentionally capped at level 50 by the server; the mate totals are retained and checked because they are part of the source level rows. The requested player progression is fully enabled through level 55.

## Combo behavior

Fervent Healing and Gods' Whip use the same compact-defined combo mechanism. A Combo special effect identifies the next skill ID and opens a 1,000 ms window:

- Fervent Healing: 14929 → 14930 → 14931 → 14932 → 14933
- Gods' Whip: 23593 → 23646 → 23647 → 23648 → 23649

The server now records the exact next stage when a Combo effect executes. That stage can be cast once before the window expires. Hidden combo stages cannot be learned or cast directly, and a different stage cannot be substituted. This retains the client's normal chained-button behavior while making the server authoritative.

## Fiend's Knell

Fiend's Knell contains two `SpawnEffect` rows which summon mate templates 14165 and 14166. Both are aggressive, inherit the summoner's faction, target the summoner's combat target, and last 30 seconds. The server's previously empty mate-spawn branch now creates these as temporary skill summons. They coexist with the player's persistent mount or battle pet, are not written to the character database, and are removed when their lifetime ends.

## Release assertions

Content Studio will refuse to build the artifact unless all release checks pass:

1. Levels 51–55 have the exact r208022 experience, mate experience, and skill-point progression.
2. One visible, learnable level-55 root skill exists for each original skillset.
3. Gods' Whip's four internal stages remain hidden and non-learnable.
4. Fervent Healing and Gods' Whip retain all eight exact one-second combo transitions.
5. Each original skillset retains seven passive skills.
6. Every root skill has an icon and an effect, plot, or controller execution graph.
7. Fiend's Knell retains its two exact temporary mate summons.

Content Studio also verifies every declared modified value after compilation and reports in-place cell changes. The level-55 plans themselves modify only ten cells: `skills.show`, from `f` to `t`, for the ten entries listed above. The complete unified artifact also contains the recipes and any other plans currently saved in the canonical project, so its overall diff is expected to include those intentional changes too.

## Build and review

From the repository root:

```powershell
dotnet run --project .\Tools\AAEmu.ContentStudio.Cli\AAEmu.ContentStudio.Cli.csproj -- validate --config .\Content\content-studio.json
dotnet run --project .\Tools\AAEmu.ContentStudio.Cli\AAEmu.ContentStudio.Cli.csproj -- build --config .\Content\content-studio.json
dotnet run --project .\Tools\AAEmu.ContentStudio.Cli\AAEmu.ContentStudio.Cli.csproj -- diff --baseline .\.content-studio\baselines\r208022\compact.sqlite3 --artifact .\.content-studio\build\compact.custom.sqlite3
```

Publishing is intentionally separate because it replaces the configured server or client compact after making a restore copy. Review the unified change list and release checks, stop the affected process, and then publish the same `compact.custom.sqlite3` artifact to both targets through Content Studio.

## In-game acceptance checklist

- A level-50 character can gain experience and progress through levels 51–55.
- Skill points total 24, 25, 26, 27, and 28 at levels 51–55 respectively.
- Each selected skillset presents its level-55 root skill only at skillset level 55.
- Each root skill can be learned with an available skill point and cannot be learned early by packet request.
- Fervent Healing accepts four follow-up activations only inside each one-second window.
- Gods' Whip accepts stages 2–5 only in order and rejects direct hidden-stage requests.
- Fiend's Knell summons both fiends for 30 seconds without replacing a persistent mate.
- Fortress, Snipe, Throw Dagger, and Whirlwind's Blessing execute their compact plot graphs without server errors.
- Relogging preserves learned level-55 skills.
