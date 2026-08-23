# ArcheAge 1.2 icon style specification

This specification is derived from icons extracted from the r208022 `game_pak`. The bundled atlases and individual reference PNGs are authoritative whenever prose and pixels appear to disagree.

## Shared visual DNA

All families belong to an early-2010s Korean PC MMORPG art direction:

- Hand-painted raster work with compressed detail, not flat vector geometry or polished mobile-game rendering.
- A decisive silhouette readable in well under a second at native size.
- Deep shadow masses and localized bright accents. Highlights are often close to white, but only on small focal edges or magical cores.
- Slightly rough, painterly transitions and selective sharpening. Avoid perfectly clean Bézier contours, uniform outlines, excessive bloom, or procedural-looking material noise.
- One visual idea per icon. Secondary forms support the subject and never become an illustrated scene.
- No baked-in frame, label, rank marker, cooldown wedge, key binding, or rarity treatment unless the requested source family demonstrably contains that element.

Measured reference tendencies are guides, not substitutes for visual comparison:

| Family | Native size | Typical alpha | Mean saturation | Mean luminance | Border luminance |
| --- | ---: | --- | ---: | ---: | ---: |
| Skill | 48×48 | fully opaque | ~0.62 | ~0.35 | ~0.22 |
| Item | 48×48 | fully opaque | ~0.19 | ~0.29 | ~0.23 |
| Interaction | 48×48 | roughly half visible | ~0.24 | ~0.19 on visible pixels | nearly black/transparent |
| Emotion | 48×48 | fully opaque | ~0.23 | ~0.38 | ~0.36 |
| Craft | 128×128 | fully opaque | ~0.26 | ~0.38 | ~0.35 |
| Buff sample | 28×28 or 48×48 | fully opaque | ~0.47 | ~0.42 | ~0.33 |
| Debuff sample | 28×28 or 48×48 | fully opaque | ~0.54 | ~0.36 | ~0.29 |

## Skill and mount-skill icons

### Composition

- Use a full-bleed dark painted field, usually opaque. It is not a transparent object cutout.
- The primary silhouette, gesture, projectile, limb, weapon arc, or magical core occupies roughly 65–90% of the square.
- Prefer a diagonal, radial, or strongly directional action vector. Trails may leave the canvas; the meaningful subject should not be accidentally cropped.
- Build depth with a near-black rear plane, a saturated midtone action shape, and a small high-value accent.
- A face, hand, weapon, shield, creature, or abstract effect may be simplified aggressively. Anatomical completeness matters less than instant action recognition.

### Rendering

- Use painted edges: sharp at the focal silhouette, softer in trailing energy and background smoke.
- Glow wraps locally around the action. Do not wash the whole icon in bloom.
- Black is a structural color, not an empty void. Maintain enough dark information to frame the bright action.
- Avoid generic circular spell emblems unless the skill meaning specifically requires one.

### Skillset palette anchors

Use neighboring references from the same family rather than treating these as rigid swatches:

| Internal family | Familiar name | Dominant tendencies |
| --- | --- | --- |
| Fight | Battlerage | hot orange, crimson, white weapon arcs, black-red ground |
| Illusion | Witchcraft | violet, indigo, cyan accents, uncanny silhouettes |
| Adamant | Defense | steel blue, white, muted green, shield/body motifs |
| Will | Auramancy | gold-white radiance, sky blue shields, warm spiritual light |
| Death | Occultism | black, blood red, magenta, purple void effects |
| Wild | Archery | acid/forest green, black, steel projectiles, occasional violet |
| Magic | Sorcery | fire orange, electric violet, blue-white elemental cores |
| Vocation | Shadowplay | orange-white cuts, black silhouettes, blood red, ambush gestures |
| Romance | Songcraft | gold, warm white, green, music/performance symbolism |
| Love | Vitalism | green-white healing light, rose/magenta, hands and organic curves |

Mount, pet, vehicle, and glider skills retain the skill rendering grammar but may use cool cyan motion bands, the mount silhouette, or a recognizable mechanical component.

