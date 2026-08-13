# AAEmu Content Studio Implementation Plan

- Status: Planned
- Audience: AAEmu developers, content designers, automation agents, and reviewers
- Target client: ArcheAge 1.2 (`r208022`)
- Last updated: August 12, 2026

## Goal

Build a comprehensive AAEmu content-authoring system that lets automation agents and human designers inspect, create, validate, build, review, and deploy synchronized `compact.sqlite3` changes without editing the pristine database directly.

The first supported vertical slice is custom crafting: custom recipes using existing items and assets, followed by dedicated custom workbenches cloned from existing client-compatible templates.

## Core principles

1. Treat the original `compact.sqlite3` as an immutable, proprietary build input that is never committed to Git.
2. Store custom content as structured, reviewable source files rather than direct database edits.
3. Use one shared content engine for the command-line interface, designer interface, tests, and future agent integrations.
4. Build one patched database artifact and deploy identical copies to the server and client.
5. Validate relationships explicitly because the compact database has no primary keys, foreign keys, indexes, views, or triggers.
6. Make builds deterministic, attributable, reversible, and safe to run in dry-run mode.
7. Keep the game server authoritative even when the client displays matching content data.

## Proposed architecture

| Component | Responsibility |
| --- | --- |
| `AAEmu.ContentStudio.Core` | SQLite inspection, catalog indexing, entity graphs, ID allocation, compilation, validation, diffing, and artifact creation |
| `AAEmu.ContentStudio.Cli` | Scriptable commands for agents, developers, CI, and automation |
| `AAEmu.ContentStudio.Designer` | Local Blazor interface for designers who should not need to edit SQL or JSON |
| `AAEmu.ContentStudio.Tests` | Synthetic compact fixtures, unit tests, deterministic-output tests, and optional tests against the real r208022 compact |
| `Content/` | Version-controlled content projects, schemas, ID registries, localization, and explicit raw SQL escape hatches |

The new maintained tool projects should be included in the main `AAEmu.slnx` solution and normal build/test workflow.

## Repository layout

```text
Content/
  baselines/
    r208022.json
  schemas/
    content-project.schema.json
    recipe.schema.json
    workbench.schema.json
  projects/
    custom/
      project.json
      id-registry.json
      recipes/
      workbenches/
      localizations/
      raw-sql/

Tools/
  AAEmu.ContentStudio.Core/
  AAEmu.ContentStudio.Cli/
  AAEmu.ContentStudio.Designer/
  AAEmu.ContentStudio.Tests/
```

Generated databases, reports, backups, and proprietary client assets must remain ignored build artifacts outside Git.

## Canonical content model

Structured JSON manifests are the canonical source of truth. Raw SQL remains available for unsupported tables and expert-reviewed corrections, but it is not the primary designer workflow.

Example recipe manifest:

```json
{
  "key": "recipe.custom_alloy",
  "id": 9100001,
  "name": {
    "en_us": "Experimental Alloy"
  },
  "skillId": 14644,
  "laborCost": 20,
  "actabilityLimit": 0,
  "materials": [
    {
      "itemId": 3409,
      "amount": 5
    }
  ],
  "products": [
    {
      "itemId": 8000026,
      "amount": 1
    }
  ]
}
```

The compiler translates the manifest into the related rows in `crafts`, `craft_materials`, `craft_products`, `craft_pack_crafts`, and `localized_texts`.

## Build pipeline

```text
Pristine r208022 compact
  -> verify baseline hash and schema fingerprint
  -> copy to a staging database
  -> compile enabled content projects
  -> apply explicitly listed raw SQL patches in a transaction
  -> run SQLite integrity checks
  -> run AAEmu semantic and reference validation
  -> generate entity-level and SQL audit reports
  -> publish a versioned compact artifact and build manifest
  -> atomically deploy identical copies to server and client targets
```

Every successful build should produce:

