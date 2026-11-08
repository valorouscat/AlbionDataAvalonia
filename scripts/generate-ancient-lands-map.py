#!/usr/bin/env python3
"""Export Ancient Lands outline and possible POIs from a pinned ao-bin-dumps checkout.

Needs dragonarea.xml, dragonareatemplatemetadata.xml, cluster/tiles.xml and
 templates/NONE/{DRAGON_001,M_DR_*,S_DR_*}.template.xml. Master rotation must be
restricted. Random island sections use each template's own layer IDs and all
allowed quarter-turns. Output is a union of candidates, never a live spawn list.
"""
import copy
import math
import re
import subprocess
import sys
import xml.etree.ElementTree as ET
from collections import Counter
from pathlib import Path
from template_geometry import IDENTITY, transform, walk

ROOT = Path(__file__).resolve().parent.parent
MASTER = 'DRAGON_001'
CELL = 5
GROUND = re.compile(r'^DRAGON_GROUND_(\d+)x(\d+)$', re.I)


def num(value):
    return f'{round(value, 2) + 0.:g}'


def quoted(text):
    return '"' + text.replace('\\', '\\\\').replace('"', '\\"') + '"'


def active_template(root, ids):
    """Critical placements are real tiles too; their layer lives in their scene-member entry."""
    root = copy.deepcopy(root)
    members = {x.get('id'): x for x in root.findall('./criticalscenemembers/criticalscenemember')}
    for parent in root.iter():
        for child in list(parent):
            if child.tag != 'criticaltile':
                continue
            member = members.get(child.get('id'))
            layer = member.get('layerId') if member is not None else None
            if layer and layer not in ids:
                parent.remove(child)
            else:
                child.tag = 'tile'
    return root


def classify(name):
    upper = name.upper()
    if upper.startswith('SPAWNPOINT_LOOTCHEST_DRAGON_AREA_'):
        label = ('Fire Drake chest' if 'FIRE_DRAKE' in upper else 'Drake chest' if 'NEUTRAL_DRAKE' in upper
                 else 'Central island chest' if 'CENTER_ISLAND' in upper else 'Island chest')
        return 'Chest', 'Possible ' + label[0].lower() + label[1:], 3 if 'DRAKE' in upper else 2
    if name == 'DRAGON_AREA_MAIN_QUEST_BOSS_SPAWNPOINT':
        return 'Boss', 'Fire Drake spawn', 3
    if upper.startswith('SPAWNPOINT_') and upper.endswith(('_BOSS', '_MINIBOSS')):
        return 'Boss', 'Drake spawn' if 'DRAKE' in upper else 'Possible boss spawn' if upper.endswith('_BOSS') else 'Possible miniboss spawn', 3 if upper.endswith('_BOSS') else 2
    if name == 'DRAGON_REWARD_SHRINE':
        return 'Altar', 'Dragon altar', 2
    if name.startswith('DRAGON_KPR_FANATICS_TELEPORTER_A'):
        return 'Teleporter', 'Leygate', 1
    if name.startswith('DRAGON_KPR_FANATICS_EXIT_AREA'):
        return 'ExitUp', 'Ancient Lands exit', 2
    if name == 'LORE_SPOT':
        return 'Lore', 'Lore spot', 1
    return None


def merge_cells(cells):
    runs = {}
    for y in sorted({y for _, y in cells}):
        row = sorted(x for x, yy in cells if yy == y)
        start = previous = row[0]
        for x in row[1:] + [row[-1] + 2]:
            if x != previous + 1:
                runs.setdefault((start, previous + 1), []).append(y)
                start = x
            previous = x
    result = []
    for (left, right), rows in sorted(runs.items()):
        start = previous = rows[0]
        for y in rows[1:] + [rows[-1] + 2]:
            if y != previous + 1:
                result.append((left * CELL, start * CELL, right * CELL, (previous + 1) * CELL))
                start = y
            previous = y
    return result


def ground_cells(tiles):
    cells = set()
    for name, matrix in tiles:
        match = GROUND.match(name)
        if not match:
            continue
        width, depth = map(float, match.groups())
        a, b, c, d, tx, ty = matrix
        determinant = a*d-b*c
        if abs(determinant) < 1e-9:
            continue
        corners = [(a*x+b*z+tx, c*x+d*z+ty) for x in [-width/2, width/2] for z in [-depth/2, depth/2]]
        for gx in range(math.floor(min(x for x,z in corners)/CELL), math.ceil(max(x for x,z in corners)/CELL)):
            for gy in range(math.floor(min(z for x,z in corners)/CELL), math.ceil(max(z for x,z in corners)/CELL)):
                x, z = (gx+.5)*CELL-tx, (gy+.5)*CELL-ty
                lx, lz = (d*x-b*z)/determinant, (-c*x+a*z)/determinant
                if abs(lx) <= width/2+1e-5 and abs(lz) <= depth/2+1e-5:
                    cells.add((gx, gy))
    if not cells:
        raise ValueError('No master ground planes found')
    return cells