## Item icons

### Composition

- Use a nearly black charcoal, blue-gray, or green-gray opaque background. Keep it subordinate and low saturation.
- Isolate one object in a catalog-like three-quarter view. Weapons commonly run bottom-left to top-right or the reverse and nearly span the square.
- Leave only a few pixels of breathing room. Do not shrink the subject into the center.
- Add a soft grounding wedge, low haze, or contact shadow. Do not create a literal room, landscape, or pedestal scene.
- Preserve believable object proportions. Exaggerate only the feature needed to identify the item class at 48×48.

### Materials

- Metal: dark body values, narrow bright edge glints, small warm or cool reflections; never uniformly chrome.
- Wood/leather: restrained brown/olive midtones with one crisp construction detail.
- Cloth: broad folds and a readable color block, not fine weave texture.
- Potions/materials: compact silhouette, one liquid/material color, subdued container highlight.
- Magical items: localized glow or colored inset; keep the neutral catalog background and physical object readable.

Item icons are substantially less saturated than skills. Do not apply a skill-style explosion or aura to an ordinary piece of equipment.

## Actions and interactions

### Interaction pictograms — 48×48

- Use a transparent canvas with a single small object or symbol occupying about half the square.
- The outermost border should remain transparent/dark. Use a compact drop shadow and a few bright material highlights.
- Prefer literal object cues—arrow, gear, sack, tool, token—over modern outline icons.
- No opaque card background, circle badge, or uniform stroke weight.

### Emotion icons — 48×48

- Use an opaque muted gray, lavender, or desaturated background.
- Show a cropped stylized character, head/torso, arm, or hand performing one readable gesture.
- The pose and silhouette carry the meaning. Facial micro-detail is secondary.
- Keep the anime-influenced character rendering muted and painterly; do not turn it into a sticker or emoji.

### Craft/actability icons — 128×128

- Treat these as small still lifes on an opaque parchment-gray, olive-gray, or charcoal ground.
- Arrange one to three recognizable tools/materials in a compact three-quarter composition.
- Use subdued natural colors, warm highlights, soft cast shadows, and more detail than a 48×48 icon permits.
- Do not reuse the explosive saturated lighting of combat skills.

## Buffs and debuffs

The database distinguishes good buffs, bad debuffs, and hidden/system buffs, but the pixels do not use a universal green-good/red-bad rule. Meaning and neighboring references control color.

### Compact status glyphs — 28×28

- Redesign for 28×28; do not merely shrink a busy 48×48 composition.
- Use one symbol or cropped body/object part, broad value blocks, and a strong light/dark separation.
- Fill most of the square. One-pixel accents matter; tiny decorative marks do not.
- Backgrounds are normally opaque and may be a dark field or a thin color-block treatment. Do not introduce transparency merely because the icon is small.

### Large status icons — 48×48

- These often reuse or closely follow skill/item compositions. Use the correct source family rules first, then ensure the status meaning remains obvious.
- Positive and negative state should come from the actual symbol, pose, and palette family—not from a generic plus/minus badge.

### Debuff emphasis

- Negative states commonly use tighter crops, hostile silhouettes, constricting shapes, sickly or hot accent colors, and heavier black/red/purple/cyan contrast.
- Avoid adding skulls, red X marks, prohibition circles, or hazard triangles unless those symbols are semantically required and supported by nearby references.

## Explicit style failures

Reject an icon exhibiting any of the following:

- glossy mobile-game bevels, thick gold frames, rarity gems, or reward-card presentation;
- flat vector fills, uniform line art, corporate iconography, or emoji/sticker rendering;
- photorealistic product photography or a clean offline 3D render;
- complex scenic backgrounds, multiple narrative subjects, or cinematic depth of field;
- illegible fine texture, text-like runes used as decoration, or AI gibberish marks;
- excessive neon bloom, global rim lighting, over-sharpened halos, or uniformly crushed black;
- a transparent background in a normally opaque family, or an opaque card behind an interaction pictogram;
- a composition that only reads at generation resolution and collapses at native size.