- A patched `compact.sqlite3` artifact.
- A machine-readable validation report.
- A human-readable entity-level change report.
- Generated SQL for review and diagnostics.
- A build manifest containing the baseline hash, enabled project and patch hashes, tool version, build time, and output hash.
- A deployment manifest recording server/client targets and hashes.

## Command-line interface

The CLI is the primary interface for agents, developers, automation, and CI. It must support noninteractive operation, stable exit codes, structured JSON output, and dry-run behavior.

Initial commands:

```text
aaemu-content doctor
aaemu-content schema list
aaemu-content catalog search item "archeum"
aaemu-content catalog show recipe 5545
aaemu-content catalog graph workbench 6387

aaemu-content ids allocate recipe recipe.custom_alloy
aaemu-content scaffold recipe recipe.custom_alloy
aaemu-content scaffold workbench workbench.custom_forge --clone 6387

aaemu-content validate
aaemu-content build
aaemu-content diff
aaemu-content deploy --target server
aaemu-content deploy --target client
aaemu-content verify-deployment
```

### Agent requirements

- Read commands support `--format json` with a stable documented schema.
- Mutating commands modify project manifests, never the pristine database.
- Mutations support `--dry-run` and explain the files and entities that would change.
- Errors identify the manifest file, field, table, entity ID, and failed relationship.
- Catalog graph commands explain multi-table compact relationships.
- JSON Schema files provide editor and pre-build validation.
- Commands remain composable so a future local MCP adapter can reuse Core rather than duplicate logic.

## Designer interface

The designer should be a local ASP.NET Core/Blazor application bound only to localhost. It should call the same Core services as the CLI and write the same manifests.

### Primary screens

- Project dashboard with baseline, validation, build, and deployment status.
- Searchable item, skill, doodad, recipe, craft-pack, and model catalogs.
- Typed recipe editor.
- Workbench cloning wizard.
- Localization editor.
- Dependency graph and reference inspector.
- Baseline-versus-project change preview.
- Validation panel with errors linked back to the relevant form field.
- Build, deployment, backup, and rollback controls.
- World-spawn JSON and GM command generator.

### Recipe editor

The recipe editor should support:

- Existing item search by ID and localized name.
- Material and product lists with quantities and grade behavior.
- Craft skill and animation selection.
- Labor cost, casting time, actability group, and proficiency requirements.
- Craft-pack placement and visible ordering.
- Required workbench selection.
- Localization entry and preview.
- Immediate semantic validation.

### Workbench cloning wizard

The wizard should:

1. Select an existing workbench to clone.
2. Display its doodad template, model, phases, functions, permissions, and craft pack.
3. Allocate all required custom IDs together.
4. Clone the complete interaction graph rather than constructing a partial workbench.
5. Allow selection of an existing client model or prefab.
6. Create a dedicated craft pack.
7. Attach selected custom recipes.
8. Add localization.
9. Generate an optional world-spawn entry or GM spawn command.

Initial releases should display model paths and known metadata. Full 3D preview is deferred until CryEngine asset rendering is understood.

## ID allocation policy

Reserve documented custom ranges above existing AAEmu custom IDs, beginning at `9,000,000` unless investigation reveals a client limitation.

Example registry:

```json
{
  "crafts": {
    "recipe.custom_alloy": 9100001
  },
  "doodad_almighties": {
    "workbench.custom_forge": 9200001
  },
  "craft_packs": {
    "pack.custom_forge": 9300001
  }
}
```

Rules:

- IDs are unique within their target table.
- Released IDs are immutable.
- Removed content is tombstoned and its IDs are never reused.
- Allocation scans the pristine baseline and every enabled content project.
- Workbench cloning allocates doodad, group, function, subtype payload, pack, recipe, material, product, and localization row IDs as one operation.
- Symbolic keys remain stable even if display names change.

## Validation requirements

The compact database provides no relational enforcement, so the tool must validate both SQLite integrity and AAEmu semantics.

### Baseline and artifact validation

