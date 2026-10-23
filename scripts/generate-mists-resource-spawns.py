#!/usr/bin/env python3
"""Export Mists candidates, instance tier sets and possible resource patch areas.

Input: ao-data/ao-bin-dumps at one revision, including cluster/tiles.xml, mists.xml,
resourcedistpresets.xml, miststemplatemetadata.xml, all GREEN/RED/DEAD Mists master
 templates, and GREEN MISTS_[MS]_RES_* templates. See docs/DEVELOPMENT.md.
No generated candidate is an observed/live node. Never expand random slave templates
into exact master points: those placements are not known from the master layout.
"""
import gzip
import json
import math
import re
import sys
import xml.etree.ElementTree as ET
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
FAMILIES = ['WOOD', 'ROCK', 'FIBER', 'HIDE', 'ORE']
BUCKETS = ['LOW', 'MEDIUM', 'HIGH']
NODE = re.compile(r'^(WOOD|ROCK|FIBER|HIDE|ORE)_(LOW|MEDIUM|HIGH)_NODE$')
IDENTITY = (1., 0., 0., 1., 0., 0.)


def point(text):
    x, _, z = map(float, text.split())
    return x, z


def transform(parent, placement):
    """Unity Y rotation on x/z; compose scale, then rotation, then translation."""
    a, b, c, d, tx, ty = parent
    px, pz = point(placement.get('pos', '0 0 0'))
    sx, sz = point(placement.get('scale', '1 1 1'))
    angle = math.radians(float(placement.get('roty', '0')))
    co, si = math.cos(angle), math.sin(angle)
    return (a*co*sx-b*si*sx, a*si*sz+b*co*sz,
            c*co*sx-d*si*sx, c*si*sz+d*co*sz,
            a*px+b*pz+tx, c*px+d*pz+ty)


def walk(container, matrix, compounds, active=None, stack=()):
    for child in container:
        if child.tag == 'layer':
            if active is None or child.get('id') in active:
                yield from walk(child, matrix, compounds, active, stack)
        elif child.tag in ('tiles', 'layergroup'):
            yield from walk(child, matrix, compounds, active, stack)
        elif child.tag == 'tile':
            yield child.get('name', ''), transform(matrix, child)
        elif child.tag == 'compoundtile':
            name = child.get('name')
            if name in stack:
                raise ValueError(f'Recursive compound: {name}')
            definition = compounds.get(name)
            if definition is None:
                raise ValueError(f'Missing compound: {name}')
            yield from walk(definition, transform(matrix, child), compounds,
                            set(child.get('activelayers', '').split()), stack + (name,))


