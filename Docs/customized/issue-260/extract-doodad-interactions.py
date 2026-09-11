#!/usr/bin/env python3
"""Extract r208022 quest-source sphere facts; never copy mesh data into the server.

See native-contract.md for the exact-client consumers. CGF layout references:
https://github.com/aws/lumberyard/blob/master/dev/Code/CryEngine/CryCommon/CryHeaders.h
https://github.com/aws/lumberyard/blob/master/dev/Code/CryEngine/Cry3DEngine/StatObjLoad.cpp
"""
import argparse
import collections
import hashlib
import importlib.util
import json
import math
import pathlib
import sqlite3
import struct
import xml.etree.ElementTree as ET


def normalized_model(model):
    scheme, path = model.replace('\\', '/').lower().rstrip('/').split('://', 1)
    if scheme == 'cgf' and not path.startswith('game/'):
        path = 'game/' + path
    return scheme + '://' + path


def world_translation(node, nodes):
    # CGF Node.tm is local. The root statObj helper uses the composed worldTM.
    center = [node['matrix'][i] for i in (12, 13, 14)]
    parent = node['parent']
    visited = set()
    while parent != -1:
        if parent in visited:
            raise ValueError('Cyclic helper parent chain')
        visited.add(parent)
        matrix = nodes[parent]['matrix']
        center = [sum(matrix[i + axis * 4] * center[axis] for axis in range(3)) + matrix[12 + i]
                  for i in range(3)]
        parent = nodes[parent]['parent']
    return [v * 0.01 for v in center]


def cgf_spheres(data):
    if data[:6] != b'CryTek':
        raise ValueError('Unsupported CGF signature')
    version, table = struct.unpack_from('<2I', data, 12)
    if version not in (0x744, 0x745) or table > len(data) - 4:
        raise ValueError('Unsupported CGF header')
    count = struct.unpack_from('<I', data, table)[0]
    stride = 16 if version == 0x744 else 20
    if count > (len(data) - table - 4) // stride:
        raise ValueError('Invalid CGF chunk count')
    chunks = {}
    nodes = {}
    for i in range(count):
        kind, version, offset, chunk_id = struct.unpack_from('<4I', data, table + 4 + i * stride)
        if offset > len(data) - 16:
            raise ValueError('Invalid CGF chunk position')
        chunks[chunk_id] = kind, version, offset + 16
        if kind == 0xCCCC000B:
            if version not in (0x823, 0x824):
                raise ValueError('Unsupported CGF node version')
            body = offset + 16
            nodes[chunk_id] = {
                'name': data[body:body + 64].split(b'\0')[0].decode(),
                'object': struct.unpack_from('<i', data, body + 64)[0],
                'parent': struct.unpack_from('<i', data, body + 68)[0],
                'matrix': struct.unpack_from('<16f', data, body + 84),
            }
    spheres = []
    for node in nodes.values():
        name = node['name'].lower()
        if name.startswith('$aimbox'):
            raise ValueError('Box helper needs a reviewed box implementation')
        if not name.startswith('$aimpoint'):
            continue
        kind, version, body = chunks[node['object']]
        if kind != 0xCCCC0001 or version != 0x744:
            raise ValueError('Aim node is not a supported helper')
        helper_type, sx, sy, sz = struct.unpack_from('<I3f', data, body)
        if helper_type != 1:
            raise ValueError('Aim helper is not HP_DUMMY')
        # Runtime HP_DUMMY size converts centimeters to meters. LoadProxy uses X.
        spheres.append({'center': world_translation(node, nodes), 'radius': sx * 0.005})
    return spheres


def transform_sphere(sphere, obj):
    position = [float(v) for v in obj.get('Pos', '0,0,0').split(',')]
    scale = [float(v) for v in obj.get('Scale', '1,1,1').split(',')]
    # CryEngine XML stores quaternion W,X,Y,Z. Current helpers use uniform scale.
    w, x, y, z = [float(v) for v in obj.get('Rotate', '1,0,0,0').split(',')]
    if max(scale) - min(scale) > 0.000001 or min(scale) <= 0:
        raise ValueError('Nonuniform helper scale needs native review')
    if abs(w*w+x*x+y*y+z*z-1) > 0.00001:
        raise ValueError('Invalid prefab quaternion')
    vx, vy, vz = [v*s for v, s in zip(sphere['center'], scale)]
    tx, ty, tz = 2*(y*vz-z*vy), 2*(z*vx-x*vz), 2*(x*vy-y*vx)
    center = [vx+w*tx+y*tz-z*ty, vy+w*ty+z*tx-x*tz, vz+w*tz+x*ty-y*tx]
    return {'center': [c+p for c, p in zip(center, position)], 'radius': sphere['radius'] * scale[0]}