- Baseline SHA-256 matches the selected target descriptor.
- Required tables and columns match the expected schema fingerprint.
- `PRAGMA integrity_check` succeeds.
- The generated output contains every explicitly enabled patch.
- Deployed server and client hashes match the built artifact.

### General data validation

- No duplicate IDs in touched or dependency tables.
- Custom IDs stay within registered ranges.
- Immutable released IDs were not renumbered or repurposed.
- Every referenced row exists and has the expected entity type.
- Required localization exists for the configured default language.
- Numeric values fit packet and server model ranges.

### Recipe validation

- Every recipe has at least one valid product.
- Material and product item templates exist.
- Amounts are positive and batch multiplication cannot overflow.
- The skill and required doodad exist.
- Labor, casting, actability, grade, and rate values are valid.
- Craft-pack links reference valid recipes and packs.
- Visible ordering is deterministic.

### Workbench validation

- The doodad template and selected model exist.
- A valid starting function group exists.
- Function groups belong to the expected doodad.
- `actual_func_type` maps to a supported subtype table.
- Subtype payload IDs exist.
- Craft-pack functions reference valid packs.
- Phase transitions do not reference missing groups.
- Fixed-world and player-placeable workbenches receive appropriate interaction and recovery functions.

## Server crafting hardening

Custom content must not rely on the current permissive crafting packet path. Before shipping custom recipes, harden `CSExecuteCraft`, `CraftManager`, and `CharacterCraft`.

Required changes:

- Replace direct recipe dictionary indexing with safe lookup and a controlled error response.
- Reject nonpositive and excessive batch counts.
- Resolve the submitted doodad in the player's current world.
- Enforce a maximum interaction distance.
- Verify that the recipe belongs to the doodad's active craft pack.
- Enforce `req_doodad_id` and permissions.
- Validate skill, material, and product templates.
- Multiply required materials by the requested count safely.
- Recheck materials, labor, and inventory capacity when each craft completes.
- Do not award products unless material and labor consumption succeeds.
- Add unit and integration tests for malformed and forged client requests.

## Testing strategy

### CI-safe tests

Because the proprietary compact cannot be committed, CI should use a small synthetic SQLite database containing representative recipe, item, localization, doodad, function, and craft-pack tables.

Tests should cover:

- Schema inspection and fingerprinting.
- ID allocation and collision handling.
- Manifest parsing and JSON Schema validation.
- Deterministic compilation and stable generated SQL.
- Duplicate and dangling-reference detection.
- Polymorphic doodad-function validation.
- Entity-level diff generation.
- Build failure rollback.
- Atomic deployment logic using temporary fixtures.

### Optional local integration tests

When the real r208022 compact is present, run opt-in tests that:

- Inspect known recipe and workbench chains.
- Build a complete patched copy.
- Load changed entities through the same field expectations as AAEmu managers.
- Confirm the output passes integrity and semantic checks.
- Confirm a second identical build produces the expected stable artifact hash.

### Manual client/server verification

- Workbench renders with the selected existing model.
- Name and recipe localization display correctly.
- Recipe appears only in the intended craft pack.
- Client displays the intended materials, product, labor, proficiency, and duration.
- Server independently rejects invalid distance, workbench, materials, labor, and batch requests.
- Single and batch crafting play the expected animation.
- Correct products appear and inputs are consumed.
- Behavior survives client relaunch and server restart.

## Deployment and rollback

Build and deployment must be separate commands. Building never changes a live client or server installation.

Deployment behavior:

1. Verify the selected build manifest and output hash.
2. Confirm the target is the configured server or client compact path.
3. Refuse replacement when the target is in active use.
4. Copy to a temporary file on the target volume.
5. Verify the temporary copy's hash.
6. Preserve a timestamped backup and manifest.
7. Replace the target atomically.
8. Verify the installed hash.

Rollback restores the previous artifact recorded in the deployment manifest.

Once new item templates can persist in MySQL inventories, released template IDs must remain available in later compact builds even when their acquisition path is disabled. Removing a persistent item definition can make existing character data unreadable or unusable.

