# AAEmu Content Studio Guide

- Audience: content designers, server operators, developers, and automation agents
- Target: ArcheAge 1.2.4.13 (`r208022`)
- Status: implemented beginner-first release
- Last updated: August 13, 2026

## What this adds

AAEmu Content Studio turns custom compact-database work into a repeatable content build instead of a one-off database edit. It provides:

- A local visual designer at `http://127.0.0.1:5188`.
- A scriptable `aaemu-content` command-line tool.
- Plain-language game search across names, descriptions, abilities, and related content.
- Ability/skillset browsing with every skill in the selected ability.
- A complete entry viewer that exposes every main-record field, localization, and supported connected row.
- Guided **Change this entry** and **Make a new copy** actions that save reviewable plans instead of editing the database immediately.
- Automatic internal identity allocation with tombstone support; designers never need to see or enter database IDs.
- Guided recipe and complete workbench-graph cloning.
- Human-readable JSON manifests as the source of truth.
- Sparse modification manifests that store only values which differ from the pristine baseline.
- Read-only artifact assertions for release-wide requirements such as level progressions and complete skill chains.
- Baseline hash and schema verification.
- Transactional database compilation.
- SQLite integrity and AAEmu relationship validation.
- Entity and field change reports, build manifests, verification SQL, and cell-level database diffs.
- Backup-first deployment and explicit rollback.
- Server-side crafting checks for recipe IDs, counts, workbench identity, craft-pack membership, range, skills, and material revalidation.

The initial project includes a working example:

| Entity | Custom ID | Source |
| --- | ---: | ---: |
| `recipe.example-unstable-solution` | `9100000` | recipe `5545` |
| cloned craft skill | `9400000` | skill `14644` |
| `workbench.example-alchemy-workbench` | `9200000` | doodad `6387` |
| dedicated craft pack | `9300000` | source pack behavior `90` |

## The important compact.sqlite3 concept

`compact.sqlite3` is static game-definition data. AAEmu reads it when the game server starts, and the ArcheAge client reads its own copy. It describes items, skills, recipes, doodads, function graphs, localized text, and hundreds of other systems.

It is not the same database as AAEmu's MySQL databases:

| Store | Purpose | Normal mutability |
| --- | --- | --- |
| `compact.sqlite3` | Static definitions understood by client and server | Built offline; read-only at runtime |
| `aaemu_game` MySQL | Characters, inventories, housing, mail, and other live state | Read/write |
| `aaemu_login` MySQL | Accounts, bans, and login state | Read/write |

The pristine r208022 client compact has 635 tables but no primary keys, foreign keys, views, triggers, or useful relational enforcement. An `id` column therefore looks like a key but SQLite does not protect it. Content Studio supplies the missing checks.

The server and client need matching content changes, but they do not use interchangeable database files. AAEmu's server compact contains additional runtime-only tables. Build each artifact from its own approved baseline, preserve target-only data, and verify that both artifacts express the same shared rows. Content Studio refuses to replace a target whose schema differs from the artifact. Existing client assets can be reused; adding a completely new model, icon, or item asset still requires client asset work beyond the compact database.

## Why a workbench is a graph

A crafting workbench is not one row. The example alchemy table resolves through this graph:

```text
doodad_almighties 6387
  -> doodad_func_groups 16902 and 16903
     -> doodad_funcs 13830..13833
        -> doodad_func_craft_packs 129 and 130
           -> craft_packs 90
              -> craft_pack_crafts
                 -> crafts 5545
                    -> skill 14644
                    -> craft_materials
                    -> craft_products
```

The workbench wizard clones every doodad group, active function, phase function, and craft-pack payload with newly allocated IDs. Non-pack behavior such as recover-item or timer payloads remains linked to its known-good source behavior. The custom craft-pack functions are redirected to a new pack containing only the selected custom recipes.

## Repository layout

