#!/usr/bin/env python3
"""Check r208022 quest objective sphere coverage without changing compact data."""

from __future__ import annotations

import argparse
from collections import defaultdict
import hashlib
import json
import math
from pathlib import Path
import re
import sqlite3
import sys


ACT_TABLES = {
    "QuestActObjSphere": "quest_act_obj_spheres",
    "QuestActCheckSphere": "quest_act_check_spheres",
}
DEFAULT_MANIFEST = Path(__file__).resolve().parents[1] / "Docs/customized/quest-spheres/exclusions.json"
NUMBER = r"[-+]?(?:\d+(?:\.\d*)?|\.\d+)(?:[eE][-+]?\d+)?|[-+]?(?:nan|inf(?:inity)?)"
CLIENT_AREA = re.compile(
    rf"area\s+qtype\s+(\d+)\s+ctype\s+(\d+)\s+"
    rf"pos\s*\(\s*x\s*({NUMBER})\s*,\s*y\s*({NUMBER})\s*,\s*z\s*({NUMBER})\s*\)\s+"
    rf"radius\s+({NUMBER})",
    re.IGNORECASE,
)


def load_json(path: Path):
    # The world files allow comments and trailing commas. Preserve quoted strings.
    text = path.read_text(encoding="utf-8-sig")
    text = re.sub(r'("(?:\\.|[^"\\])*")|//[^\n]*|/\*.*?\*/',
                  lambda m: m.group(1) or "", text, flags=re.DOTALL)
    text = re.sub(r',(?=\s*[}\]])', '', text)
    return json.loads(text)


def file_hash(path: Path) -> str:
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def input_digest(root: Path, paths) -> dict:
    entries = [{"path": path.relative_to(root).as_posix(), "sha256": file_hash(path)}
               for path in sorted(paths)]
    canonical = json.dumps(entries, sort_keys=True, separators=(",", ":")) + "\n"
    return {"file_count": len(entries), "sha256": hashlib.sha256(canonical.encode("utf-8")).hexdigest()}


def objective_rows(connection: sqlite3.Connection) -> list[dict]:
    result = []
    for act_type, detail_table in ACT_TABLES.items():
        rows = connection.execute(
            f"""SELECT q.id AS quest_id, q.zone_id, c.id AS component_id,
                       a.id AS act_id, a.act_detail_id, d.sphere_id
                FROM quest_acts a
                JOIN quest_components c ON c.id = a.quest_component_id
                JOIN quest_contexts q ON q.id = c.quest_context_id
                LEFT JOIN {detail_table} d ON d.id = a.act_detail_id
                WHERE a.act_detail_type = ?""", (act_type,))
        result.extend(dict(row, act_type=act_type) for row in rows)
    return sorted(result, key=lambda row: (row["quest_id"], row["component_id"], row["act_id"]))


def valid_volume(volume: dict) -> bool:
    try:
        values = [volume[key] for key in ("x", "y", "z", "radius")]
        return all(isinstance(value, (int, float)) and not isinstance(value, bool)
                   and math.isfinite(value) for value in values) and volume["radius"] > 0
    except (KeyError, TypeError):
        return False


