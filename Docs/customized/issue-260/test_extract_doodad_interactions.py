"""Run with python -m unittest discover -s Docs/customized/issue-260."""
import importlib.util
import pathlib
import struct
import unittest

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


if __name__ == '__main__':
    unittest.main()
