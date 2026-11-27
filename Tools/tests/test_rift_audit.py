import importlib.util
import json
from contextlib import closing
from pathlib import Path
import sqlite3
import tempfile
import unittest


SPEC = importlib.util.spec_from_file_location("rift_audit", Path(__file__).parents[1] / "rift_audit.py")
AUDIT = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(AUDIT)


class RiftGroupAuditTests(unittest.TestCase):
    def setUp(self):
        temp = tempfile.TemporaryDirectory()
        self.addCleanup(temp.cleanup)
        self.root = Path(temp.name)
        self.db = self.root / "compact.sqlite3"
        self.data = self.root / "AAEmu.Game/Data"
        (self.data / "TowerDefense").mkdir(parents=True)
        (self.data / "Worlds/main_world").mkdir(parents=True)
        self.sql("""
            CREATE TABLE tower_defs(id);
            CREATE TABLE tower_def_progs(id, tower_def_id);
            CREATE TABLE tower_def_prog_spawn_targets(tower_def_prog_id, spawn_target_id);
            CREATE TABLE tower_def_prog_kill_targets(tower_def_prog_id, kill_target_id, kill_count);
            CREATE TABLE npcs(id);
            CREATE TABLE np_skills(owner_id, skill_use_condition_id, skill_id);
            CREATE TABLE npc_spawners(id, maxPopulation);
            CREATE TABLE npc_spawner_npcs(npc_spawner_id, member_id, member_type);
            CREATE TABLE npc_groups(id);
            CREATE TABLE npc_group_members(npc_group_id, npc_id);
            INSERT INTO tower_defs VALUES (28);
            INSERT INTO tower_def_progs VALUES (1,28);
            INSERT INTO tower_def_prog_spawn_targets VALUES (1,16338);
            INSERT INTO tower_def_prog_kill_targets VALUES (1,14580,5),(1,14581,3);
            INSERT INTO npcs VALUES (14580),(14581),(14579);
            INSERT INTO npc_spawners VALUES (16338,1),(9,1);
            INSERT INTO npc_spawner_npcs VALUES (16338,177,'NpcGroup'),(9,14581,'Npc');
            INSERT INTO npc_groups VALUES (177);
            INSERT INTO npc_group_members VALUES (177,14580),(177,14580),(177,14580),(177,14581),(177,14581);
        """)

    def sql(self, script):
        with closing(sqlite3.connect(self.db)) as db:
            db.executescript(script)

    def audit(self, pins=(16338, 16338), bindings=None):
        placements = [{"EventPlacementId": f"p{i}", "EventSiteKey": "lilyut", "UnitId": 14579,
                       "NpcSpawnerIds": list(pin) if isinstance(pin, tuple) else [pin],
                       "StartInactive": True} for i, pin in enumerate(pins)]
        if bindings is None:
            bindings = [p["EventPlacementId"] for p in placements]
        site = {"key": "lilyut", "bindings": {"16338": bindings}}
        event = {"key": "oblivion", "towerDefId": 28, "sites": [site]}
        (self.data / "TowerDefense/retail-rifts.json").write_text(json.dumps({"events": [event]}))
        (self.data / "Worlds/main_world/npc_spawns_tower_defense.json").write_text(json.dumps(placements))
        return AUDIT.audit(self.db, self.root)["audited_steps"][0]["objectives"]

    def test_two_distinct_formations_supply_the_wave(self):
        self.assertEqual({"14580": "6/5", "14581": "4/3"}, self.audit())

    def test_one_formation_is_short(self):
        with self.assertRaisesRegex(ValueError, "14580: 3/5"):
            self.audit((16338,))

    def test_reusing_one_placement_does_not_add_another_formation(self):
        with self.assertRaisesRegex(ValueError, "14580: 3/5"):
            self.audit((16338,), ["p0", "p0"])

    def test_group_does_not_spawn_the_placement_unit(self):
        self.sql("UPDATE tower_def_prog_kill_targets SET kill_target_id=14579, kill_count=1 WHERE kill_target_id=14580;")
        with self.assertRaisesRegex(ValueError, "14579: 0/1"):
            self.audit()

    def test_plain_pin_uses_placement_unit(self):
        self.sql("DELETE FROM tower_def_prog_kill_targets; INSERT INTO tower_def_prog_kill_targets VALUES (1,14579,1);")
        self.assertEqual({"14579": "1/1"}, self.audit((9,)))

    def add_controller(self):
        self.sql("""
            CREATE TABLE skills(id, plot_id, target_relation_id);
            CREATE TABLE skill_effects(skill_id, effect_id);
            CREATE TABLE effects(id, actual_type, actual_id);
            CREATE TABLE npc_spawner_spawn_effects(id, spawner_id);
            INSERT INTO np_skills VALUES (14579,5,7);
            INSERT INTO skills VALUES (7,0,0);
            INSERT INTO skill_effects VALUES (7,1);
            INSERT INTO effects VALUES (1,'NpcSpawnerSpawnEffect',2);
            INSERT INTO npc_spawner_spawn_effects VALUES (2,16338);
        """)

    def test_controller_can_summon_a_group_at_its_own_site(self):
        self.add_controller()
        self.assertEqual({"14580": "6/5", "14581": "4/3"}, self.audit((9, 16338, 16338), ["p0"]))

    def test_two_summons_cannot_claim_one_group_twice(self):
        self.add_controller()
        with self.assertRaisesRegex(ValueError, "Group placement p2 is reused"):
            self.audit((9, 9, 16338), ["p0", "p1"])

    def test_summon_matches_the_indexed_template(self):
        self.add_controller()
        with self.assertRaisesRegex(ValueError, "Missing effect placements"):
            self.audit((9, (16338, 9)), ["p0"])

    def test_ambiguous_group_selection_needs_review(self):
        self.sql("INSERT INTO npc_spawner_npcs VALUES (16338,14581,'Npc');")
        with self.assertRaisesRegex(ValueError, "16338.*multiple"):
            self.audit()

    def test_empty_group_cannot_satisfy_an_objective(self):
        self.sql("DELETE FROM npc_group_members;")
        with self.assertRaisesRegex(ValueError, "177.*no members"):
            self.audit()

    def test_group_needs_a_population_slot(self):
        self.sql("UPDATE npc_spawners SET maxPopulation=0 WHERE id=16338;")
        with self.assertRaisesRegex(ValueError, "16338.*zero capacity"):
            self.audit()


if __name__ == "__main__":
    unittest.main()
