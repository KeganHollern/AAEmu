from __future__ import annotations

import argparse
import csv
import shutil
from pathlib import Path

from PIL import Image, ImageDraw, ImageFont


CURATED = {
    "skills": [
        "icon_skill_fight02.dds", "icon_skill_fight09.dds", "icon_skill_fight15.dds",
        "icon_skill_illusion04.dds", "icon_skill_illusion05.dds", "icon_skill_illusion18.dds",
        "icon_skill_adamant01.dds", "icon_skill_adamant14.dds", "icon_skill_adamant28.dds",
        "icon_skill_will01.dds", "icon_skill_will14.dds", "icon_skill_will24.dds",
        "icon_skill_death02.dds", "icon_skill_death14.dds", "icon_skill_death27.dds",
        "icon_skill_wild01.dds", "icon_skill_wild14.dds", "icon_skill_wild23.dds",
        "icon_skill_magic01.dds", "icon_skill_magic13.dds", "icon_skill_magic30.dds",
        "icon_skill_vocation01.dds", "icon_skill_vocation14.dds", "icon_skill_vocation26.dds",
        "icon_skill_romance01.dds", "icon_skill_romance14.dds", "icon_skill_romance28.dds",
        "icon_skill_love01.dds", "icon_skill_love14.dds", "icon_skill_love26.dds",
        "icon_skill_horseback01.dds", "icon_skill_battle_pet01.dds", "icon_skill_vehicle07.dds",
    ],
    "items": [
        "icon_item_sword_1h_0001.dds", "icon_item_sword_1h_0048.dds", "icon_item_sword_1h_0090.dds",
        "icon_item_blade_2h_0001.dds", "icon_item_blade_2h_0038.dds", "icon_item_blade_2h_0070.dds",
        "icon_item_axe_1h_0001.dds", "icon_item_axe_1h_0045.dds", "icon_item_axe_1h_0085.dds",
        "icon_item_staff_2h_0001.dds", "icon_item_staff_2h_0026.dds", "icon_item_staff_2h_0050.dds",
        "icon_item_mace_1h_0001.dds", "icon_item_mace_1h_0042.dds", "icon_item_mace_1h_0080.dds",
        "icon_item_spear_2h_0001.dds", "icon_item_spear_2h_0036.dds", "icon_item_spear_2h_0068.dds",
        "icon_item_bow_0001.dds", "icon_item_bow_0045.dds", "icon_item_bow_0088.dds",
        "icon_item_shield_0001.dds", "icon_item_shield_0048.dds", "icon_item_shield_0090.dds",
        "icon_item_arm_cloth_0008.dds", "icon_item_arm_leather_0008.dds", "icon_item_arm_metal_0008.dds",
        "icon_item_potion01.dds", "icon_item_potion04.dds", "icon_item_moonstone03.dds",
        "icon_item_0126.dds", "icon_item_0520.dds", "icon_item_0920.dds",
    ],
    "actions": [
        "interaction/icon_interaction01.dds", "interaction/icon_interaction10.dds",
        "interaction/icon_interaction20.dds", "interaction/icon_interaction35.dds",
        "interaction/icon_interaction47.dds", "interaction/icon_interaction60.dds",
        "interaction/icon_interaction75.dds", "interaction/icon_interaction90.dds",
        "emotion/icon_emotion_001.dds", "emotion/icon_emotion_008.dds",
        "emotion/icon_emotion_016.dds", "emotion/icon_emotion_024.dds",
        "emotion/icon_emotion_032.dds", "emotion/icon_emotion_040.dds",
        "emotion/icon_emotion_047.dds", "emotion/icon_emotion_049.dds",
        "craft/alchemy.dds", "craft/animal.dds", "craft/blacksmith.dds", "craft/cook.dds",
        "craft/farm.dds", "craft/fish.dds", "craft/metal.dds", "craft/woodwork.dds",
    ],
}


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="Refresh curated ArcheAge icon-style reference assets.")
    parser.add_argument("--extraction", type=Path, required=True, help="Full icon review folder")
    parser.add_argument("--skill", type=Path, required=True, help="archeage-icon-style skill folder")
    return parser.parse_args()


def read_manifest(path: Path) -> list[dict[str, str]]:
    with path.open("r", encoding="utf-8-sig", newline="") as stream:
        return list(csv.DictReader(stream))


