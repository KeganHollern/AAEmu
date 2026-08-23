from __future__ import annotations

import argparse
import json
import math
import struct
import sys
from pathlib import Path

import numpy as np
from PIL import Image


PROFILES = {
    "skill": {
        "dimensions": {(48, 48)}, "opaque": True,
        "saturation": (0.38, 0.82), "luminance": (0.18, 0.52),
        "border_luminance": (0.05, 0.40), "p99_luminance_min": 0.72,
        "references": "skills",
    },
    "item": {
        "dimensions": {(48, 48)}, "opaque": True,
        "saturation": (0.04, 0.42), "luminance": (0.14, 0.43),
        "border_luminance": (0.10, 0.38), "p99_luminance_min": 0.62,
        "references": "items",
    },
    "interaction": {
        "dimensions": {(48, 48)}, "alpha_coverage": (0.20, 0.80),
        "saturation": (0.08, 0.50), "luminance": (0.10, 0.45),
        "border_luminance": (0.00, 0.12), "p99_luminance_min": 0.62,
        "references": "actions",
    },
    "emotion": {
        "dimensions": {(48, 48)}, "opaque": True,
        "saturation": (0.08, 0.52), "luminance": (0.18, 0.56),
        "border_luminance": (0.14, 0.58), "p99_luminance_min": 0.68,
        "references": "actions",
    },
    "craft": {
        "dimensions": {(128, 128)}, "opaque": True,
        "saturation": (0.08, 0.50), "luminance": (0.20, 0.56),
        "border_luminance": (0.16, 0.56), "p99_luminance_min": 0.68,
        "references": "actions",
    },
    "buff": {
        "dimensions": {(28, 28), (48, 48)}, "opaque": True,
        "saturation": (0.20, 0.82), "luminance": (0.18, 0.70),
        "border_luminance": (0.10, 0.68), "p99_luminance_min": 0.62,
        "references": "buffs",
    },
    "debuff": {
        "dimensions": {(28, 28), (48, 48)}, "opaque": True,
        "saturation": (0.25, 0.88), "luminance": (0.12, 0.70),
        "border_luminance": (0.08, 0.68), "p99_luminance_min": 0.58,
        "references": "debuffs",
    },
}


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="Validate an icon against the measured ArcheAge 1.2 style envelope.")
    parser.add_argument("icon", type=Path)
    parser.add_argument("--category", choices=sorted(PROFILES), required=True)
    parser.add_argument("--json", action="store_true", dest="as_json")
    return parser.parse_args()


def load_rgba(path: Path) -> tuple[Image.Image, dict[str, object]]:
    technical: dict[str, object] = {"format": path.suffix.lower().lstrip(".")}
    if path.suffix.casefold() == ".dds":
        header = path.read_bytes()[:128]
        if len(header) >= 128 and header[:4] == b"DDS ":
            technical["dds_fourcc"] = header[84:88].decode("ascii", "replace").strip("\x00") or "RGB32"
            technical["dds_mip_count"] = struct.unpack_from("<I", header, 28)[0]
    with Image.open(path) as source:
        source.load()
        technical["source_mode"] = source.mode
        return source.convert("RGBA"), technical


def metrics(image: Image.Image) -> dict[str, float | str]:
    pixels = np.asarray(image).astype(np.float32) / 255.0
    rgb = pixels[:, :, :3]
    alpha = pixels[:, :, 3]
    visible = alpha > 0.04
    if not visible.any():
        return {
            "dimensions": f"{image.width}x{image.height}", "alpha_coverage": 0.0,
            "alpha_min": float(alpha.min()), "alpha_max": float(alpha.max()),
            "saturation": 0.0, "luminance": 0.0, "border_luminance": 0.0,
            "p99_luminance": 0.0,
        }
    maximum = rgb.max(axis=2)
    minimum = rgb.min(axis=2)
    saturation = np.divide(maximum - minimum, maximum, out=np.zeros_like(maximum), where=maximum > 0)
    luminance = 0.2126 * rgb[:, :, 0] + 0.7152 * rgb[:, :, 1] + 0.0722 * rgb[:, :, 2]
    border = np.concatenate((luminance[0], luminance[-1], luminance[:, 0], luminance[:, -1]))
    return {
        "dimensions": f"{image.width}x{image.height}",
        "alpha_coverage": float(visible.mean()),
        "alpha_min": float(alpha.min()),
        "alpha_max": float(alpha.max()),
        "saturation": float(saturation[visible].mean()),
        "luminance": float(luminance[visible].mean()),
        "border_luminance": float(border.mean()),
        "p99_luminance": float(np.quantile(luminance[visible], 0.99)),
    }