def read_volumes(client_root: Path, world_data: Path, errors: list[str]):
    volumes = []
    client_files = sorted(client_root.rglob("quest_sign_sphere.g"))
    if not client_files:
        errors.append("The client root contains no quest_sign_sphere.g files.")
    for path in client_files:
        relative = path.relative_to(client_root).as_posix()
        match = re.search(r"(?:^|/)worlds/([^/]+)/level_design/zone/(\d+)/(?:client/)?quest_sign_sphere\.g$", relative)
        if not match:
            errors.append(f"The client geometry path has no world and zone: {relative}")
            continue
        world, zone = match.group(1), int(match.group(2))
        content = path.read_text(encoding="utf-8-sig")
        matches = list(CLIENT_AREA.finditer(content))
        if CLIENT_AREA.sub("", content).strip():
            errors.append(f"The client geometry has an invalid area record: {relative}")
        for index, area in enumerate(matches):
            quest, component = (int(area[index]) for index in (1, 2))
            x, y, z, radius = (float(area[index]) for index in (3, 4, 5, 6))
            volumes.append({"world": world, "zone_key": zone, "quest_id": quest,
                            "component_id": component, "x": x, "y": y, "z": z,
                            "radius": radius, "source": relative, "source_kind": "client",
                            "record": index})
    for path in sorted(world_data.glob("*/quest_spheres.json")):
        relative = path.relative_to(world_data).as_posix()
        rows = load_json(path)
        if not isinstance(rows, list):
            errors.append(f"The supplement must contain an array: {relative}")
            continue
        for index, row in enumerate(rows):
            try:
                volumes.append({"world": path.parent.name, "zone_key": row["ZoneId"],
                                "quest_id": row["QuestId"], "component_id": row["ComponentId"],
                                "x": row["X"], "y": row["Y"], "z": row["Z"],
                                "radius": row["Radius"], "source": relative,
                                "source_kind": "supplement", "record": index})
            except (KeyError, TypeError):
                errors.append(f"The supplement record lacks a required field: {relative}[{index}]")
    for volume in volumes:
        if not valid_volume(volume):
            errors.append(f"The volume must have finite coordinates and a positive radius: "
                          f"{volume['source']}[{volume['record']}]")
    return volumes


def starter_evidence(connection: sqlite3.Connection, quest_id: int, placements: set[int]):
    acts = list(connection.execute(
        """SELECT a.act_detail_type, a.act_detail_id, n.npc_id, d.quest_context_id AS chain_quest_id
           FROM quest_acts a JOIN quest_components c ON c.id = a.quest_component_id
           LEFT JOIN quest_act_con_accept_npcs n
             ON a.act_detail_type = 'QuestActConAcceptNpc' AND n.id = a.act_detail_id
           LEFT JOIN quest_act_con_accept_components d
             ON a.act_detail_type = 'QuestActConAcceptComponent' AND d.id = a.act_detail_id
           WHERE c.quest_context_id = ? AND c.component_kind_id = 2
             AND a.act_detail_type LIKE 'QuestActConAccept%'""",
        (quest_id,)))
    alternate = []
    for table in ("item_accept_quests", "accept_quest_effects", "doodad_func_quests", "sphere_accept_quest_quests"):
        if connection.execute(f"SELECT 1 FROM {table} WHERE quest_id = ? LIMIT 1", (quest_id,)).fetchone():
            alternate.append(table)
    inbound = sorted({row[0] for row in connection.execute(
        """SELECT c.quest_context_id FROM quest_act_con_accept_components d
           JOIN quest_acts a ON a.act_detail_id=d.id AND a.act_detail_type='QuestActConAcceptComponent'
           JOIN quest_components c ON c.id=a.quest_component_id
           JOIN quest_contexts q ON q.id=c.quest_context_id
           WHERE d.quest_context_id=? AND c.quest_context_id<>?""", (quest_id, quest_id))})
    if connection.execute("SELECT 1 FROM npcs WHERE engage_combat_give_quest_id = ? LIMIT 1", (quest_id,)).fetchone():
        alternate.append("npcs.engage_combat_give_quest_id")
    npc_ids = sorted({row["npc_id"] for row in acts if row["npc_id"] is not None})
    return {"acts": [dict(row) for row in acts], "npc_ids": npc_ids,
            "placed_npc_ids": sorted(set(npc_ids) & placements), "alternate": alternate,
            "inbound_quests": inbound}


