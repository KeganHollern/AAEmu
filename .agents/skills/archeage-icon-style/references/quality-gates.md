# Icon quality gates

An icon is accepted only when every hard gate passes at its native resolution. A polished large image is not evidence of a valid game icon.

## Gate 1 — Correct family and semantics

- The icon uses the family requested by its actual UI placement.
- One subject/action/status is recognizable without a label.
- The silhouette remains recognizable in a mixed icon grid, not only in isolation.
- No unrelated symbolic shorthand was introduced to explain a weak composition.

Failure is blocking. Re-brief before changing rendering.

## Gate 2 — Native composition

Inspect at 100% native size and at 4× nearest-neighbor zoom.

- Skill/item/emotion/interaction: exactly 48×48.
- Compact status: exactly 28×28.
- Craft/actability: exactly 128×128.
- The focal shape fills a similar fraction of the canvas as the selected references.
- No important feature becomes a one-pixel accident after reduction.
- Cropping looks intentional; no unexplained tangencies touch the edge.

Failure is blocking.

## Gate 3 — Family-specific background and alpha

- Skills and items: opaque square with the correct dark painted/catalog ground.
- Emotions and craft icons: opaque family-specific ground.
- Buffs/debuffs: normally opaque at both supported sizes.
- Interactions: transparent outer field with a compact isolated pictogram.

Check alpha numerically. A visually black pixel is not equivalent to a transparent pixel.

Failure is blocking.

## Gate 4 — Rendering match

Compare beside at least three individual references:

- painterly edge variation rather than uniform vector outlines;
- comparable shadow depth, highlight size, saturation, and local contrast;
- no mobile-game bevel, global gloss, photorealism, or clean 3D-render signature;
- no text, gibberish marks, accidental faces, malformed hands, or ambiguous object joins;
- material behavior matches the family: restrained items, energetic skills, symbolic compact states.

Any obvious modernizing deviation is blocking even if the validator passes.

## Gate 5 — Quantitative envelope

Run the validator from the repository root:

```powershell
python .agents/skills/archeage-icon-style/scripts/validate_icon.py path\to\icon.png --category skill
python .agents/skills/archeage-icon-style/scripts/validate_icon.py path\to\icon.dds --category item --json
```

The validator checks dimensions, alpha behavior, measured color/value envelopes, highlight range, DDS format/mips, and nearest bundled references.

- Exit 0: quantitative checks pass; continue visual review.
- Exit 1: one or more style-envelope warnings; compare to the reported nearest references and correct or explicitly justify.
- Exit 2: hard technical failure; do not deliver or integrate.

The measured envelope is deliberately broad. It cannot detect the wrong anatomy, a weak silhouette, fake text, inappropriate symbolism, or generic AI aesthetics.

## Gate 6 — Client-ready output

Keep a lossless PNG master. For DDS integration:

- match the nearest existing icon family's pixel format rather than choosing globally;
- use DXT1/BC1 for the opaque families that use it, or uncompressed RGB32/BGRA when the neighboring references do;
- preserve straight alpha for transparent interaction icons;
- use the complete native mip chain: 6 levels for 48×48, 5 for 28×28, and 8 for 128×128;
- inspect the DDS after encoding because BC1 can destroy tiny colored accents;
- use a new filename/ID until replacement is explicitly approved; never overwrite an extracted original by default.

## Focused correction loop

Make one diagnosis per iteration:

1. State the single largest mismatch.
2. Edit/regenerate only that property.
3. Reduce to native size again.
4. Re-run the visual and quantitative gates.

Examples of useful correction prompts:

- “Keep the silhouette and palette; remove the glossy bevel and replace it with rough painted edge highlights matching references A and B.”
- “Increase the weapon to fill the 48×48 diagonal like reference C; keep the neutral charcoal item background and remove the magical aura.”
- “Redesign this as a true 28×28 status glyph with one broad symbol; discard the small secondary particles.”

After three focused corrections, stop. Report the remaining mismatch instead of weakening the acceptance criteria.