def render_underlay(outline, low, high):
    """Cartographic schematic, not game artwork or a navigability/elevation map.

    Pixels use the declared world bounds: positive X/Z run right/down. The land
    mask is exactly the exported five-metre cells; decoration never adds ground.
    """
    from PIL import Image, ImageChops, ImageDraw, ImageFilter
    width, height = round(high[0]-low[0]), round(high[1]-low[1])
    mask = Image.new('L', (width, height))
    draw = ImageDraw.Draw(mask)
    for a, b, c, d in outline:
        draw.rectangle((round(a-low[0]), round(b-low[1]), round(c-low[0])-1, round(d-low[1])-1), fill=255)
    image = Image.new('RGB', mask.size, '#142331')
    # A diffuse rim and a small drop shadow separate known ground from the void.
    halo = mask.filter(ImageFilter.GaussianBlur(9))
    image.paste('#29414a', mask=halo)
    shadow = Image.new('L', mask.size)
    shadow.paste(mask, (0, 5))
    image.paste('#0b151f', mask=shadow.filter(ImageFilter.GaussianBlur(3)))
    image.paste('#53685f', mask=mask)
    edge = ImageChops.subtract(mask, mask.filter(ImageFilter.MinFilter(3)))
    image.paste('#9eafa0', mask=edge)
    # Regular coordinate grid only: no invented contours, section shapes or paths.
    grid = Image.new('RGB', mask.size, '#30414a')
    grid_draw = ImageDraw.Draw(grid)
    for x in range(math.ceil(low[0]/50)*50, math.ceil(high[0]/50)*50, 50):
        grid_draw.line((x-low[0], 0, x-low[0], height), fill='#7b8c83')
    for z in range(math.ceil(low[1]/50)*50, math.ceil(high[1]/50)*50, 50):
        grid_draw.line((0, z-low[1], width, z-low[1]), fill='#7b8c83')
    # Overlay only the grid lines, retaining the terrain colors between them.
    grid_mask = Image.new('L', mask.size)
    grid_draw = ImageDraw.Draw(grid_mask)
    for x in range(math.ceil(low[0]/50)*50, math.ceil(high[0]/50)*50, 50):
        grid_draw.line((x-low[0], 0, x-low[0], height), fill=36)
    for z in range(math.ceil(low[1]/50)*50, math.ceil(high[1]/50)*50, 50):
        grid_draw.line((0, z-low[1], width, z-low[1]), fill=36)
    image.paste(grid, mask=grid_mask)
    target = ROOT/'AlbionDataAvalonia/Assets/Maps/ancient-lands-schematic.webp'
    image.save(target, lossless=True, method=6, exact=True)


