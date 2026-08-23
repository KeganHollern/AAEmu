# Reference asset index

The reference assets are curated from the exact r208022 client extraction. Always inspect pixels before prompting; filenames alone are not sufficient.

## Atlases

| Family | Atlas | Use for |
| --- | --- | --- |
| Skills and mount skills | `assets/reference-atlas-skills.png` | Full-bleed action composition, skillset palettes, glow, silhouettes |
| Items | `assets/reference-atlas-items.png` | Equipment perspective, neutral ground, materials, consumables |
| Actions | `assets/reference-atlas-actions.png` | Interaction transparency, emotion poses, 128×128 craft still lifes |
| Buffs | `assets/reference-atlas-buffs.png` | Positive 28×28 glyphs and 48×48 reused status imagery |
| Debuffs | `assets/reference-atlas-debuffs.png` | Negative 28×28 glyphs and hostile 48×48 status imagery |

The atlases are overview references. When the image-generation tool accepts multiple inputs, also pass individual icons from `assets/references/` so 48×48 details are not lost in the atlas layout.

## Individual references

The directories mirror the families:

```text
assets/references/
  skills/
  items/
  actions/
    interaction/
    emotion/
    craft/
  buffs/
  debuffs/
```

`assets/reference-manifest.csv` lists every bundled reference, its original pak filename, local path, category, and native dimensions.

## Choosing a reference set

Use 4–8 individual references with distinct jobs:

1. **Family anchor** — closest overall layout and native size.
2. **Semantic anchor** — similar object, body part, action, or status meaning.
3. **Palette anchor** — same skillset or material family.
4. **Edge/lighting anchor** — similar glow, metal highlight, transparency, or background treatment.
5. Optional alternatives — one or two neighboring assets to prevent overfitting to a single icon.

Never select references only because their colors are attractive. Native size, UI role, composition, and opacity behavior take precedence.

## Routing examples

- New Battlerage strike: skill atlas plus two `icon_skill_fight*` references and one neighboring weapon-arc skill.
- New healing skill: skill atlas plus `icon_skill_love*` hand/light references; do not use potion item icons as primary style anchors.
- New sword item: item atlas plus low/mid/high sword examples with the desired material complexity.
- New repair interaction: interaction references with gears/tools and transparent edges; do not use craft icons, which are opaque 128×128 still lifes.
- New alchemy actability icon: action atlas plus `actions/craft/alchemy.png` and related craft still lifes.
- New compact debuff: debuff atlas plus three 28×28 debuff glyphs; do not shrink a 48×48 combat scene unchanged.

## Refreshing the references

If the source client changes, rebuild the curated assets from a verified full icon extraction:

```powershell
python .agents/skills/archeage-icon-style/scripts/build_reference_assets.py `
  --extraction "D:\AAEmu\Extracted Item Ability Action Buff Debuff Icons" `
  --skill ".agents\skills\archeage-icon-style"
```

Review all regenerated atlases before accepting them. A successful script run proves file availability, not continued stylistic suitability.