```text
Content/
  baselines/r208022.json              Exact supported baseline fingerprint
  schemas/*.schema.json               Manifest schemas
  content-studio.example.json         Shareable configuration example
  content-studio.json                 Local machine configuration; ignored
  projects/custom/
    project.json                      Project entry point
    id-registry.json                  Stable ID ownership
    records/*.json                    General entry change/copy plans
    assertions/*.json                 Read-only artifact requirements
    recipes/*.json                    Recipe source manifests
    workbenches/*.json                Workbench source manifests
    raw-sql/*.sql                     Expert-only escape hatch

Tools/
  AAEmu.ContentStudio.Core/           Shared compiler and validation engine
  AAEmu.ContentStudio.Cli/            Agent/developer CLI
  AAEmu.ContentStudio.Designer/       Local designer UI
  AAEmu.ContentStudio.Tests/          Synthetic end-to-end tests

.content-studio/                      Ignored local state
  baselines/r208022/compact.sqlite3   Pristine immutable source copy
  build/                              Built database and reports
  backups/                            Deployment backups
  designer-keys/                      Local Blazor data-protection keys
```

Never commit compact databases, client assets, `.content-studio/`, or `Content/content-studio.json`.

## Shared designer and agent workflow

The files under `Content/projects/custom/` are the single source of truth for both the GUI and automation agents. The GUI does not keep a separate private database or browser-only draft. This makes handoff work in both directions:

1. A designer saves a change in the guided GUI. The Studio writes the corresponding recipe, workbench, or record plan atomically under `Content/projects/custom/`.
2. An agent reads and modifies that same plan, preferably through the Content Studio Core services or CLI so schema validation and identity ownership remain intact.
3. **My changes** watches the project directory and refreshes automatically when an agent creates, changes, renames, or deletes a plan.
4. If an agent updates a plan while a designer already has it open, the editor shows **Newer shared work is available** and offers to load it.
5. Every guided save checks the version originally opened. If the file changed in the meantime, Content Studio refuses to overwrite the newer work and asks the designer to reload.

Agents should treat display names as the collaboration language and internal numeric values as serialization details. Do not ask a designer for an ID. Resolve the entry by name and surrounding context, preserve its stable `key`, and use the existing identity registry for any new internal rows. Write complete JSON through an atomic replace; do not leave half-written project files for the watcher to read.

Designers work only with display names, named enum choices, and contextual descriptions. Search results, relationship pickers, My Changes cards, and guided forms intentionally do not display database IDs. When a legacy relationship cannot yet be mapped to a verified name catalog, the GUI preserves it as **Kept from the source** instead of presenting a numeric input.

This is also the repository's only Content Studio project. Feature areas such as levels 51–55, skill-tree enablement, gear balance, recipes, and workbenches are grouped by namespaced plan filenames inside `Content/projects/custom/`; they do not receive separate feature projects. A build represents the complete intended change set for one compatible baseline. Client and server publication requires separate target-compatible artifacts, never copying one compact across their different schemas.

## One-time setup

The repository targets .NET 10. Verify it with:

```powershell
dotnet --list-sdks
```

Copy the original r208022 database to the protected local baseline location:

```powershell
New-Item -ItemType Directory -Force .content-studio/baselines/r208022
Copy-Item -LiteralPath "D:\path\to\original\game\db\compact.sqlite3" `
  -Destination ".content-studio/baselines/r208022/compact.sqlite3"
```

Copy `Content/content-studio.example.json` to the ignored `Content/content-studio.json`, then set the client target path. The checked-in example uses paths relative to the configuration file; the local configuration in this workspace is already set up.

Run the safety check:

```powershell
dotnet run --project Tools/AAEmu.ContentStudio.Cli -- `
  doctor --config Content/content-studio.json
```

A healthy setup ends with `PASS` and zero errors.

## Start the visual designer

From the repository root:

```powershell
Scripts\StartContentStudio.bat
```