def exclusion_holds(exclusion, objective, connection, placements):
    """Each exclusion is a checked claim, not a permanent ignored component ID."""
    reason = exclusion.get("reason")
    if not exclusion.get("evidence"):
        return False, "The exclusion has no evidence."
    if reason == "closed_zone":
        zone_id = exclusion.get("zone_id")
        zone = connection.execute("SELECT closed FROM zones WHERE id = ?", (zone_id,)).fetchone()
        holds = zone_id == objective["zone_id"] and zone is not None and zone["closed"] in (1, "t", "true")
        return holds, "The quest must still belong to the recorded closed zone."
    if reason in ("no_npc_starter_placement", "no_starter"):
        evidence = starter_evidence(connection, objective["quest_id"], placements)
        if reason == "no_starter":
            return not evidence["acts"] and not evidence["alternate"] and not evidence["inbound_quests"], "The quest must still have no starter."
        expected = sorted(exclusion.get("npc_ids", []))
        holds = (bool(expected) and evidence["npc_ids"] == expected and not evidence["placed_npc_ids"]
                 and not evidence["alternate"] and not evidence["inbound_quests"]
                 and all(row["act_detail_type"] == "QuestActConAcceptNpc" for row in evidence["acts"]))
        return holds, "The recorded NPC starters must still have no placement or alternate starter."
    if reason == "unavailable_chain":
        chain = exclusion.get("quest_chain", [])
        if len(chain) < 2 or chain[-1] != objective["quest_id"] or len(chain) != len(set(chain)):
            return False, "The exclusion must record a chain that ends at this quest."
        entry = starter_evidence(connection, chain[0], placements)
        holds = (entry["npc_ids"] == sorted(exclusion.get("npc_ids", [])) and bool(entry["npc_ids"])
                 and not entry["placed_npc_ids"] and not entry["alternate"] and not entry["inbound_quests"]
                 and all(act["act_detail_type"] == "QuestActConAcceptNpc" for act in entry["acts"]))
        for previous, quest_id in zip(chain, chain[1:]):
            evidence = starter_evidence(connection, quest_id, placements)
            holds &= (evidence["inbound_quests"] == [previous] and not evidence["alternate"]
                      and bool(evidence["acts"]) and all(
                          act["act_detail_type"] == "QuestActConAcceptComponent" and act["chain_quest_id"] == quest_id
                          for act in evidence["acts"]))
        return bool(holds), "The chain must still have only its recorded entry NPC and predecessor edges."
    return False, f"The exclusion reason is unknown: {reason}"


