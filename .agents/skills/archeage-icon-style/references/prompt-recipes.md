# Image-generation prompt recipes

Use these as structured prompt components, not as substitutes for inspecting and attaching the correct local references. Replace every bracketed field with concrete semantics.

## Shared fidelity block

Append this block to every new-icon prompt:

```text
The attached extracted ArcheAge 1.2 r208022 icons are the binding visual target. Match their hand-painted early-2010s PC MMORPG raster treatment, compressed values, selective sharpness, native-size legibility, and detail density. Design for [48x48 / 28x28 / 128x128] even though the working image is larger. One icon only, square composition.

Do not add text, letters, numbers, fake runes, a border, card frame, badge, rarity treatment, cooldown overlay, key binding, or other UI chrome. No modern mobile-game gloss, thick bevels, flat vector style, corporate pictogram, emoji/sticker style, photorealism, cinematic scene, clean 3D render, global bloom, or unrelated decorative particles.
```

## Skill or mount skill

```text
Create one [skillset/family] skill icon for: [single action and gameplay meaning].

Use an opaque full-bleed dark painted background. The [primary silhouette/action] fills roughly 65-90% of the square and follows a strong [diagonal/radial] motion vector. Use [family palette] with one small near-white focal accent, saturated midtone action shapes, deep black structural shadows, crisp focal edges, and softer energy trails. Local glow only. The action must remain immediately readable at 48x48.

[Shared fidelity block]
```

For mount/pet/vehicle skills, name the exact mount or component that must remain recognizable and attach at least one matching bundled mount/pet/vehicle reference.

## Item

```text
Create one item icon for: [exact object, material, and quality level].

Show a single believable object in compact three-quarter catalog view, [diagonal orientation if applicable], nearly filling a 48x48 square with only a few pixels of breathing room. Use a fully opaque low-saturation charcoal/blue-gray/green-gray background, a soft grounding wedge or contact shadow, dark material body values, and narrow selective edge highlights. Keep ordinary equipment physically readable; use magical glow only at [specific inset/edge] if required.

[Shared fidelity block]
```

## Interaction pictogram

```text
Create one interaction pictogram for: [literal interaction].

Use a transparent 48x48 canvas and one compact literal object/symbol occupying about half the square. Keep the outside border transparent. Render with small painterly material highlights and a tight soft shadow; no opaque card, circle badge, outline-icon system, or full background scene.

[Shared fidelity block]
```

## Emotion

```text
Create one emotion icon showing: [single gesture/emotional action].

Use an opaque muted gray/lavender/desaturated 48x48 background. Show a tightly cropped stylized character head/torso/arm/hand performing one unmistakable gesture. The pose and silhouette carry the meaning. Keep facial micro-detail secondary and match the muted painterly anime-influenced references; do not make a sticker or emoji.

[Shared fidelity block]
```

## Craft or actability

```text
Create one 128x128 craft/actability icon for: [profession or process].

Arrange [one to three tools/materials] as a compact three-quarter still life on an opaque parchment-gray/olive-gray/charcoal ground. Use subdued natural colors, warm selective highlights, soft cast shadows, readable material differences, and the reference level of small detail. No combat explosion, aura, or saturated spell lighting.

[Shared fidelity block]
```

## Compact buff or debuff

```text
Create one [positive buff / negative debuff] status glyph for: [single state].

Design natively for 28x28 rather than shrinking a busy skill scene. Use one broad symbol or cropped body/object part, large value blocks, strong light/dark separation, an opaque background, and only meaningful one-pixel accents. Fill most of the square. Convey the state through the symbol, pose, and reference-supported palette; do not add a generic plus, minus, red X, skull, prohibition circle, or hazard badge unless semantically required.

[Shared fidelity block]
```

## Focused edits

When a candidate is close, use edit mode with the candidate and the same references. Preserve every passing property and name one correction:

```text
Preserve the exact subject, silhouette, crop, and [passing properties]. Correct only [single mismatch]. Match [specific references] for [edge/background/palette/material/detail] behavior. Do not redesign or add elements.
```

Avoid stacking multiple fixes in one edit prompt. Re-evaluate at native size after every pass.