def main(source, output=None):
    source = Path(source)
    metadata = ET.parse(source/'miststemplatemetadata.xml').getroot()
    game = ET.parse(source/'mists.xml').getroot()
    tiles = ET.parse(source/'cluster/tiles.xml').getroot()
    compounds = {x.get('name'): x for x in tiles.findall('compoundtile')}
    tile_defs = {x.get('name'): x for x in tiles.findall('tile')}
    masters = {x.get('name'): x for x in metadata.findall('master')}
    templates = {x.get('name'): x for x in metadata.findall('template')}
    pools = {x.get('id'): x for x in game.iter('templatepool')}
    resource_mobs = dict(re.findall(r'\["([^"]+)"\] = "([^"]+)"', (ROOT/'AlbionDataAvalonia/Activity/ActivityResourceMobCatalog.g.cs').read_text()))
    presets = [x for x in ET.parse(source/'resourcedistpresets.xml').getroot().findall('preset')
               if x.get('biome') == 'Mists']
    world = json.loads((ROOT/'shared/AFMDataClientCore/src/AFMDataClient.Core/Locations/activity-world.json').read_text())
    known = set(world['mistsLandmarks'])
    if known - masters.keys():
        raise ValueError(f'Missing known layouts: {known-masters.keys()}')
    sizes = {}
    resource_templates = {}
    for name, meta in templates.items():
        if '_RES_' not in name:
            continue
        root = ET.parse(source/f'templates/GREEN/{name}.template.xml').getroot()
        limits = [abs(float(v)) for attr in ('editorBoundsMin', 'editorBoundsMax') for v in root.get(attr).split()]
        sizes[meta.get('size').lower()] = max(sizes.get(meta.get('size').lower(), 0), *limits)
        resource_templates[name] = root
    result = {'revision': (source/'source-revision.txt').read_text().strip(), 'layouts': {}, 'contexts': {}}
    for name in sorted(known):
        master = masters[name]
        slots = [[*point(s.get('pos')), sizes[s.get('size').lower()]] for s in master.findall('slot')]
        variants = {}
        for biome in ('green', 'red', 'dead'):
            root = ET.parse(source/f'templates/{biome.upper()}/{name}.template.xml').getroot()
            candidates = set()
            for tile, matrix in walk(root, IDENTITY, compounds):
                match = NODE.match(tile)
                if not match:
                    continue
                x, z = round(matrix[4], 3), round(matrix[5], 3)
                # Slave sections replace these squares; don't advertise the placeholder ground underneath.
                if any(abs(x-sx) <= half and abs(z-sz) <= half for sx, sz, half in slots):
                    continue
                family, bucket = match.groups()
                candidates.add((FAMILIES.index(family), BUCKETS.index(bucket), x, z))
            if not candidates:
                raise ValueError(f'No candidates for {name}/{biome}')
            variants[biome] = sorted(candidates)
        bounds = [*map(float, master.get('minimapBoundsMin').split()), *map(float, master.get('minimapBoundsMax').split())]
        result['layouts'][name] = {'bounds': bounds, 'slots': slots, 'variants': variants}
    for mode in game.iter('mistsmode'):
        kind = mode.get('type')
        if kind not in ('mists', 'resourcemists'):
            continue
        mapset = next(x for x in game.iter('mapset') if x.get('name') == mode.get('mapset'))
        for entry in mapset.findall('map'):
            layout = entry.get('mastertemplate')
            if layout not in known:
                raise ValueError(f'Unknown master: {layout}')
            tier = int(entry.get('clustertier'))
            pvp = 'PvpAllowed' if mode.get('resourcepool') == 'YELLOW' else 'PvpForced'
            wanted = 'ResourceMists' if kind == 'resourcemists' else 'Mists'
            difficulty = {f'D{d}' for d in range(int(entry.get('mindifficulty', 0)), int(entry.get('maxdifficulty', 4))+1)}
            selected = [p for p in presets if p.get('type') == wanted and int(p.get('tier')) == tier and p.get('pvprules') == pvp
                        and (not p.get('difficulty') or difficulty.intersection(p.get('difficulty').split(',')))]
            if not selected:
                raise ValueError(f'No resource preset for {entry.attrib}')
            masks = [[0]*3 for _ in FAMILIES]
            for preset in selected:
                for bi, bucket in enumerate(BUCKETS):
                    for resource in preset.findall(bucket.lower()+'/*'):
                        if resource.tag.upper() in FAMILIES and float(resource.get('amount','0')) > 0:
                            masks[FAMILIES.index(resource.tag.upper())][bi] |= 1 << int(resource.get('tier'))
            areas = {}
            for size in pools[entry.get('templatepool')].findall('size'):
                mask = [0]*len(FAMILIES)
                for category in size.findall('type'):
                    if category.get('category') != 'resource' or float(category.get('weight','0')) <= 0:
                        continue
                    tier_tags = {x.get('name') for x in category.findall('rolllayertag/layertag') if float(x.get('weight','0')) > 0}
                    for reference in category.findall('template'):
                        if float(reference.get('weight','0')) <= 0:
                            continue
                        name = reference.get('id')
                        selected_layers = {x.get('id') for x in templates[name].findall('layer') if tier_tags.intersection(re.split(r'[|\s]+', x.get('tags','')))}
                        family_tags = {f.upper() for f in reference.get('layertags','').split()}
                        # Only the chosen resource layers, not unlayered landscape/decorative harvestables.
                        for layer in resource_templates[name].iter('layer'):
                            if layer.get('id') not in selected_layers:
                                continue
                            for tile, _ in walk(layer, IDENTITY, compounds):
                                definition = tile_defs.get(tile)
                                harvest = definition.find('harvestable') if definition is not None else None
                                family, resource_tier = '', 0
                                if harvest is not None:
                                    family = harvest.get('type','').split('_')[0]
                                    resource_tier = int(harvest.get('tier'))
                                elif tile.startswith('SpawnPoint_') and tile[11:] in resource_mobs:
                                    family = resource_mobs[tile[11:]].upper()
                                    family = 'ROCK' if family == 'STONE' else family
                                    resource_tier = int(tile[12])
                                if family in FAMILIES and (not family_tags or family in family_tags):
                                    mask[FAMILIES.index(family)] |= 1 << resource_tier
                areas[size.get('name')] = mask
            context = {'layout': layout, 'biome': entry.get('subbiome'), 'masks': masks, 'areas': areas}
            key = entry.get('id')
            if key in result['contexts']:
                previous = result['contexts'][key]
                if previous['layout'] != layout or previous['biome'] != context['biome']:
                    raise ValueError(f'Ambiguous layout/biome for {key}')
                # Resource Mists reuse map ids across resource-family modes. Without that mode,
                # retain the union of allowed families/tiers, never claim which slave was selected.
                for f in range(len(FAMILIES)):
                    for b in range(len(BUCKETS)):
                        context['masks'][f][b] |= previous['masks'][f][b]
                for size, old_mask in previous['areas'].items():
                    context['areas'][size] = [a | b for a, b in zip(context['areas'].get(size, [0]*5), old_mask)]
            result['contexts'][key] = context
    target = Path(output) if output else ROOT/'AlbionDataAvalonia/ReferenceData/mists-resource-spawns.json.gz'
    encoded = json.dumps(result, separators=(',', ':'), sort_keys=True).encode()
    target.write_bytes(gzip.compress(encoded, mtime=0))
    print(f"Wrote {len(result['layouts'])} layouts, {len(result['contexts'])} contexts, {len(encoded)} JSON bytes, {target.stat().st_size} gzip bytes")


if __name__ == '__main__':
    main(sys.argv[1], sys.argv[2] if len(sys.argv) > 2 else None)
