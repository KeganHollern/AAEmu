---
name: archeage-icon-style
description: Create, extend, repair, or review ArcheAge 1.2 (r208022) UI icons while matching the extracted game_pak art direction and native texture constraints. Use for item, skill, mount, interaction, emotion, craft, buff, or debuff icons. Do not use for logos, general fantasy illustration, UI layouts, or code-native vector assets.
---

# ArcheAge 1.2 icon style

Produce an icon that reads as an original r208022 client asset at native size. Fidelity is judged against the bundled extracted references, not against generic fantasy-game icon conventions.

## Required context

Before generating or reviewing an icon:

1. Read [references/style-spec.md](references/style-spec.md) completely.
2. Read [references/reference-index.md](references/reference-index.md), select the correct icon family, and inspect the chosen atlas plus 4-8 individual references with an image viewer.
3. For generation, read [references/prompt-recipes.md](references/prompt-recipes.md).
4. For finalization or review, read [references/quality-gates.md](references/quality-gates.md).

Do not generate until the icon family and gameplay meaning are known. If the request gives a concept but not a family, infer it only when the intended UI placement makes the choice unambiguous; otherwise ask one concise question.

## Workflow

### 1. Lock the semantic brief

Write a one-sentence internal brief containing:

- family: skill, item, interaction, emotion, craft, buff, or debuff;
- one primary subject/action;
- intended palette or skillset affiliation;
- required native size;
- any must-preserve silhouette from an attached concept.

Treat an attached concept as semantic guidance unless it is itself an extracted ArcheAge reference. The bundled assets remain authoritative for style.

### 2. Select references intentionally

Use the matching atlas in `assets/` to establish the family grammar, then choose individual PNGs under `assets/references/` for composition, material, palette, and edge treatment. Do not use an item atlas to style a skill, a 48x48 skill to style a 28x28 status glyph, or an emotion portrait to style an interaction pictogram.

When using image generation, pass the smallest useful set of local reference images. Prefer 4-8 closely relevant individual references; include the atlas only when broader family context is needed. Generate one icon per image.

### 3. Generate at working resolution

Use the image-generation/editing capability for creative generation. State that the extracted references are the binding visual target. Keep the composition square and designed for the native target from the start even if the generator returns a larger raster.

The prompt must explicitly require:

- the chosen family grammar from `style-spec.md`;
- a single immediately readable subject/action;
- hand-painted early-2010s PC MMORPG rendering;
- native-size legibility, strong silhouette, compressed values, and restrained micro-detail;
- no text, lettering, numbers, border, card frame, badge, rarity frame, cooldown overlay, drop-shadow panel, or extra UI chrome;
- no modern mobile-game gloss, flat vector treatment, clean corporate pictogram style, photorealism, cinematic scene, or 3D-render look.

Do not ask the model to make a generic "ArcheAge-like fantasy icon" without reference images and family-specific constraints.

### 4. Reduce to the native asset

Crop to a true square without changing the intended silhouette. Downsample in sRGB with a high-quality filter; do not use nearest-neighbor reduction for painted icons. Inspect the reduced native image, not only the large generation.

Native masters:

| Family | Required master |
| --- | --- |
| Skill, mount skill, item, interaction, emotion | 48x48 PNG |
| Compact buff/debuff | 28x28 PNG |
| Large buff/debuff reusing a skill/item composition | 48x48 PNG |
| Craft/actability | 128x128 PNG |

Retain the lossless PNG as the editable master. Create DDS only when requested or when integrating into the client. Match the nearest source family's DDS pixel format and mip chain; never overwrite an extracted original.

### 5. Validate and iterate

Run:

```powershell
python .agents/skills/archeage-icon-style/scripts/validate_icon.py <icon.png> --category <family>
```

Valid families are `skill`, `item`, `interaction`, `emotion`, `craft`, `buff`, and `debuff`. Exit code 2 is a hard failure. Exit code 1 requires visual review and usually one focused correction. Quantitative passing does not replace the visual gates.

Compare at native size and at 4x nearest-neighbor zoom. Regenerate or edit only the failing property - silhouette, palette, lighting, background, detail density, or alpha behavior. Stop after three focused correction passes; if it still fails, deliver the closest candidate with the unresolved mismatch stated plainly rather than relaxing the style.

## Non-negotiable invariants

- Preserve the family boundaries and native dimensions.
- Design for recognition at 48x48 or 28x28; detail that disappears is not useful detail.
- Keep the icon itself free of UI frames and text. The client supplies overlays separately.
- Do not add transparent backgrounds to opaque families. In the reference corpus, skills, items, emotions, craft icons, buffs, and debuffs are normally opaque squares; interaction pictograms are the transparent family.
- Never "improve" the style into a cleaner, glossier, sharper, more realistic, or more modern aesthetic.
- Do not trace or minimally recolor a reference. Preserve the visual grammar while creating a distinct subject-specific asset.