def main(source):
    source = Path(source)
    config = ET.parse(source/'dragonarea.xml').getroot()
    metadata = ET.parse(source/'dragonareatemplatemetadata.xml').getroot()
    meta = {x.get('name'): x for x in metadata}
    master_meta = meta[MASTER]
    maps = list(config.findall('.//map'))
    if not maps:
        raise ValueError('No Ancient Lands map definitions found')
    if any(x.get('mastertemplate') != MASTER or x.get('restrictrotation') != 'true' for x in maps):
        raise ValueError('Unknown master or unrestricted rotation: add recognition before exporting this revision')
    catalog = ET.parse(source/'cluster/tiles.xml').getroot()
    compounds = {x.get('name'): x for x in catalog.findall('compoundtile')}
    templates = {}
    def tiles(name, tag):
        key = name, tag
        if key not in templates:
            root = ET.parse(source/f'templates/NONE/{name}.template.xml').getroot()
            ids = {x.get('id') for x in meta[name].findall('layer') if tag in x.get('tags', '').split()}
            templates[key] = list(walk(active_template(root, ids), IDENTITY, compounds, ids))
        return templates[key]
    pools = {x.get('id'): x for x in config.findall('./templates/templatepool')}
    tags = sorted({x.get('tags') for x in master_meta.findall('layer')})
    all_points = {}
    for tag in tags:
        found = set()
        def add(name, matrix, section=False):
            kind = classify(name)
            if kind:
                found.add((*kind, round(matrix[4],2)+0., round(matrix[5],2)+0., section))
        for name, matrix in tiles(MASTER, tag):
            add(name, matrix)
        pool = next(p for p in pools.values() if any(x.get('name') == tag for x in p.findall('.//layertag')))
        for slot in master_meta.findall('slot'):
            sx, _, sz = map(float, slot.get('pos').split())
            matching = [t for t in pool.findall(f"./size[@name='{slot.get('size').lower()}']/type")
                        if set(t.get('requiredslottags', '').split()) <= set(slot.get('slottags','').split())]
            priority = [t for t in matching if t.get('selectionmode') == 'prioritypick']
            if priority:
                matching = priority
            for category in matching:
                for section in category.findall('template'):
                    for turns in ([0] if section.get('restrictrotation') == 'true' else range(4)):
                        placement = ET.Element('placement', pos=f'{sx} 0 {sz}', roty=str(turns*90))
                        parent = transform(IDENTITY, placement)
                        for name, local in tiles(section.get('id'), tag):
                            a,b,c,d,tx,ty = parent
                            la,lb,lc,ld,lx,ly = local
                            add(name, (a*la+b*lc,a*lb+b*ld,c*la+d*lc,c*lb+d*ld,a*lx+b*ly+tx,c*lx+d*ly+ty), True)
        all_points[tag] = found
    all_points['all'] = set.union(*all_points.values())
    # Ground planes of the fixed master only: random sections are not claimed as confirmed terrain.
    outline = merge_cells(ground_cells(tiles(MASTER, tags[0])))
    revision = subprocess.check_output(['git','-C',str(source),'rev-parse','HEAD'],text=True).strip()
    low = list(map(float,master_meta.get('minimapBoundsMin').split()))
    high = list(map(float,master_meta.get('minimapBoundsMax').split()))
    lines = ['// <auto-generated>', '// Written by scripts/generate-ancient-lands-map.py; regenerate rather than editing.',
             '// Possible template positions, not confirmed live sightings.', '// </auto-generated>',
             'using System;', 'using System.Collections.Generic;', '', 'namespace AlbionDataAvalonia.Maps;', '',
             'internal static partial class AncientLandsMapData', '{', f'    public const string SourceRevision = "{revision}";',
             f'    private static readonly MapPoint BoundsMin = new({num(low[0])}, {num(low[1])});',
             f'    private static readonly MapPoint BoundsMax = new({num(high[0])}, {num(high[1])});',
             '    private static readonly MapRect[] Ground =', '    [']
    lines += [f'        new(new({num(a)}, {num(b)}), new({num(c)}, {num(d)})),' for a,b,c,d in outline]
    lines += ['    ];','    private static readonly Dictionary<string, Variant> Variants = new(StringComparer.OrdinalIgnoreCase)', '    {']
    for m in maps:
        tag = next(x.get('name') for x in pools[m.get('templatepool')].findall('.//layertag'))
        pvp = 'orange' if 'ORANGE' in m.get('templatepool') else 'black'
        lines.append(f'        [{quoted(m.get("id"))}] = new({quoted(tag)}, {m.get("clustertier")}, {quoted(pvp)}),')
    lines += ['    };','    private static readonly Dictionary<string, MapPoi[]> Points = new(StringComparer.OrdinalIgnoreCase)','    {']
    canonical = {}
    aliases = {}
    for tag, found in sorted(all_points.items()):
        key = frozenset(found)
        if key in canonical:
            aliases[tag] = canonical[key]
            continue
        canonical[key] = tag
        lines += [f'        [{quoted(tag)}] =','        [']
        for kind,label,grade,x,y,section in sorted(found):
            note = ('A possible position from alternative island sections/rotations; alternatives do not all spawn together'
                    if section else 'A template position; availability depends on the instance and its progress')
            if kind == 'Teleporter':
                note = 'Possible Leygate position; its active state and destination are not confirmed'
            lines.append(f'            new(MapPoiKind.{kind}, {quoted(label)}, {grade}, new({num(x)}, {num(y)}), Notes: [{quoted(note)}, "Not a confirmed live sighting"]),')
        lines += ['        ],']
    lines += ['    };', '    private static readonly Dictionary<string, string> LayerAliases = new(StringComparer.OrdinalIgnoreCase)', '    {']
    lines += [f'        [{quoted(tag)}] = {quoted(aliases.get(tag, tag))},' for tag in sorted(all_points)]
    lines += ['    };','}','']
    target=ROOT/'AlbionDataAvalonia/Maps/AncientLandsMapData.g.cs'
    target.write_text('\n'.join(lines),encoding='utf-8')
    render_underlay(outline, low, high)
    print('Source',revision,'outline',len(outline),'rectangles; candidates by mode', {k:len(v) for k,v in all_points.items()})
    print('Generic POI kinds',dict(Counter(x[0] for x in all_points['all'])))


if __name__ == '__main__':
    main(sys.argv[1])
