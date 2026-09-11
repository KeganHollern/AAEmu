"""Run with python -m unittest discover -s Docs/customized/issue-260."""
import importlib.util
import pathlib
import struct
import unittest
import xml.etree.ElementTree as ET

spec = importlib.util.spec_from_file_location('extract', pathlib.Path(__file__).with_name('extract-doodad-interactions.py'))
extract = importlib.util.module_from_spec(spec)
spec.loader.exec_module(extract)


def model(name='$aimpoint', helper_type=1):
    data = bytearray(320)
    data[:8] = b'CryTek\0\0'
    struct.pack_into('<4I', data, 8, 0xFFFF0000, 0x744, 20, 2)
    struct.pack_into('<4I', data, 24, 0xCCCC0001, 0x744, 56, 1)
    struct.pack_into('<4I', data, 40, 0xCCCC000B, 0x823, 88, 2)
    struct.pack_into('<I3f', data, 72, helper_type, 200, 200, 200)
    encoded = name.encode()
    data[104:104+len(encoded)] = encoded
    struct.pack_into('<4i', data, 168, 1, -1, 0, -1)
    struct.pack_into('<16f', data, 188, 1,0,0,0, 0,1,0,0, 0,0,1,0, 100,200,300,0)
    return data


class ExtractTests(unittest.TestCase):
    def test_cgf_size_and_translation_use_meters(self):
        self.assertEqual(extract.cgf_spheres(model()), [{'center': [1, 2, 3], 'radius': 1}])

    def test_non_aim_node_does_not_create_geometry(self):
        self.assertEqual(extract.cgf_spheres(model('ordinary')), [])

    def test_box_needs_native_review(self):
        with self.assertRaisesRegex(ValueError, 'Box helper'):
            extract.cgf_spheres(model('$aimbox'))

    def test_non_dummy_helper_is_unsupported(self):
        with self.assertRaisesRegex(ValueError, 'HP_DUMMY'):
            extract.cgf_spheres(model(helper_type=0))

    def test_chunk_count_cannot_exceed_file(self):
        data = model()
        struct.pack_into('<I', data, 20, 0xFFFFFFFF)
        with self.assertRaisesRegex(ValueError, 'chunk count'):
            extract.cgf_spheres(data)

    def test_model_normalization_matches_archive_paths(self):
        self.assertEqual(extract.normalized_model('cgf://Objects\\Model.CGF/'), 'cgf://game/objects/model.cgf')
        self.assertEqual(extract.normalized_model('prefab://Prefabs/a.xml/Entry'), 'prefab://prefabs/a.xml/entry')

    def test_brush_scale_and_translation(self):
        sphere = extract.transform_sphere({'center': [1, 0, 0], 'radius': 2},
                                         {'Pos': '1,2,3', 'Scale': '2,2,2'})
        self.assertEqual(sphere, {'center': [3, 2, 3], 'radius': 4})

    def test_brush_quaternion_is_wxyz(self):
        sphere = extract.transform_sphere({'center': [1, 0, 0], 'radius': 1},
                                         {'Rotate': '0,0,0,1'})
        self.assertEqual(sphere, {'center': [-1, 0, 0], 'radius': 1})

    def test_parent_rotation_precedes_grandparent_translation(self):
        child = {'parent': 1, 'matrix': [1,0,0,0, 0,1,0,0, 0,0,1,0, 100,0,0,0]}
        nodes = {
            1: {'parent': 2, 'matrix': [0,1,0,0, -1,0,0,0, 0,0,1,0, 100,0,0,0]},
            2: {'parent': -1, 'matrix': [1,0,0,0, 0,1,0,0, 0,0,1,0, 0,200,0,0]},
        }
        self.assertEqual(extract.world_translation(child, nodes), [1, 3, 0])

    def test_parent_cycle_rejects_catalog_input(self):
        child = {'parent': 1, 'matrix': [1,0,0,0, 0,1,0,0, 0,0,1,0, 0,0,0,0]}
        with self.assertRaisesRegex(ValueError, 'Cyclic'):
            extract.world_translation(child, {1: child})

    def test_prefab_combines_comments_brushes_and_entities_without_fallback(self):
        prefab = ET.fromstring('''<Prefab><Objects>
          <Object Type="Comment" Name="aimPoint_1" Pos="0,0,0" Comment="2" />
          <Object Type="Brush" Prefab="objects/a.cgf" Pos="1,2,3" Scale="2,2,2" />
          <Object Type="Entity"><Properties object_Model="objects/b.cga" /></Object>
        </Objects></Prefab>''')
        shapes, dependencies = extract.prefab_spheres(prefab, lambda path: (model(), path))
        self.assertEqual(shapes, [
            {'center': [0, 0, 0], 'radius': 2},
            {'center': [3, 6, 9], 'radius': 2},
            {'center': [1, 2, 3], 'radius': 1},
        ])
        self.assertEqual([item['path'] for item in dependencies], ['game/objects/a.cgf', 'game/objects/b.cga'])

    def test_prefab_fallback_follows_the_complete_empty_group(self):
        prefab = ET.fromstring('''<Prefab><Objects>
          <Object Type="Comment" Name="name_tag" Pos="0,0,9" Comment="" />
          <Object Type="Brush" Prefab="objects/a.cgf" Pos="5,6,7" Scale="2,2,2" />
          <Object Type="Entity"><Properties object_Model="objects/b.chr" /></Object>
        </Objects></Prefab>''')
        shapes, dependencies = extract.prefab_spheres(prefab, lambda path: (model('ordinary'), path))
        self.assertEqual(shapes, [{'center': [0, 0, 0], 'radius': 0.5}])
        self.assertEqual(len(dependencies), 2)


if __name__ == '__main__':
    unittest.main()