## Implementation milestones

### Milestone 1: Foundation and read-only inspector

- Add Core, CLI, and test projects to the main solution.
- Define the baseline descriptor and schema fingerprint format.
- Implement database inventory and generic table inspection.
- Implement localized catalog search.
- Implement recipe and workbench dependency graphs.
- Add the synthetic compact fixture.

Acceptance: an agent can inspect recipe `5545` and workbench `6387`, including their complete dependency chains, without writing SQL.

### Milestone 2: Declarative recipe compiler

- Define project, recipe, localization, and ID-registry schemas.
- Implement ID allocation.
- Compile recipes into a staging database.
- Support linking a new recipe to an existing craft pack.
- Generate SQL and entity-level diff reports.
- Implement initial semantic validation.

Acceptance: the same manifest reproducibly builds a validated compact artifact from the same baseline.

### Milestone 3: Crafting security and first end-to-end recipe

- Harden the server crafting request and completion paths.
- Add behavior tests for invalid IDs, distance, batch counts, permissions, materials, labor, and output capacity.
- Build one custom recipe using existing items and an existing workbench.
- Deploy identical compact artifacts to the client and server.
- Perform an in-game crafting test.

Acceptance: the native client UI displays accurate recipe information and the server independently enforces it.

### Milestone 4: Dedicated workbench compiler

- Model doodad templates, groups, phase functions, action functions, subtype payloads, and craft packs.
- Implement complete workbench graph cloning.
- Add model, permission, and phase-graph validation.
- Generate a world-spawn entry and GM spawn command.
- Create the first dedicated custom workbench using an existing client model.

Acceptance: the new workbench has its own localized name and craft pack, renders without new assets, and exposes only its intended recipes.

### Milestone 5: Designer application

- Build the local Blazor application on Core.
- Add catalog browsers, recipe forms, workbench wizard, localization, dependency graphs, validation, and diff views.
- Add autosaved drafts and undo support.
- Ensure UI output is byte-for-byte equivalent at the manifest level to CLI-authored content.

Acceptance: a designer can create, validate, build, and preview the same custom workbench without editing SQL or JSON.

### Milestone 6: Deployment, provenance, and CI

- Implement atomic server/client deployment and rollback.
- Add build and deployment manifests with hashes.
- Add synthetic-project validation to CI.
- Add optional real-compact integration tests.
- Document agent, designer, build, deployment, and recovery workflows.

Acceptance: content builds are reviewable, reproducible, safely deployable, verifiable, and reversible.

### Milestone 7: Extensions

- Add a local agent/MCP adapter over Core.
- Add item-template cloning.
- Add icon preview from existing client assets.
- Add editors for loot, merchants, NPCs, skills, quests, and world spawns.
- Add multi-project dependencies and conflict resolution.
- Add localization completeness reporting.
- Add optional MySQL compatibility checks for persistent custom template IDs.

New 3D assets, animations, and `game_pak` repacking remain outside the initial Content Studio release.

## First vertical implementation slice

Implement this order first:

1. Read-only compact inspector and entity graph.
2. Declarative recipe format and JSON Schema.
3. ID registry and collision validation.
4. Deterministic staging-database builder.
5. Validation and diff reports.
6. Server crafting hardening.
7. One custom recipe on an existing alchemy workbench.

This delivers immediate gameplay value and proves the shared foundation before building the larger designer interface and dedicated workbench compiler.

## Definition of done

The Content Studio foundation is complete when:

- Agents can inspect and author supported content through deterministic CLI commands.
- Designers can author the same content through the local visual interface.
- Both workflows produce the same canonical manifests.
- Builds start from a verified pristine baseline and never mutate it.
- Validation catches duplicate IDs, broken references, invalid function graphs, and client/server divergence.
- Build reports clearly explain every entity and table change.
- Server and client receive identical verified artifacts.
- Deployment is atomic and has a tested rollback path.
- The first dedicated custom workbench and recipe work end to end with existing client assets.