def style_feature(image: Image.Image) -> np.ndarray:
    rgba = image.convert("RGBA").resize((8, 8), Image.Resampling.BILINEAR)
    pixels = np.asarray(rgba).astype(np.float32) / 255.0
    pixels[:, :, :3] *= pixels[:, :, 3:4]
    return pixels.flatten()


def nearest_references(image: Image.Image, category: str, script_path: Path) -> list[dict[str, object]]:
    reference_root = script_path.parent.parent / "assets" / "references" / str(PROFILES[category]["references"])
    candidate = style_feature(image)
    found: list[tuple[float, Path]] = []
    for path in reference_root.rglob("*.png"):
        with Image.open(path) as reference:
            distance = float(math.sqrt(np.mean((candidate - style_feature(reference)) ** 2)))
        found.append((distance, path))
    found.sort(key=lambda value: value[0])
    return [
        {"filename": str(path.relative_to(reference_root)).replace("\\", "/"), "distance": round(distance, 4)}
        for distance, path in found[:5]
    ]


def check_range(value: float, allowed: tuple[float, float], name: str, findings: list[dict[str, str]]) -> None:
    if not allowed[0] <= value <= allowed[1]:
        findings.append({
            "severity": "warning",
            "check": name,
            "message": f"{value:.3f} is outside the reference envelope {allowed[0]:.3f}-{allowed[1]:.3f}",
        })


def validate(image: Image.Image, category: str, measured: dict[str, float | str]) -> list[dict[str, str]]:
    profile = PROFILES[category]
    findings: list[dict[str, str]] = []
    if image.size not in profile["dimensions"]:
        expected = ", ".join(f"{width}x{height}" for width, height in sorted(profile["dimensions"]))
        findings.append({
            "severity": "error", "check": "dimensions",
            "message": f"Found {image.width}x{image.height}; required native size is {expected}",
        })
    if profile.get("opaque") and float(measured["alpha_min"]) < 1.0:
        findings.append({
            "severity": "error", "check": "alpha",
            "message": "This icon family is fully opaque in the reference corpus; flatten every pixel to alpha 255",
        })
    if "alpha_coverage" in profile:
        check_range(float(measured["alpha_coverage"]), profile["alpha_coverage"], "alpha_coverage", findings)
    for field in ("saturation", "luminance", "border_luminance"):
        check_range(float(measured[field]), profile[field], field, findings)
    if float(measured["p99_luminance"]) < float(profile["p99_luminance_min"]):
        findings.append({
            "severity": "warning", "check": "highlight_range",
            "message": f"Brightest details are too subdued ({float(measured['p99_luminance']):.3f}); reference minimum is {profile['p99_luminance_min']:.3f}",
        })
    return findings


def validate_dds(technical: dict[str, object], image: Image.Image) -> list[dict[str, str]]:
    if technical.get("format") != "dds":
        return []
    findings: list[dict[str, str]] = []
    expected_mips = {(48, 48): 6, (28, 28): 5, (128, 128): 8}.get(image.size)
    if expected_mips is not None and technical.get("dds_mip_count") != expected_mips:
        findings.append({
            "severity": "error", "check": "dds_mip_count",
            "message": f"Found {technical.get('dds_mip_count')} mip levels; {image.width}x{image.height} references use {expected_mips}",
        })
    if technical.get("dds_fourcc") not in ("DXT1", "RGB32"):
        findings.append({
            "severity": "warning", "check": "dds_pixel_format",
            "message": f"Found {technical.get('dds_fourcc')}; reference icons use DXT1/BC1 or uncompressed RGB32",
        })
    return findings


def main() -> int:
    args = parse_args()
    path = args.icon.resolve()
    image, technical = load_rgba(path)
    measured = metrics(image)
    findings = validate(image, args.category, measured) + validate_dds(technical, image)
    report = {
        "icon": str(path), "category": args.category,
        "technical": technical, "metrics": measured,
        "findings": findings,
        "nearest_references": nearest_references(image, args.category, Path(__file__).resolve()),
        "result": "fail" if any(item["severity"] == "error" for item in findings) else "review" if findings else "pass",
    }
    if args.as_json:
        print(json.dumps(report, ensure_ascii=False, indent=2))
    else:
        print(f"{report['result'].upper()}: {path.name} as {args.category}")
        print("Metrics: " + ", ".join(f"{key}={value:.3f}" if isinstance(value, float) else f"{key}={value}" for key, value in measured.items()))
        for finding in findings:
            print(f"[{finding['severity'].upper()}] {finding['check']}: {finding['message']}")
        print("Nearest references: " + ", ".join(item["filename"] for item in report["nearest_references"]))
    if any(item["severity"] == "error" for item in findings):
        return 2
    if findings:
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
