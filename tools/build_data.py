"""Builds files/statindex-data.json from the game's map scenes and SPT's database.

maps: key id -> map ids its locks are on. Scene folders are assigned to maps by which map's loot containers they hold.
vehicleDefaults: keys set on 3+ vehicle models; their vehicle locks are skipped.
collector: fallback Collector item list (existing entries in the output file are kept).

    pip install UnityPy
    python tools/build_data.py <SPT folder> [out]

Rerun after a game update. Read-only on the install.
"""
import collections
import json
import os
import re
import sys

import UnityPy

NAME = re.compile(rb'([\x05-\x7f])\x00\x00\x00([A-Za-z0-9_\-]{5,127})')
KEY = re.compile(rb'\x18\x00\x00\x00([0-9a-f]{24})')
MIN_CONTAINERS = 10
VEHICLE_MODELS = 3
# Kamaz_4310_on (1) -> kamaz_4310
MODEL_STATE = re.compile(r'(\s*\(\d+\))+$|_(on|off|open|closed)$', re.IGNORECASE)


def database(game):
    for root, dirs, _ in os.walk(os.path.join(game, 'SPT_Runtime', 'SPT_Data')):
        if root.endswith('database') and 'locations' in dirs:
            return root
    sys.exit('No SPT database under SPT_Runtime/SPT_Data')


def is_a(items, tpl, node):
    while tpl and tpl in items:
        if items[tpl]['_name'] == node:
            return True
        tpl = items[tpl].get('_parent')
    return False


def model(name):
    previous = None
    while previous != name:
        previous, name = name, MODEL_STATE.sub('', name)
    return name.lower()


def vehicle(o):
    """The vehicle model a lock is on, or None when it isn't a vehicle's door (the game's Trunk objects)."""
    try:
        behaviour = o.read(check_read=False)
        if behaviour.m_Script.read().m_ClassName != 'Trunk':
            return None
        t = behaviour.m_GameObject.read().m_Transform.read()
        return model(t.m_Father.read().m_GameObject.read().m_Name) if t.m_Father and t.m_Father.path_id else ''
    except Exception:
        return None


def collector(db, items):
    """Items handed over for the quest whose reward is the biggest secure container (Kappa, Collector); if two give
    one as big, the one asking for more."""
    def cells(tpl):
        grids = items.get(tpl, {}).get('_props', {}).get('Grids') or []
        return sum(g['_props']['cellsH'] * g['_props']['cellsV'] for g in grids)

    best, best_cells = set(), 0
    for quest in json.load(open(os.path.join(db, 'templates', 'quests.json'), encoding='utf-8')).values():
        rewards = (quest.get('rewards') or {}).get('Success') or []
        size = max((cells(i.get('_tpl')) for r in rewards for i in r.get('items') or []
                    if is_a(items, i.get('_tpl'), 'MobContainer')), default=0)
        if size == 0:
            continue
        wanted = {t for c in (quest.get('conditions') or {}).get('AvailableForFinish') or []
                  if c.get('conditionType') == 'HandoverItem'
                  for t in (c.get('target') if isinstance(c.get('target'), list) else [c.get('target')]) if t}
        if size > best_cells or size == best_cells and len(wanted) > len(best):
            best, best_cells = wanted, size
    return best


def main(game, out=os.path.join('files', 'statindex-data.json')):
    db = database(game)
    items = json.load(open(os.path.join(db, 'templates', 'items.json'), encoding='utf-8'))
    keys = {i for i, t in items.items() if t['_type'] == 'Item' and is_a(items, t['_parent'], 'Key')}

    # map id -> its loot containers' names
    boxes = collections.defaultdict(set)
    for folder in os.listdir(os.path.join(db, 'locations')):
        base = os.path.join(db, 'locations', folder, 'base.json')
        static = os.path.join(db, 'locations', folder, 'staticContainers.json')
        if os.path.exists(base) and os.path.exists(static):
            map_id = json.load(open(base, encoding='utf-8'))['Id'].lower()
            for c in json.load(open(static, encoding='utf-8'))['staticContainers']:
                boxes[map_id].add(c['template']['Id'])
    want = set().union(*boxes.values())
    print(f'{len(keys)} keys, {len(boxes)} maps, {len(want)} containers', flush=True)

    data = os.path.join(game, 'EscapeFromTarkov_Data')
    gg = UnityPy.load(os.path.join(data, 'globalgamemanagers'))
    scenes = next(o.read_typetree()['scenes'] for o in gg.objects if o.type.name == 'BuildSettings')

    # scene -> (folder, [(key, vehicle model or None)], containers in it)
    found = {}
    for i, scene in enumerate(scenes):
        parts = scene.split('/')
        level = os.path.join(data, f'level{i}')
        if len(parts) < 5 or parts[2] != 'Locations' or not os.path.exists(level):
            continue
        locks, held = [], set()
        for o in UnityPy.load(level).objects:
            if o.type.name != 'MonoBehaviour':
                continue
            raw = o.get_raw_data()
            here = {m.group(1).decode() for m in KEY.finditer(raw)} & keys
            if len(here) == 1:
                locks.append((here.pop(), vehicle(o)))
            names = {m.group(2).decode() for m in NAME.finditer(raw) if m.group(1)[0] == len(m.group(2))} & want
            if len(names) == 1:
                held |= names
        if locks or held:
            found[scene] = (parts[3], locks, held)

    # counted per folder: single small scenes share container names across maps
    held_by_folder = collections.defaultdict(set)
    for folder, _, held in found.values():
        held_by_folder[folder] |= held
    by_folder = {}
    for folder, held in held_by_folder.items():
        counts = {m: len(held & b) for m, b in boxes.items()}
        top = max(counts.values(), default=0)
        if top >= MIN_CONTAINERS:
            by_folder[folder] = {m for m, n in counts.items() if n == top}
            print(f'  {folder}: {sorted(by_folder[folder])} ({top} containers)', flush=True)

    models = collections.defaultdict(set)
    for _, locks, _ in found.values():
        for k, v in locks:
            if v is not None:
                models[k].add(v)
    defaults = {k for k, v in models.items() if len(v) >= VEHICLE_MODELS}
    for k in sorted(defaults):
        print(f'  vehicle default, its vehicle locks left out: {k} on {sorted(models[k])}', flush=True)

    result = collections.defaultdict(set)
    lost = set()
    for scene, (folder, locks, _) in found.items():
        ms = by_folder.get(folder)
        if not ms:
            if locks:
                print(f'  no map for {scene} ({folder}): {len(locks)} locks', flush=True)
                lost |= {k for k, _ in locks}
            continue
        for k, v in locks:
            if v is None or k not in defaults:
                result[k] |= ms

    wanted = collector(db, items)
    # keep existing entries (mod-added Collector items were merged in by hand)
    if os.path.exists(out):
        wanted |= set(json.load(open(out, encoding='utf-8')).get('collector') or [])
    with open(out, 'w', encoding='utf-8') as f:
        json.dump({'maps': {k: sorted(v) for k, v in sorted(result.items())},
                   'vehicleDefaults': sorted(defaults), 'collector': sorted(wanted)}, f, separators=(',', ':'))
    print(f'{len(result)} keys placed by their locks, {len(keys - set(result))} with no lock '
          f'({len(lost - set(result))} of them only in scenes with no map), {len(wanted)} Collector items', flush=True)


if __name__ == '__main__':
    if len(sys.argv) < 2 or len(sys.argv) > 3:
        sys.exit(__doc__)
    main(*sys.argv[1:])
