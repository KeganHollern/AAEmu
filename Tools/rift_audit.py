"""Read-only r208022 rift data audit; does not substitute for runtime or client tests."""
import argparse
from collections import Counter
import heapq
import itertools
import json
from pathlib import Path
import sqlite3


def audit(database, source):
    connection = sqlite3.connect(database.resolve().as_uri() + "?mode=ro", uri=True)
    connection.row_factory = sqlite3.Row
    def rows(query, *args):
        return [dict(row) for row in connection.execute(query, args)]
    def one(query, *args):
        result = rows(query, *args)
        if len(result) != 1:
            raise ValueError(f"Expected one row: {query} {args}; found {len(result)}")
        return result[0]

    data = source / "AAEmu.Game/Data"
    manifest = json.loads((data / "TowerDefense/retail-rifts.json").read_text(encoding="utf-8-sig"))
    placements = json.loads((data / "Worlds/main_world/npc_spawns_tower_defense.json").read_text(encoding="utf-8-sig"))
    by_id = {p["EventPlacementId"]: p for p in placements}
    if len(by_id) != len(placements):
        raise ValueError("Duplicate event placement ID")
    checked_plots = set()

    def plot_spawns(plot_id):
        nodes = {r["id"]: r for r in rows("SELECT * FROM plot_events WHERE plot_id=?", plot_id)}
        if not nodes:
            raise ValueError(f"Empty plot {plot_id}")
        queue = [(0, 0, min(nodes.values(), key=lambda n: n["position"])["id"])]
        sequence = itertools.count(1)
        visits, summons = Counter(), Counter()
        operations = 0
        while queue:
            now, _, node_id = heapq.heappop(queue)
            operations += 1
            if operations > 10000:
                raise ValueError(f"Unbounded or oversized plot {plot_id}")
            node = nodes[node_id]
            visits[node_id] += 1
            # Runtime treats tickets=1 as unrestricted, including wave-three summon leaves.
            if node["tickets"] > 1 and visits[node_id] > node["tickets"]:
                continue
            if rows("SELECT * FROM plot_event_conditions WHERE event_id=?", node_id):
                raise ValueError(f"Plot {plot_id} node {node_id}: conditional path needs explicit audit")
            for effect in rows("SELECT * FROM plot_effects WHERE event_id=?", node_id):
                if effect["actual_type"] != "SpawnEffect":
                    continue
                spawn = one("SELECT * FROM spawn_effects WHERE id=?", effect["actual_id"])
                if (effect["source_id"], effect["target_id"], spawn["owner_type_id"],
                    spawn["pos_dir_id"], spawn["ori_dir_id"]) != (1, 5, 1, 1, 2):
                    raise ValueError(f"Unexpected rift summon selectors: {effect}")
                if node["target_update_method_id"] != 4:
                    raise ValueError(f"Summon must reuse impact location: {node_id}")
                incoming = rows("SELECT * FROM plot_next_events WHERE next_event_id=?", node_id)
                for edge in incoming:
                    impact = nodes[edge["event_id"]]
                    if impact["target_update_method_param4"] != 500000 or edge["delay"] not in (800, 1000, 1200):
                        raise ValueError(f"Missing terrain impact / arrival delay at {node_id}")
                summons[spawn["sub_type"]] += 1
            for edge in rows("SELECT * FROM plot_next_events WHERE event_id=? ORDER BY position", node_id):
                if edge["fail"] == "t":
                    continue
                if edge["per_target"] == "t" or edge["speed"] or edge["casting"] == "t":
                    raise ValueError(f"Unsupported timing in audited rift plot: {edge}")
                heapq.heappush(queue, (now + edge["delay"], next(sequence), edge["next_event_id"]))
        checked_plots.add(plot_id)
        return summons

    def npc_outputs(npc_id, site, ancestors=()):
        if npc_id in ancestors:
            raise ValueError(f"Recursive NPC summon cycle: {ancestors} -> {npc_id}")
        one("SELECT id FROM npcs WHERE id=?", npc_id)
        output = Counter({npc_id: 1})
        for npc_skill in rows("SELECT * FROM np_skills WHERE owner_id=? AND skill_use_condition_id=5", npc_id):
            skill = one("SELECT id,plot_id,target_relation_id FROM skills WHERE id=?", npc_skill["skill_id"])
            if skill["target_relation_id"] != 0:
                raise ValueError(f"Controller {npc_id}: self-cast relation requires review")
            if skill["plot_id"]:
                # Decorative initial portal plots have no NPC summon effects.
                has_spawns = rows("SELECT pe.id FROM plot_effects pe JOIN plot_events n ON n.id=pe.event_id "
                                  "WHERE n.plot_id=? AND pe.actual_type='SpawnEffect'", skill["plot_id"])
                if has_spawns:
                    for child, count in plot_spawns(skill["plot_id"]).items():
                        # Verify the dynamic-spawn lookup used by SpawnEffect.
                        member = rows("SELECT npc_spawner_id FROM npc_spawner_npcs WHERE member_id=? AND member_type='Npc'", child)
                        if not member:
                            raise ValueError(f"No dynamic spawner for NPC {child}")
                        output.update({child: count})
                continue
            for effect in rows("SELECT e.* FROM skill_effects se JOIN effects e ON e.id=se.effect_id WHERE se.skill_id=?", skill["id"]):
                if effect["actual_type"] != "NpcSpawnerSpawnEffect":
                    continue
                spawn = one("SELECT * FROM npc_spawner_spawn_effects WHERE id=?", effect["actual_id"])
                children = [p for p in placements if p["EventSiteKey"] == site and spawn["spawner_id"] in p["NpcSpawnerIds"]]
                if not children:
                    raise ValueError(f"Missing effect placements: site={site} spawner={spawn['spawner_id']}")
                for child in children:
                    output.update(npc_outputs(child["UnitId"], site, ancestors + (npc_id,)))
        return output

    report = []
    for event in manifest["events"]:
        definition = one("SELECT * FROM tower_defs WHERE id=?", event["towerDefId"])
        for site in event["sites"]:
            total_available = Counter()
            for binding, ids in site["bindings"].items():
                for placement_id in ids:
                    placement = by_id[placement_id]
                    if placement["EventSiteKey"] != site["key"] or not placement["StartInactive"]:
                        raise ValueError(f"Incorrect site isolation: {placement_id}")
                    if event["key"].startswith("rift.crimson.") and binding != "initial":
                        if placement["Position"]["Z"] < site["anchor"]["z"] + 50:
                            raise ValueError(f"Crimson controller is not airborne: {placement_id}")
            for step in rows("SELECT * FROM tower_def_progs WHERE tower_def_id=? ORDER BY id", definition["id"]):
                available = Counter()
                for target in rows("SELECT * FROM tower_def_prog_spawn_targets WHERE tower_def_prog_id=?", step["id"]):
                    for placement_id in site["bindings"][str(target["spawn_target_id"])]:
                        available.update(npc_outputs(by_id[placement_id]["UnitId"], site["key"]))
                objectives = rows("SELECT * FROM tower_def_prog_kill_targets WHERE tower_def_prog_id=?", step["id"])
                total_available.update(available)
                for objective in objectives:
                    npc_id, count = objective["kill_target_id"], objective["kill_count"]
                    if available[npc_id] < count:
                        raise ValueError(f"Unreachable objective: {event['key']} {site['key']} step={step['id']} NPC={npc_id}: {available[npc_id]}/{count}")
                if available:
                    report.append({"event": event["key"], "site": site["key"], "step": step["id"],
                                   "objectives": {str(o["kill_target_id"]): f"{available[o['kill_target_id']]}/{o['kill_count']}" for o in objectives}})
            completion = event.get("completionTarget")
            if completion and total_available[completion["npcId"]] < completion["count"]:
                raise ValueError(f"Unreachable completion target: {event['key']} {site['key']}")
    connection.close()
    return {"audited_steps": report, "summon_plots": sorted(checked_plots), "placements": len(placements)}


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("database", type=Path)
    parser.add_argument("--source", type=Path, default=Path(__file__).resolve().parents[1])
    args = parser.parse_args()
    print(json.dumps(audit(args.database, args.source), indent=2))
