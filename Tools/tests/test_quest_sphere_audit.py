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

    def add_starter_fixture(self):
        self.sql("""
            ALTER TABLE quest_contexts ADD COLUMN category_id INTEGER;
            ALTER TABLE spheres ADD COLUMN sphere_detail_type TEXT;
            ALTER TABLE spheres ADD COLUMN sphere_detail_id INTEGER;
            CREATE TABLE sphere_quests(id INTEGER, quest_id INTEGER);
            CREATE TABLE quest_act_con_accept_spheres(id INTEGER, sphere_id INTEGER);
            INSERT INTO quest_components VALUES(11,1,2);
            INSERT INTO quest_acts VALUES(111,11,200,'QuestActConAcceptSphere');
            INSERT INTO quest_act_con_accept_spheres VALUES(200,2000);
            INSERT INTO spheres VALUES(2000,'SphereQuest',9000);
            INSERT INTO sphere_quests VALUES(9000,1);
        """)
        world = self.client / "game/worlds/main_world/world.xml"
        world.write_text('<World name="main_world"><ZoneList><Zone id="142"/></ZoneList></World>')

    def with_starters(self):
        return AUDIT.audit(self.compact, self.client, self.worlds, self.manifest, include_starters=True)

    def test_starter_needs_component_geometry_even_with_another_start_reference(self):
        self.add_starter_fixture()
        self.sql("INSERT INTO item_accept_quests VALUES(1); UPDATE zones SET closed='t';")
        result = self.with_starters()
        self.assertTrue(result["ok"], result["errors"])
        starter, = result["starter_inventory"]["components"]
        self.assertEqual("missing", starter["geometry_status"])
        self.assertEqual([10], starter["other_geometry_components"])
        self.assertEqual(["item_accept_quests"], starter["other_quest_references"])
        self.assertTrue(starter["quest_zone_closed"])
        self.assertEqual(["main_world"], starter["quest_zone_worlds"])

    def test_component_only_hint_does_not_assign_an_area_to_each_sphere_id(self):
        self.add_starter_fixture()
        self.write_supplement(ComponentId=11)
        self.assertEqual({"single_binding": 1}, self.with_starters()["starter_inventory"]["geometry_counts"])
        self.sql("""INSERT INTO quest_acts VALUES(112,11,201,'QuestActConAcceptSphere');
                    INSERT INTO quest_act_con_accept_spheres VALUES(201,2001);
                    INSERT INTO spheres VALUES(2001,'SphereQuest',9001);
                    INSERT INTO sphere_quests VALUES(9001,1);""")
        inventory = self.with_starters()["starter_inventory"]
        self.assertEqual((2, 1), (inventory["act_count"], inventory["component_count"]))
        starter, = inventory["components"]
        self.assertEqual("component_only", starter["geometry_status"])
        self.assertEqual([2000, 2001], starter["quest_sphere_bindings"])

    def test_starter_geometry_with_another_quest_binding_is_not_linked(self):
        self.add_starter_fixture()
        self.write_supplement(ComponentId=11)
        self.sql("UPDATE sphere_quests SET quest_id=2;")
        starter, = self.with_starters()["starter_inventory"]["components"]
        self.assertEqual("component_only", starter["geometry_status"])
        self.assertEqual([], starter["quest_sphere_bindings"])
        self.assertEqual(2, starter["acts"][0]["mapped_quest_id"])

    def test_starter_geometry_requires_its_own_world_and_quest(self):
        self.add_starter_fixture()
        self.write_supplement(world="other_world", ComponentId=11)
        result = self.with_starters()
        self.assertTrue(any("does not belong to its world" in message for message in result["errors"]))
        self.write_supplement(ComponentId=11, QuestId=2)
        result = self.with_starters()
        self.assertTrue(any("quest ID differs" in message for message in result["errors"]))
        self.assertEqual({"missing": 1}, result["starter_inventory"]["geometry_counts"])

    def test_unsupported_binding_remains_a_quest_reference(self):
        self.add_starter_fixture()
        self.write_supplement(ComponentId=11)
        self.sql("""UPDATE spheres SET sphere_detail_type='SphereAcceptQuest' WHERE id=2000;
                    INSERT INTO sphere_accept_quest_quests VALUES(1);""")
        result = self.with_starters()
        self.assertTrue(result["ok"], result["errors"])
        starter, = result["starter_inventory"]["components"]
        self.assertEqual("component_only", starter["geometry_status"])
        self.assertEqual(["sphere_accept_quest_quests"], starter["other_quest_references"])
        self.assertEqual([], starter["quest_sphere_bindings"])

    def test_starter_world_definition_and_sphere_metadata_are_checked(self):
        self.add_starter_fixture()
        world = self.client / "game/worlds/main_world/world.xml"
        world.write_text('<World name="different_world"/>')
        self.sql("DELETE FROM spheres WHERE id=2000;")
        result = self.with_starters()
        self.assertFalse(result["ok"])
        self.assertTrue(any("world definition" in message for message in result["errors"]))
        self.assertTrue(any("no sphere metadata" in message for message in result["errors"]))

    def test_orphan_and_non_start_components_do_not_enter_starter_inventory(self):
        self.add_starter_fixture()
        self.sql("""INSERT INTO quest_components VALUES(20,2,2),(21,1,4);
                    INSERT INTO quest_acts VALUES(113,20,200,'QuestActConAcceptSphere'),
                                                 (114,21,200,'QuestActConAcceptSphere');""")
        self.assertEqual(1, self.with_starters()["starter_inventory"]["act_count"])

    def test_report_is_deterministic_and_database_is_unchanged(self):
        before = self.compact.read_bytes()
        first = self.run_audit()
        self.assertEqual(first, self.run_audit())
        self.assertEqual(before, self.compact.read_bytes())


if __name__ == "__main__":
    unittest.main()