Then open [http://127.0.0.1:5188](http://127.0.0.1:5188). The designer binds only to localhost by default.

The main screens are:

- **Overview:** a three-step Search, Change, Build introduction and current project status.
- **Game search:** plain-language discovery across abilities/skillsets, items, recipes, workbenches, NPCs, skills, buffs, quests, achievements, titles, and world content.
- **Recipe maker:** find a known-good recipe by name, clone it, and edit every supported recipe setting before saving.
- **Workbench maker:** find a crafting workbench by name, clone its full behavior graph, and attach recipes through a name-based picker.
- **My changes:** friendly cards for every saved change made by either a designer or an agent, refreshed automatically when project files change.
- **Review & publish:** run safety checks, review the exact changes, and publish to the client or server with an automatic restore copy.

If the first button click occurs while the interactive connection is still starting, wait a second and click again. The host log should show `Now listening on: http://127.0.0.1:5188`.

## Create a custom recipe

The safest approach is to clone the nearest known-good recipe and then change only intentional fields.

In the designer:

1. Open **Recipe maker**.
2. Type part of the existing recipe's ordinary in-game name and select the correct result.
3. Review the complete editable copy that appears below the source search.
4. Give it a clear English name. Content Studio creates the stable project key automatically.
5. Add, remove, or rebalance ingredients and products. Type an ordinary item name in each item box and choose from the matching dropdown.
6. Change workbench, crafting time, proficiency, level, grade, visibility, and advanced behavior only as needed.
7. Enable independent labor and casting time when the custom recipe should clone its crafting skill.
8. Save the custom recipe, then use **My changes** for later edits and review.

Choosing a crafting workbench is one atomic operation in the Recipe Maker. The Studio updates both the recipe's workbench requirement and the craft-pack membership that controls which station menu lists it. A build is rejected if those connections disagree, including when an agent edits the recipe plan directly. This prevents a recipe labeled for one workbench from remaining visible at the source recipe's station.

The same source recipe can be copied any number of times. Content Studio automatically proposes distinct names such as **Custom Lumber**, **Custom Lumber 2**, and **Custom Lumber 3**, while keeping the protected storage names unique as well. Workbench copies follow the same rule.

CLI equivalent:

```powershell
dotnet run --project Tools/AAEmu.ContentStudio.Cli -- `
  scaffold-recipe `
  --config Content/content-studio.json `
  --key recipe.moonlit-tonic `
  --source 5545 `
  --name "Moonlit Tonic" `
  --clone-skill
```

Use `--dry-run` to preview every allocation and output path without changing files.

Important recipe fields:

| Field | Meaning |
| --- | --- |
| `id` | New `crafts.id` |
| `names` / `descriptions` | Values compiled into one `localized_texts` row per field |
| `skillClone` | Source skill, new skill ID, labor, casting time, and cloned effect-row IDs |
| `requiredDoodadId` | The only doodad template allowed to execute the recipe when nonzero |
| `materials` | Required item IDs and quantities |
| `products` | Result item IDs, quantities, rates, and grade behavior |
| `rowIds` | Stable IDs for relationship/localization rows; normally tool-owned |

All material and product item IDs must already exist in the client baseline unless separate client item work is performed.

## Create the custom workbench

In the designer:

1. Open **Workbench maker**.
2. Search for an existing crafting object by its in-game name and select it. The search only returns objects with crafting behavior.
3. Review the copied name and safe model choice. Content Studio creates the internal project key automatically.
4. Search for recipes by name. Both baseline recipes and recipes already saved in **My changes** can be attached.
5. Review the complete scrollable attached-recipe list and remove anything that does not belong.
6. Save the workbench manifest.

CLI equivalent:

```powershell
dotnet run --project Tools/AAEmu.ContentStudio.Cli -- `
  scaffold-workbench `
  --config Content/content-studio.json `
  --key workbench.moonlit-alchemy-bench `
  --source 6387 `
  --name "Moonlit Alchemy Bench" `
  --recipes 9100000
```

The workbench scaffold performs two important automatic operations:

- It creates the new craft pack and all cloned doodad-function rows.
- For custom recipe manifests already in the project, it sets `requiredDoodadId` to the new workbench and clears old pack links. The workbench becomes the single owner of those recipe links.

The result also prints a temporary GM test command:

```text
/doodad spawn 9200000
```

The command spawns the doodad three meters in front of the character. To make a spawn permanent, use AAEmu's existing doodad save workflow or add the generated spawn to the appropriate `AAEmu.Game/Data/Worlds/<world>/doodad_spawns*.json` data after testing.

## Validate and build

Validate source manifests without building:

```powershell
dotnet run --project Tools/AAEmu.ContentStudio.Cli -- `
  validate --config Content/content-studio.json
```

Build from the pristine database:

```powershell
dotnet run --project Tools/AAEmu.ContentStudio.Cli -- `
  build --config Content/content-studio.json
```

Every successful build starts from the pristine baseline and writes:

| Output | Purpose |
| --- | --- |
| `.content-studio/build/compact.custom.sqlite3` | The only database artifact to deploy |
| `content-build-manifest.json` | Baseline/source/output hashes, tool version, counts, validation, and changes |
| `content-build-report.md` | Human-readable change and validation report |
| `content-build-audit.sql` | Read-only verification queries for the custom rows |

The original baseline is never patched in place. Compilation occurs in a unique staging directory and a failed transaction is discarded.

Inspect the exact row-count delta:

```powershell
dotnet run --project Tools/AAEmu.ContentStudio.Cli -- `
  diff `
  --baseline .content-studio/baselines/r208022/compact.sqlite3 `
  --artifact .content-studio/build/compact.custom.sqlite3
```

## Inspect from the CLI

```powershell
# Search the whole localized game catalog, with typo recovery and related content
dotnet run --project Tools/AAEmu.ContentStudio.Cli -- `
  search --compact .content-studio/baselines/r208022/compact.sqlite3 --query "archemu"

# Search only items by localized name or exact ID
dotnet run --project Tools/AAEmu.ContentStudio.Cli -- `
  items --compact .content-studio/baselines/r208022/compact.sqlite3 --query "archeum"

# Show a complete recipe graph
dotnet run --project Tools/AAEmu.ContentStudio.Cli -- `
  recipe --compact .content-studio/build/compact.custom.sqlite3 --id 9100000

# Show the cloned workbench graph and attached recipes
dotnet run --project Tools/AAEmu.ContentStudio.Cli -- `
  workbench --compact .content-studio/build/compact.custom.sqlite3 --id 9200000

# Inspect matching table schemas and row counts
dotnet run --project Tools/AAEmu.ContentStudio.Cli -- `
  schema --compact .content-studio/baselines/r208022/compact.sqlite3 --filter craft
```

Successful CLI reads emit JSON. Validation commands use exit code `0` for pass and `1` for failure so agents and CI can rely on them.

### How Game search works

You do not need to know a table name, numeric identity, exact spelling, or entity type before searching. Enter a full name, part of a name, or a descriptive word such as `alchemy`. Search reads every localized table/field pair in the compact database, ranks the most useful matches, and groups them into approachable types. Small spelling mistakes are recovered automatically; for example, `archemu` still finds Archeum content.

Selecting a result opens a contextual inspector. Items load a dedicated scrollable list containing **every recipe that directly consumes the selected item**; this list is independent of the general search-result limit. Recipes show materials, products, labor, casting time, and compatible workbenches, while workbenches show their function graph and available recipes. These relationships also appear directly in the general results for discovery, but the selected item's direct-recipe list is authoritative and complete. Use the type pills to narrow a broad result set. Exact numeric searches stay exact to avoid unrelated descriptions that merely contain the same digits.

Equipment items also load an **Equipment stats** panel. It follows the item into its weapon, armor, or accessory template and shows item level, required level, equipment type, primary-stat allocation, grading/enchanting/repair flags, directly linked buffs or procs, and the complete equipment set. Set panels list every member and every piece threshold, with links to the underlying buff or proc data. Final numeric combat values can vary by grade and tempering, so the panel distinguishes the stable template allocation from values calculated on a particular in-game item instance.

### Edit one gear item or rebalance a whole gear category

Open any weapon, armor piece, or accessory and choose **Change this entry**. The dedicated **Gear power & balance** step separates the controls by scope:

- **Overall power and requirements** changes only the selected item. Item power drives its calculated attributes, damage, or defense; required level only controls who can equip it.
- **Primary attributes** shows the item's Strength, Agility, Stamina, Intelligence, and Spirit proportions. Turn on **Give this item its own primary-stat mix** to copy the shared profile to a safe custom ID and relink only this item. This prevents an apparently small edit from silently changing hundreds of other items.
- **Item-specific template** exposes the selected weapon, armor, or accessory row, including enchanting, repair, durability, type, slot, equipment-set, proc, and recharge-buff settings.
- **Shared balance** identifies every broader rule that includes the item and states approximately how many items it affects.

Use **Gear balance** in the main navigation when the intended change is broad. Its sections deliberately expose the game's reusable balance layers:

| Scope | What it controls |
| --- | --- |
| Global gear constants | Primary-stat and durability scaling for every equipment item |
| Item grade | Quality multipliers for damage, defense, attributes, durability, enchanting, and refunds |
| Weapon type | Shared attack speed, range, damage/healing formulas, durability, and stat scaling |
| Armor class | Physical-defense, magic-defense, damage-type, and durability ratios |
| Equipment slot | The share of total stats, defense, and durability assigned to each body slot |
| Armor formula | Server-wide level-and-grade defense curves |

Each shared rule shows its blast radius before offering **Plan broad change**. The rule then opens in the same friendly, fully explained editor as every other entry, and the saved plan remains visible and editable in **My changes**. This makes changes such as “increase every scepter,” “reduce all plate defense,” or “flatten the gap between quality grades” explicit instead of requiring hand-edited SQL.

The final values players see are composed at runtime from the item's power level, its primary-stat proportions, grade multipliers, its weapon type or armor class, its equipment-slot coverage, and global constants. Build target-compatible artifacts after saving; restart the server and client so both load the matching intended changes from their respective compact schemas.

Raw schema inspection remains available through the command-line tooling for agents and developers, outside the designer workflow.

### Abilities and skills

In ArcheAge, an **ability** is a skillset such as Battlerage, Sorcery, or Vitalism. It is not a single attack. Content Studio translates the compact database's numeric `ability_id` values into those familiar names, so a designer can search for `abilities`, `skillsets`, `Battlerage`, or the name of an individual skill.

Selecting an ability opens a simple ability page. It shows the skills players normally see first and also provides an **All skill rows** view so hidden, passive, and supporting records are not silently omitted. Select any skill to open its complete entry.

### View, change, or copy an entry

Every normal search result has **View all data · change · copy**. The entry page is arranged for people who have never used a game database:

1. **What this page does** explains whether the page is read-only, changing the existing entry, or creating a new entry.
2. **Player-facing text** shows translated names and descriptions without database terminology.
3. **Important fields** puts commonly changed values first and explains what each value controls.
4. **What happens when used** follows a skill through its generic effect into the actual damage, healing, movement, or buff entry. Buff cards summarize application chance, level ranges, shield capacity, duration, stacks, and known character-stat changes.
5. **Connected rules** shows rows that belong to the entry. Skills include effect links, reagents, products, tags, tooltip effects, and use requirements. Buffs include attribute modifiers, dynamic modifiers, repeated effects, and tags when present.

Whenever a field points to another entry—such as an item, skill, buff, workbench, equipment set, recipe group, or proficiency—the editor presents a searchable name picker. The selected card shows the display name and a useful description, and links to the connected entry. Dropdowns search both the baseline and custom entries already saved in **My changes**. Enum-like values such as target type, relationship, damage type, character attribute, and item quality use named choices. Internal relationships without a verified name catalog are preserved safely instead of exposing a numeric box.
6. **More settings** keeps less-common behavior available in the same guided format without exposing the raw storage layout.

The two change choices have deliberately different safety rules:

| Choice | Result |
| --- | --- |
| **Change this entry** | Changes the selected existing content in the built custom database. Use it when that content should behave differently everywhere. |
| **Make a new copy** | Creates a safely separate entry and preserves the original. Use it for new content based on an existing entry. |

Saving does not edit `compact.sqlite3`. It creates a reviewable plan under `Content/projects/custom/records/`. Open **My changes** to see the friendly summary, then use **Build & deploy** to validate and compile it. The untouched baseline remains the input to every build.

When a skill is copied, Content Studio also copies the directly owned connected rows listed on its entry page and allocates safe IDs for them. References to existing effects, icons, models, animations, and other client assets remain linked to their existing IDs. This avoids accidentally duplicating shared game systems and does not create missing client assets.

Recipes and workbenches offer their purpose-built makers in addition to the complete data view. Prefer those makers when changing crafting behavior because they understand and validate the full recipe/workbench graph.

### Edit something already in My changes

Every editable card in **My changes** has **Open and edit**. The card describes the meaningful differences from the original instead of counting every preserved storage field.

- Saved skills, buffs, items, NPCs, and other normal entries reopen in their semantic entry editor with the saved values already applied.
- Saved recipes reopen with separate sections for player text, crafting rules, ingredients, products, labor, and timing.
- Saved workbenches reopen with sections for name/appearance and attached recipes; their reserved function-graph IDs remain protected.
- Internal JSON remains an agent/developer implementation detail and is not exposed in the designer GUI.

Updating a saved change preserves its key, internal identity, translations, and connected rows. It does not create a second plan.

### Delete something from My changes

Choose **Delete this change** on its card and review the confirmation before proceeding. Content Studio explains affected links before enabling permanent deletion:

- Deleting a recipe removes it from any saved custom workbench that offers it.
- Deleting a workbench keeps its saved recipes but changes them to require no workbench, ready for reassignment.
- Deleting a change to an existing entry restores the untouched baseline behavior on the next build.
- Deleting a custom copy permanently retires every ID allocated to it. Tombstoned IDs are never reused.
- Deleting a generic custom copy is blocked while another saved change still references it; the confirmation names the dependency to fix first.

Deletion removes the saved plan, not the already deployed database. Run **Build & deploy** again when the deployed game should stop using that change.

### Example: change Insulating Lens strength

Search for **Insulating Lens**, open the skill, and look under **What happens when used**. Each rank shows its exact buff, level range, shield capacity, physical-defense bonus, and stealth-detection bonus. Choose **Change its gameplay values** for the rank you want to adjust.

The buff editor puts these controls near the top:

- **Initial minimum/maximum charge:** shield damage capacity.
- **Duration / level duration:** fixed lifetime when the buff uses one.
- **Maximum stacks:** how many copies may coexist.
- **Attribute changes:** plainly labeled rows such as `Physical defense +700` and `Stealth detection range +50`; expand a row to change its value or choose flat amount versus percentage.

The skill and its buff are separate game entries. Save each intended change as its own clearly named plan so **My changes** can show exactly what will be altered.

## Deploy a target-compatible artifact

Stop the process that owns the target before replacing its database. The checked-in r208022 baseline descriptor is for the client compact. Do not use that artifact as the AAEmu server compact; the server has additional runtime-only tables. A server deployment remains gated until the same plans can be compiled against and validated on the server-superset baseline.

First preview deployment:

```powershell
dotnet run --project Tools/AAEmu.ContentStudio.Cli -- `
  deploy --config Content/content-studio.json `
  --artifact .content-studio/build/compact.custom.sqlite3 `
  --sha256 <artifactSha256-from-content-build-manifest> `
  --target client --dry-run
```

Deploy only to the compatible configured target:

```powershell
dotnet run --project Tools/AAEmu.ContentStudio.Cli -- `
  deploy --config Content/content-studio.json `
  --artifact .content-studio/build/compact.custom.sqlite3 `
  --sha256 <artifactSha256-from-content-build-manifest> `
  --target client
```

Deployment performs these steps:

1. Stage the artifact and verify it still matches the reviewed build SHA-256.
2. Integrity-check the immutable staged artifact.
3. Integrity-check the existing target when present.
4. Verify that the staged artifact and target schemas match exactly.
5. Copy the existing target to a timestamped backup.
6. Atomically replace the target.
7. Integrity-check the deployed target and verify its reviewed SHA-256.
8. Write a deployment manifest.

Rollback uses the exact backup path printed in the deployment manifest:

```powershell
dotnet run --project Tools/AAEmu.ContentStudio.Cli -- `
  rollback --config Content/content-studio.json `
  --target client `
  --backup .content-studio/backups/client/compact.<timestamp>.<hash>.sqlite3
```

## Test in game

1. Confirm the separately built server and client artifacts contain the same intended shared-row changes while retaining their target-specific schemas.
2. Start Login and Game. Craft and doodad managers load new server rows at startup, so a server restart is required after deployment.
3. Log in with a GM-capable character.
4. Exercise each named change from the reviewed build manifest; the canonical project intentionally contains no production test recipe or test workbench.
5. For a reviewed recipe, confirm its expected workbench, materials, products, labor, and cast time, then craft one result.
6. Move farther than five meters and confirm a forged or stale craft request is rejected.
7. Confirm an unrelated workbench cannot execute the recipe.

The server now rejects:

- Unknown craft IDs instead of throwing a dictionary exception.
- Counts below 1 or above 1000.
- Missing or wrong required doodads.
- Workbenches farther than five meters.
- Recipes not present in the doodad's exposed craft pack.
- Missing craft skills.
- Materials removed during the cast before products are granted.

## ID registry behavior

`Content/projects/custom/id-registry.json` owns IDs by table and stable symbolic key. Allocation:

- Reuses an existing key's ID.
- Scans the pristine compact for collisions.
- Scans current allocations and tombstones.
- Allocates the first free value in the table's reserved range.
- Never writes to the pristine compact.

Do not hand-edit allocated IDs casually. When content is retired, move its allocation to `tombstones` rather than reusing it. IDs are table-specific; the same numeric value in unrelated tables is legal, but Content Studio uses separate ranges to make debugging clearer.

## Raw SQL escape hatch

Files matching `Content/projects/custom/raw-sql/*.sql` execute inside the same build transaction after typed manifests. Use them only for tables the initial model does not support.

Raw SQL must:

- Use explicit fixed IDs registered in `id-registry.json`.
- Never attach another database or write outside the staging compact.
- Be reviewed like code.
- Remain deterministic.
- Include enough comments to identify its entity and source research.

Typed recipe/workbench manifests are preferred because they receive stronger semantic validation.

## Troubleshooting

### Baseline hash or length mismatch

The selected baseline was edited, patched, or comes from another client build. Restore the pristine r208022 copy. Do not update `Content/baselines/r208022.json` to silence the check unless intentionally adding support for a separately researched client build.

### Designer page renders but buttons do nothing

Wait for the interactive connection, then reload once. Ensure the host serves `/_framework/blazor.web.js` with status 200. Start through `Scripts/StartContentStudio.bat`, which builds and launches the correct project.

### Port 5188 is already in use

```powershell
Scripts\StartContentStudio.ps1 -Url http://127.0.0.1:5190
```

### The recipe does not appear

Inspect both graphs in each target-compatible artifact. The recipe must point to the custom `requiredDoodadId`, and `craft_pack_crafts` must connect the workbench's custom pack to the recipe. Confirm the client received its compatible artifact and was fully closed during replacement.

### The workbench appears but has the wrong visual

The clone reuses the source doodad's client model and phase models. Set `modelOverride` only to a model path already present in the r208022 client. New model assets require separate client modifications.

### Build reports duplicate IDs

Do not change the database directly. Find the conflicting key in `id-registry.json`, choose the appropriate table-specific custom range, and scaffold again. Preserve already released IDs as tombstones.

### A numeric field reports `--- :null` or “must be a whole number”

Some r208022 rows use the legacy text marker `--- :null` for an empty numeric value. Content Studio normalizes that marker to **Leave empty / use the game default** in the GUI and to a real null during compilation. Existing saved plans containing the marker are also accepted; designers should never type or repair it manually.

### A deployment fails

The target may be open by AAEmu.Game or the ArcheAge client. Close the process and retry. The deployment stages and verifies before replacement; a failed attempt does not intentionally modify the pristine baseline.

## Verification performed for this release

- Baseline SHA-256 and all 635 tables verified against the actual r208022 compact.
- The local designer compiled and rendered successfully.
- Interactive designer preflight, ability browsing, saved-plan editing, skill-to-buff tracing, semantic buff/recipe/workbench forms, legacy-null handling, typo recovery, relationship discovery, and My Changes review exercised against the real baseline.
- Twenty-one automated compiler, search, friendly-reference, repeated-copy naming, and shared-edit safety tests passed.
- AAEmu.Game compiled with the craft request hardening.
- The included custom recipe/workbench graph compiled from the real 126 MB baseline.
- SQLite integrity and semantic graph validation passed.
- The built recipe resolves to custom skill `9400000`, required doodad `9200000`, and craft pack `9300000`.
- The built workbench resolves both cloned craft-pack payloads to pack `9300000` and recipe `9100000`.