def audit(compact: Path, client_root: Path, world_data: Path, manifest_path: Path) -> dict:
    errors = []
    manifest = load_json(manifest_path)
    if manifest.get("schema_version") != 1:
        raise ValueError("The exclusion manifest must use schema_version 1.")
    connection = sqlite3.connect(compact.resolve().as_uri() + "?mode=ro", uri=True)
    connection.row_factory = sqlite3.Row
    try:
        objectives = objective_rows(connection)
        volumes = read_volumes(client_root, world_data, errors)
        geometry = defaultdict(list)
        for volume in volumes:
            geometry[(volume["world"], volume["component_id"])].append(volume)
            if not connection.execute("SELECT 1 FROM zones WHERE zone_key=?", (volume["zone_key"],)).fetchone():
                errors.append(f"The volume has no matching compact zone key: {volume['source']}[{volume['record']}]")
        placements = {row["UnitId"] for path in sorted(world_data.glob("*/npc_spawns*.json"))
                      for row in load_json(path) if "UnitId" in row}
        overrides = {(row["quest_id"], row["component_id"]): row for row in manifest.get("world_overrides", [])}
        exclusions = {}
        for row in manifest.get("exclusions", []):
            key = (row["quest_id"], row["component_id"], row["sphere_id"])
            if key in exclusions:
                errors.append(f"The manifest repeats an exclusion: {key}")
            exclusions[key] = row
        used_exclusions, used_overrides = set(), set()
        results = []
        for objective in objectives:
            key = (objective["quest_id"], objective["component_id"], objective["sphere_id"])
            component_key = key[:2]
            override = overrides.get(component_key)
            world = override["world"] if override else "main_world"
            if override:
                used_overrides.add(component_key)
                if not override.get("evidence"):
                    errors.append(f"The world override has no evidence: {component_key}")
            matches = geometry.get((world, objective["component_id"]), [])
            accepted = [volume for volume in matches if volume["quest_id"] == objective["quest_id"] and valid_volume(volume)]
            for volume in matches:
                if volume["quest_id"] != objective["quest_id"]:
                    errors.append(f"The geometry quest ID differs from its component: {volume['source']}[{volume['record']}]")
            result = dict(objective, world=world, volume_count=len(accepted))
            exclusion = exclusions.get(key)
            if objective["sphere_id"] is None or not connection.execute(
                    "SELECT 1 FROM spheres WHERE id=?", (objective["sphere_id"],)).fetchone():
                errors.append(f"The objective has no sphere metadata: {key}")
            if accepted:
                result["status"] = "covered"
                result["sources"] = sorted({volume["source"] for volume in accepted})
                if exclusion:
                    used_exclusions.add(key)
                    errors.append(f"The exclusion is stale because geometry now covers the objective: {key}")
            elif exclusion:
                used_exclusions.add(key)
                holds, explanation = exclusion_holds(exclusion, objective, connection, placements)
                if exclusion.get("world") != world:
                    holds, explanation = False, "The exclusion world differs from the objective world."
                result["status"] = "excluded" if holds else "invalid_exclusion"
                result["reason"] = exclusion.get("reason")
                if not holds:
                    errors.append(f"The exclusion is stale: {key}. {explanation}")
            else:
                result["status"] = "missing"
                other_worlds = sorted({volume["world"] for volume in volumes
                                       if volume["component_id"] == objective["component_id"] and volume["world"] != world})
                result["geometry_in_other_worlds"] = other_worlds
                errors.append(f"The objective has no geometry in {world}: {key}")
            results.append(result)
        for key in sorted(set(exclusions) - used_exclusions):
            errors.append(f"The exclusion no longer matches a live objective: {key}")
        for key in sorted(set(overrides) - used_overrides):
            errors.append(f"The world override no longer matches a live objective: {key}")
        return {"schema_version": 1, "compact_sha256": file_hash(compact),
                "manifest_sha256": file_hash(manifest_path), "objective_count": len(results),
                "client_geometry": input_digest(client_root, client_root.rglob("quest_sign_sphere.g")),
                "supplements": input_digest(world_data, world_data.glob("*/quest_spheres.json")),
                "npc_placements": input_digest(world_data, world_data.glob("*/npc_spawns*.json")),
                "covered_count": sum(row["status"] == "covered" for row in results),
                "excluded_count": sum(row["status"] == "excluded" for row in results),
                "client_volume_count": sum(row["source_kind"] == "client" for row in volumes),
                "supplement_volume_count": sum(row["source_kind"] == "supplement" for row in volumes),
                "objectives": results, "errors": sorted(set(errors)), "ok": not errors}
    finally:
        connection.close()


def main(argv=None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--compact", required=True, type=Path)
    parser.add_argument("--client-root", required=True, type=Path)
    parser.add_argument("--world-data", required=True, type=Path)
    parser.add_argument("--exclusions", type=Path, default=DEFAULT_MANIFEST)
    parser.add_argument("--report", required=True, type=Path)
    args = parser.parse_args(argv)
    try:
        result = audit(args.compact, args.client_root, args.world_data, args.exclusions)
    except (OSError, ValueError, KeyError, TypeError, sqlite3.Error) as error:
        result = {"schema_version": 1, "ok": False, "errors": [str(error)]}
    args.report.parent.mkdir(parents=True, exist_ok=True)
    args.report.write_text(json.dumps(result, indent=2, sort_keys=True, allow_nan=False) + "\n", encoding="utf-8")
    if result["ok"]:
        print(f"The audit passed: {result['covered_count']} objectives have geometry and "
              f"{result['excluded_count']} objectives have checked exclusions.")
        return 0
    for error in result["errors"]:
        print(error, file=sys.stderr)
    return 1


if __name__ == "__main__":
    raise SystemExit(main())