def evenly_spaced(rows: list[dict[str, str]], count: int) -> list[str]:
    filenames = sorted({row["icon_filename"] for row in rows})
    if len(filenames) <= count:
        return filenames
    return [filenames[round(index * (len(filenames) - 1) / (count - 1))] for index in range(count)]


def status_selection(extraction: Path, category: str) -> list[str]:
    rows = [
        row for row in read_manifest(extraction / "manifests" / f"{category}.csv")
        if row["asset_status"] == "extracted"
        and "/" not in row["icon_filename"]
        and row["icon_filename"].startswith("icon_")
    ]
    small = [row for row in rows if row["dimensions"] == "28x28"]
    large = [row for row in rows if row["dimensions"] == "48x48"]
    return evenly_spaced(small, 12) + evenly_spaced(large, 12)


def create_atlas(title: str, entries: list[tuple[str, Path]], output: Path) -> None:
    columns = 6
    cell_width, cell_height = 218, 104
    header_height = 42
    rows = (len(entries) + columns - 1) // columns
    atlas = Image.new("RGBA", (columns * cell_width, header_height + rows * cell_height), "#1b1f25")
    draw = ImageDraw.Draw(atlas)
    title_font = ImageFont.load_default(size=20)
    label_font = ImageFont.load_default(size=12)
    draw.text((12, 10), title, fill="white", font=title_font)
    for index, (filename, path) in enumerate(entries):
        x = (index % columns) * cell_width
        y = header_height + (index // columns) * cell_height
        draw.rectangle((x, y, x + cell_width - 1, y + cell_height - 1), outline="#46505d")
        with Image.open(path) as source:
            icon = source.convert("RGBA")
            icon.thumbnail((72, 72), Image.Resampling.NEAREST)
            atlas.alpha_composite(icon, (x + 8, y + 8))
        label = filename.replace("icon_", "")
        if len(label) > 26:
            label = label[:23] + "..."
        draw.text((x + 88, y + 12), label, fill="#f4f7fb", font=label_font)
        draw.text((x + 88, y + 34), f"{source.width}x{source.height}", fill="#aab4c0", font=label_font)
    output.parent.mkdir(parents=True, exist_ok=True)
    atlas.convert("RGB").save(output, "PNG", optimize=True)


def main() -> None:
    args = parse_args()
    extraction = args.extraction.resolve()
    assets = args.skill.resolve() / "assets"
    refs = assets / "references"
    refs.mkdir(parents=True, exist_ok=True)

    selections = dict(CURATED)
    selections["buffs"] = status_selection(extraction, "buffs")
    selections["debuffs"] = status_selection(extraction, "debuffs")
    manifest_rows: list[dict[str, str]] = []

    for category, filenames in selections.items():
        category_directory = (refs / category).resolve()
        if not category_directory.is_relative_to(refs.resolve()):
            raise RuntimeError(f"Unsafe generated-reference path: {category_directory}")
        if category_directory.exists():
            shutil.rmtree(category_directory)
        entries: list[tuple[str, Path]] = []
        for filename in filenames:
            source = extraction / "all_icons" / "png" / Path(filename).with_suffix(".png")
            if not source.is_file():
                raise FileNotFoundError(f"Missing curated source: {source}")
            destination = refs / category / Path(filename).with_suffix(".png")
            destination.parent.mkdir(parents=True, exist_ok=True)
            shutil.copy2(source, destination)
            entries.append((filename, destination))
            with Image.open(source) as image:
                manifest_rows.append({
                    "category": category,
                    "source_filename": filename,
                    "asset_path": str(destination.relative_to(args.skill.resolve())).replace("\\", "/"),
                    "dimensions": f"{image.width}x{image.height}",
                })
        create_atlas(
            f"ArcheAge 1.2 {category.replace('_', ' ')} references",
            entries,
            assets / f"reference-atlas-{category}.png",
        )

    with (assets / "reference-manifest.csv").open("w", encoding="utf-8-sig", newline="") as stream:
        writer = csv.DictWriter(stream, fieldnames=["category", "source_filename", "asset_path", "dimensions"])
        writer.writeheader()
        writer.writerows(manifest_rows)
    print(f"Created {len(manifest_rows)} reference icons and {len(selections)} atlases")


if __name__ == "__main__":
    main()