def prefab_spheres(prefab, read_asset):
    spheres = []
    dependencies = []
    for obj in prefab.findall('./Objects/Object'):
        properties = obj.find('Properties')
        asset = obj.get('Prefab') if obj.get('Type') == 'Brush' else None
        if obj.get('Type') == 'Entity' and properties is not None:
            asset = properties.get('object_Model')
        if asset:
            child_path = normalized_model('cgf://' + asset).split('://', 1)[1]
            child_data, child_digest = read_asset(child_path)
            dependencies.append({'path': child_path, 'sha256': child_digest})
            spheres.extend(transform_sphere(sphere, obj) for sphere in cgf_spheres(child_data))
            continue
        if obj.get('Type') != 'Comment':
            continue
        name = obj.get('Name', '').lower()
        if name.startswith('aimbox'):
            raise ValueError('Prefab box needs a reviewed box implementation')
        if name.startswith('aimpoint'):
            spheres.append({'center': [float(v) for v in obj.get('Pos', '0,0,0').split(',')],
                            'radius': float(obj.get('Comment'))})
    return spheres or [{'center': [0, 0, 0], 'radius': 0.5}], dependencies


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--pak', type=pathlib.Path, required=True)
    parser.add_argument('--compact', type=pathlib.Path, required=True)
    parser.add_argument('--pak-reader', type=pathlib.Path, required=True,
                        help='The workspace aaemu-client-pak skill aapak.py')
    parser.add_argument('--output', type=pathlib.Path, required=True)
    args = parser.parse_args()
    spec = importlib.util.spec_from_file_location('aapak', args.pak_reader)
    reader = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(reader)
    index = {name.lower().replace('\\', '/'): (offset, size)
             for name, offset, size in reader.read_index(args.pak)}
    source_ids = collections.defaultdict(set)
    connection = sqlite3.connect(f'file:{args.compact.resolve()}?mode=ro', uri=True)
    quest_ids = 'SELECT doodad_id FROM quest_act_con_accept_doodads UNION SELECT doodad_id FROM quest_act_con_report_doodads'
    queries = [f'SELECT id,model FROM doodad_almighties WHERE id IN ({quest_ids})',
               f'SELECT doodad_almighty_id,model FROM doodad_func_groups WHERE doodad_almighty_id IN ({quest_ids})']
    missing = []
    for query in queries:
        for doodad_id, model in connection.execute(query):
            if model:
                source_ids[normalized_model(model)].add(doodad_id)
    facts = []
    with args.pak.open('rb') as pak:
        def read_asset(path):
            if path not in index:
                raise ValueError(f'Missing asset: {path}')
            offset, size = index[path]
            pak.seek(offset)
            data = pak.read(size)
            return data, hashlib.sha256(data).hexdigest()

        for model, ids in sorted(source_ids.items()):
            try:
                scheme, path = model.split('://', 1)
                if scheme == 'cgf':
                    data, digest = read_asset(path)
                    spheres = cgf_spheres(data)
                elif scheme == 'prefab':
                    path, name = path.split('.xml/', 1)
                    path += '.xml'
                    if path not in index:
                        path = 'game/' + path
                    data, digest = read_asset(path)
                    root = ET.fromstring(data)
                    matches = [prefab for prefab in root.iter('Prefab')
                               if prefab.get('Name', '').lower() == name]
                    if len(matches) != 1:
                        raise ValueError('Prefab name is missing or ambiguous')
                    spheres, dependencies = prefab_spheres(matches[0], read_asset)
                else:
                    raise ValueError('Unsupported model scheme')
                if not spheres:
                    spheres = [{'center': [0, 0, 0], 'radius': 0.5}]
                if any(len(s['center']) != 3 or s['radius'] < 0 or
                       not all(math.isfinite(v) for v in s['center']+[s['radius']]) for s in spheres):
                    raise ValueError('Invalid sphere')
                fact = {'model': model, 'templateIds': sorted(ids), 'sourceSha256': digest, 'spheres': spheres}
                if scheme == 'prefab':
                    fact['dependencies'] = sorted(dependencies, key=lambda item: item['path'])
                facts.append(fact)
            except (ValueError, KeyError, struct.error) as error:
                missing.append({'model': model, 'templateIds': sorted(ids), 'reason': str(error)})
    result = {
        'version': 'r208022',
        'clientSha256': 'a1995455440cdeb6356682e801f322cd946f3de3ad6595fae818d7a139100ae0',
        'compactSha256': hashlib.sha256(args.compact.read_bytes()).hexdigest(),
        'models': facts,
        'unsupportedModels': missing,
    }
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(result, indent=2) + '\n')
    print(f'{len(facts)} model entries, {len(missing)} unsupported models')
    for item in missing:
        print(item)


if __name__ == '__main__':
    main()
