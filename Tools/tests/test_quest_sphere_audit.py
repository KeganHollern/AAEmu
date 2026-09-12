import importlib.util
from contextlib import closing
import json
from pathlib import Path
import sqlite3
import tempfile
import unittest


SPEC = importlib.util.spec_from_file_location("quest_sphere_audit", Path(__file__).parents[1] / "quest_sphere_audit.py")
AUDIT = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(AUDIT)


class QuestSphereAuditTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.compact = self.root / "compact.sqlite3"
        self.client = self.root / "client"
        self.worlds = self.root / "Worlds"
        self.manifest = self.root / "exclusions.json"
        self.worlds.mkdir()
        with closing(sqlite3.connect(self.compact)) as db:
            db.executescript("""
                CREATE TABLE quest_contexts(id INTEGER, zone_id INTEGER);
                CREATE TABLE quest_components(id INTEGER, quest_context_id INTEGER, component_kind_id INTEGER);
                CREATE TABLE quest_acts(id INTEGER, quest_component_id INTEGER, act_detail_id INTEGER, act_detail_type TEXT);
                CREATE TABLE quest_act_obj_spheres(id INTEGER, sphere_id INTEGER);
                CREATE TABLE quest_act_check_spheres(id INTEGER, sphere_id INTEGER);
                CREATE TABLE quest_act_con_accept_npcs(id INTEGER, npc_id INTEGER);
                CREATE TABLE quest_act_con_accept_components(id INTEGER, quest_context_id INTEGER);
                CREATE TABLE spheres(id INTEGER);
                CREATE TABLE zones(id INTEGER, zone_key INTEGER, closed TEXT);
                CREATE TABLE item_accept_quests(quest_id INTEGER);
                CREATE TABLE accept_quest_effects(quest_id INTEGER);
                CREATE TABLE doodad_func_quests(quest_id INTEGER);
                CREATE TABLE sphere_accept_quest_quests(quest_id INTEGER);
                CREATE TABLE npcs(id INTEGER, engage_combat_give_quest_id INTEGER);
                INSERT INTO quest_contexts VALUES(1,9);
                INSERT INTO quest_components VALUES(10,1,4);
                INSERT INTO quest_acts VALUES(100,10,101,'QuestActObjSphere');
                INSERT INTO quest_act_obj_spheres VALUES(101,1001);
                INSERT INTO spheres VALUES(1001);
                INSERT INTO zones VALUES(9,142,'f');
            """)
        self.write_manifest()
        self.write_client()

    def sql(self, sql):
        with closing(sqlite3.connect(self.compact)) as db:
            db.executescript(sql)

    def write_manifest(self, exclusions=None, overrides=None):
        self.manifest.write_text(json.dumps({"schema_version": 1, "exclusions": exclusions or [],
                                            "world_overrides": overrides or []}))

    def write_client(self, world="main_world", component=10, quest=1, radius="5"):
        path = self.client / f"game/worlds/{world}/level_design/zone/142/client/quest_sign_sphere.g"
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(f"area\n qtype {quest}\n ctype {component}\n pos ( x 1, y 2, z 3 )\n radius {radius}\n")

    def write_supplement(self, world="main_world", **changes):
        value = {"Name": "Fixture", "QuestId": 1, "ComponentId": 10, "ZoneId": 142,
                 "X": 1, "Y": 2, "Z": 3, "Radius": 5}
        value.update(changes)
        path = self.worlds / world / "quest_spheres.json"
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(json.dumps([value]))

    def run_audit(self):
        return AUDIT.audit(self.compact, self.client, self.worlds, self.manifest)

    def missing_geometry(self):
        self.write_client(component=999, quest=999)

    def npc_exclusion(self):
        self.sql("""INSERT INTO quest_components VALUES(11,1,2);
                    INSERT INTO quest_acts VALUES(110,11,111,'QuestActConAcceptNpc');
                    INSERT INTO quest_act_con_accept_npcs VALUES(111,7);""")
        return {"quest_id": 1, "component_id": 10, "sphere_id": 1001, "world": "main_world",
                "reason": "no_npc_starter_placement", "npc_ids": [7], "evidence": "NPC7 has no placement."}

    def test_client_and_supplement_cover_objective_and_check(self):
        self.sql("""INSERT INTO quest_components VALUES(20,1,4);
                    INSERT INTO quest_acts VALUES(200,20,201,'QuestActCheckSphere');
                    INSERT INTO quest_act_check_spheres VALUES(201,1002);
                    INSERT INTO spheres VALUES(1002);""")
        self.write_supplement(ComponentId=20)
        result = self.run_audit()
        self.assertTrue(result["ok"], result["errors"])
        self.assertEqual(2, result["covered_count"])

    def test_orphan_components_are_not_live_objectives(self):
        self.sql("""INSERT INTO quest_components VALUES(20,2,4);
                    INSERT INTO quest_acts VALUES(200,20,201,'QuestActObjSphere');
                    INSERT INTO quest_act_obj_spheres VALUES(201,1002);""")
        result = self.run_audit()
        self.assertTrue(result["ok"])
        self.assertEqual(1, result["objective_count"])

    def test_missing_geometry_fails(self):
        self.missing_geometry()
        result = self.run_audit()
        self.assertFalse(result["ok"])
        self.assertEqual("missing", result["objectives"][0]["status"])

    def test_geometry_in_another_world_does_not_cover_objective(self):
        self.missing_geometry()
        self.write_supplement(world="other_world")
        result = self.run_audit()
        self.assertFalse(result["ok"])
        self.assertEqual(["other_world"], result["objectives"][0]["geometry_in_other_worlds"])

    def test_world_override_selects_the_destination_world(self):
        override = {"quest_id": 1, "component_id": 10, "world": "arena", "evidence": "The target is the arena."}
        self.write_manifest(overrides=[override])
        self.assertFalse(self.run_audit()["ok"])
        self.write_supplement(world="arena")
        self.assertTrue(self.run_audit()["ok"])

    def test_nonfinite_and_nonpositive_volumes_fail_even_with_valid_coverage(self):
        for changes in ({"Radius": 0}, {"Radius": -1}, {"Radius": float("nan")}, {"X": float("inf")}):
            with self.subTest(changes=changes):
                self.write_supplement(**changes)
                result = self.run_audit()
                self.assertFalse(result["ok"])
                self.assertTrue(any("finite coordinates" in error for error in result["errors"]))

    def test_invalid_client_volume_and_unparsed_records_fail(self):
        for radius in ("nan", "-3", "0", "invalid"):
            with self.subTest(radius=radius):
                self.write_client(radius=radius)
                self.assertFalse(self.run_audit()["ok"])

    def test_unknown_zone_key_and_wrong_quest_id_fail(self):
        self.write_supplement(ZoneId=999)
        self.assertTrue(any("zone key" in error for error in self.run_audit()["errors"]))
        self.write_supplement(QuestId=2)
        self.assertTrue(any("quest ID" in error for error in self.run_audit()["errors"]))

    def test_missing_starter_exclusion_expires_when_npc_is_placed(self):
        self.missing_geometry()
        self.write_manifest(exclusions=[self.npc_exclusion()])
        self.assertTrue(self.run_audit()["ok"])
        path = self.worlds / "main_world/npc_spawns.json"
        path.parent.mkdir(parents=True)
        path.write_text('[{"UnitId":7}]')
        result = self.run_audit()
        self.assertFalse(result["ok"])
        self.assertEqual("invalid_exclusion", result["objectives"][0]["status"])

    def test_missing_starter_exclusion_expires_with_alternate_starter(self):
        self.missing_geometry()
        self.write_manifest(exclusions=[self.npc_exclusion()])
        self.sql("INSERT INTO item_accept_quests VALUES(1);")
        self.assertFalse(self.run_audit()["ok"])

    def test_exclusion_expires_when_geometry_appears_or_objective_disappears(self):
        self.write_manifest(exclusions=[self.npc_exclusion()])
        self.assertTrue(any("geometry now covers" in error for error in self.run_audit()["errors"]))
        self.sql("DELETE FROM quest_acts WHERE id=100;")
        self.assertTrue(any("no longer matches" in error for error in self.run_audit()["errors"]))

    def test_closed_zone_exclusion_expires_when_zone_opens(self):
        self.missing_geometry()
        exclusion = {"quest_id": 1, "component_id": 10, "sphere_id": 1001, "world": "main_world",
                     "reason": "closed_zone", "zone_id": 9, "evidence": "Zone9 is closed."}
        self.write_manifest(exclusions=[exclusion])
        self.sql("UPDATE zones SET closed='t';")
        self.assertTrue(self.run_audit()["ok"])
        self.sql("UPDATE zones SET closed='f';")
        self.assertFalse(self.run_audit()["ok"])

    def test_missing_sphere_metadata_fails(self):
        self.sql("DELETE FROM spheres;")
        self.assertFalse(self.run_audit()["ok"])

    def test_no_starter_exclusion_expires_with_an_accept_act(self):
        self.missing_geometry()
        exclusion = {"quest_id": 1, "component_id": 10, "sphere_id": 1001, "world": "main_world",
                     "reason": "no_starter", "evidence": "The quest has no starter."}
        self.write_manifest(exclusions=[exclusion])
        self.assertTrue(self.run_audit()["ok"])
        self.npc_exclusion()
        self.assertFalse(self.run_audit()["ok"])

    def test_chain_exclusion_checks_every_entry_and_edge(self):
        self.missing_geometry()
        self.sql("""INSERT INTO quest_contexts VALUES(2,9),(3,9);
                    INSERT INTO quest_components VALUES(20,2,2),(21,2,8),(30,3,2),(31,3,8),(11,1,2);
                    INSERT INTO quest_act_con_accept_npcs VALUES(201,7);
                    INSERT INTO quest_act_con_accept_components VALUES(202,3),(301,3),(302,1),(111,1);
                    INSERT INTO quest_acts VALUES(200,20,201,'QuestActConAcceptNpc'),
                      (210,21,202,'QuestActConAcceptComponent'),(300,30,301,'QuestActConAcceptComponent'),
                      (310,31,302,'QuestActConAcceptComponent'),(110,11,111,'QuestActConAcceptComponent');""")
        exclusion = {"quest_id": 1, "component_id": 10, "sphere_id": 1001, "world": "main_world",
                     "reason": "unavailable_chain", "quest_chain": [2,3,1], "npc_ids": [7],
                     "evidence": "The chain starts at absent NPC7."}
        self.write_manifest(exclusions=[exclusion])
        self.assertTrue(self.run_audit()["ok"])
        self.sql("INSERT INTO item_accept_quests VALUES(3);")
        self.assertFalse(self.run_audit()["ok"])
        self.sql("DELETE FROM item_accept_quests; DELETE FROM quest_acts WHERE id=310;")
        self.assertFalse(self.run_audit()["ok"])

    def test_report_is_deterministic_and_database_is_unchanged(self):
        before = self.compact.read_bytes()
        first = self.run_audit()
        self.assertEqual(first, self.run_audit())
        self.assertEqual(before, self.compact.read_bytes())


if __name__ == "__main__":
    unittest.main()
