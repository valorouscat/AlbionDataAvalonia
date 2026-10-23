#!/usr/bin/env python3
"""Writes AlbionDataAvalonia/Maps/DepthsMapData.g.cs from the game's Depths (hell dungeon) files.

AlbionWorld has no minimap or map features for the Depths, so the map draws each floor from the game files at
https://github.com/ao-data/ao-bin-dumps:

- Floors. helldungeons.xml lists the Depths maps (DUO_ORANGE_HELL_DUNGEON_01 …), each the floor of a fixed master
  layout (HELLDNG_MASTER_01_LVL1 …). helldungeonstemplatemetadata.xml gives each master's minimap bounds and the slots
  its camp sections fill when the instance opens.
- Outline. The master template places the floor's halls, corridors and walls as tiles. Every 10 m cell holding a tile
  is kept, small gaps closed, and the cells merged into rectangles: a rough outline of where the floor has ground, not
  a minimap. The camp slots are drawn as rooms of their section's size on top of it.
- Points of interest. The master places the loot chest and coffer spots, key spawns, resurrection shrines and the
  exits up, down and out, and the demon altars (the room nodes the in-game map marks) that spawn its monsters. The game
  may turn each floor; positions stay in template coordinates and observed landmarks determine the instance's turn.
  A camp section is picked when the instance opens and fills its slot, so a slot shows as one
  camp spot, with the chance of a boss the floor's template pool gives that slot size.

Rerun after a game patch changes the Depths:

    git clone --depth 1 --filter=blob:none --sparse https://github.com/ao-data/ao-bin-dumps.git /tmp/ao-bin-dumps
    git -C /tmp/ao-bin-dumps sparse-checkout set --no-cone /helldungeons.xml /helldungeonstemplatemetadata.xml \\
        '/templates/DEAD/HELLDNG_*'
    python3 scripts/generate-depths-map-data.py /tmp/ao-bin-dumps
"""
import math
import re
import sys
import xml.etree.ElementTree as ElementTree
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
TILE = re.compile(r'<(?:tile|compoundtile|criticaltile) name="([^"]+)" pos="([^"]+)"')
CELL = 10
# The demon altars a floor's monsters spawn at, by how strong the room is.
ALTARS = {"DNG_HELL_ROOM_LOW_NODE": 1, "DNG_HELL_ROOM_MEDIUM_NODE": 2, "DNG_HELL_ROOM_HIGH_NODE": 3}
# Tiles that are lights, sound, atmosphere, water, roofs or the backdrop around the floor, or markers, don't outline it.
NOT_FLOOR = ("LIGHT", "ATMOSPHERE", "WATER", "RIVER", "ROOF", "BACKDROP", "SPAWN", "MARKER", "NODE", "FX")


def ground(pos: str) -> tuple[float, float]:
    """A template position is "x height z"; the map uses x and z. Instance rotation is determined at runtime."""
    x, _, z = (float(value) for value in pos.split())
    return round(x, 1) + 0.0, round(z, 1) + 0.0


def pair(value: str) -> tuple[float, float]:
    x, z = (float(part) for part in value.split())
    return round(x, 1), round(z, 1)


def unique(values):
    return list(dict.fromkeys(values))


def number(value: float) -> str:
    return f"{value:g}"


def points(values) -> str:
    return ", ".join(f"({number(x)}, {number(z)})" for x, z in values)


def template(dumps: Path, name: str) -> str:
    # Every Depths map is in the dead subbiome.
    path = dumps / "templates" / "DEAD" / f"{name}.template.xml"
    if not path.exists():
        raise SystemExit(f"No template {path}")
    return path.read_text(encoding="utf-8")


def bounds(text: str, key: str) -> tuple[float, float, float, float]:
    match = re.search(rf'{key}BoundsMin="([^"]+)" {key}BoundsMax="([^"]+)"', text)
    return pair(match.group(1)) + pair(match.group(2))


def outline(text: str, low, high, slots, section_half) -> list[tuple[float, float, float, float]]:
    cells = set()
    for name, pos in TILE.findall(text):
        if any(word in name.upper() for word in NOT_FLOOR):
            continue
        x, z = ground(pos)
        if low[0] <= x <= high[0] and low[1] <= z <= high[1]:
            cells.add((math.floor(x / CELL), math.floor(z / CELL)))
    # A hall's floor is often one large tile, so a cell with tiles on most sides is floor too.
    for _ in range(2):
        xs, zs = [c[0] for c in cells], [c[1] for c in cells]
        cells |= {(i, j) for i in range(min(xs) - 1, max(xs) + 2) for j in range(min(zs) - 1, max(zs) + 2)
                  if (i, j) not in cells and sum((i + a, j + b) in cells for a in (-1, 0, 1) for b in (-1, 0, 1)) >= 4}
    # The camp slots draw as rooms of their own.
    def in_slot(i, j):
        cx, cz = (i + 0.5) * CELL, (j + 0.5) * CELL
        return any(abs(cx - x) < section_half[medium] and abs(cz - z) < section_half[medium] for x, z, medium in slots)
    cells = {c for c in cells if not in_slot(*c)}
    # Runs along x, then runs of the same span stacked along z.
    runs = {}
    for j in sorted({c[1] for c in cells}):
        row = sorted(c[0] for c in cells if c[1] == j)
        start = previous = row[0]
        for i in row[1:] + [None]:
            if i is not None and i == previous + 1:
                previous = i
                continue
            runs.setdefault((start, previous), []).append(j)
            if i is not None:
                start = previous = i
    rects = []
    for (first, last), rows in runs.items():
        top = bottom = rows[0]
        for j in rows[1:] + [None]:
            if j is not None and j == bottom + 1:
                bottom = j
                continue
            rects.append((first * CELL, top * CELL, (last + 1) * CELL, (bottom + 1) * CELL))
            if j is not None:
                top = bottom = j
    return sorted(rects, key=lambda r: (r[1], r[0]))


def boss_note(weights: dict[str, float]) -> str:
    """The chance of a boss or miniboss a slot size rolls, as the pool's layer tags weigh them."""
    total = sum(weights.values())
    def share(kind):
        return round(100 * sum(w for tag, w in weights.items() if tag.startswith(kind)) / total) if total else 0
    if total == 0:
        return ""
    if any(tag.startswith("keychest") for tag, w in weights.items() if w == total):
        return "Holds the floor's key chest"
    boss, miniboss = share("boss"), share("miniboss")
    if boss == 0 and miniboss == 0:
        return "No boss on this floor"
    return f"Boss {boss}%, miniboss {miniboss}%"


def main(source: str) -> None:
    dumps = Path(source)
    metadata = ElementTree.parse(dumps / "helldungeonstemplatemetadata.xml").getroot()
    modes = ElementTree.parse(dumps / "helldungeons.xml").getroot()

    pools = {}
    for pool in modes.iter("templatepool"):
        for size in pool.iter("size"):
            weights = {}
            templates = []
            for tag in size.iter("layertag"):
                weights[re.sub(r"(duo|trio)$", "", tag.get("name"))] = float(tag.get("weight"))
            for entry in size.iter("template"):
                templates.append(entry.get("id"))
            pools[(pool.get("id"), size.get("name"))] = (weights, templates)

    maps = {}
    levels = {}
    for entry in modes.iter("map"):
        maps[entry.get("id")] = entry.get("mastertemplate")
        # Duo and trio pools weigh their slots alike; the first one seen speaks for the floor.
        levels.setdefault(entry.get("mastertemplate"), (int(entry.get("difficultylevel")) + 1, entry.get("templatepool")))

    floors = {}
    for master in metadata.iter("master"):
        name = master.get("name")
        if name not in levels:
            continue
        level, pool = levels[name]
        text = template(dumps, name)
        low, high = pair(master.get("minimapBoundsMin")), pair(master.get("minimapBoundsMax"))
        slots = unique(ground(slot.get("pos")) + (slot.get("size") == "Medium",) for slot in master.iter("slot"))
        sections = {}
        for medium in (True, False):
            weights, names = pools[(pool, "medium" if medium else "small")]
            section = template(dumps, names[0])
            low_x, low_z, high_x, high_z = bounds(section, "editor")
            sections[medium] = (max(high_x - low_x, high_z - low_z) / 2, boss_note(weights))
        found = {kind: [] for kind in ("chest", "coffer", "key", "shrine", "up", "down", "soul")}
        altars = []
        for tile, pos in TILE.findall(text):
            if tile in ALTARS:
                altars.append(ground(pos) + (ALTARS[tile],))
                continue
            kind = ("chest" if tile == "TreasureSpawnpointLootchest" else "coffer" if tile == "TreasureSpawnpointCoffer"
                    else "key" if tile == "helldungeonkeyspawnMarker" else "soul" if "SOUL_EXIT" in tile
                    else "shrine" if "RESHRINE" in tile or "RESSHRINE" in tile else "up" if "_EXIT_UP" in tile
                    else "down" if "_EXIT_DOWN" in tile else None)
            if kind:
                found[kind].append(ground(pos))
        found["altar"] = unique(altars)
        floors[name] = (level, low, high, slots, sections,
                        outline(text, low, high, slots, {medium: sections[medium][0] for medium in sections}),
                        {kind: unique(values) for kind, values in found.items()})

    lines = [
        "// <auto-generated>",
        "// Written by scripts/generate-depths-map-data.py from the game's Depths templates; rerun it rather than editing.",
        "// </auto-generated>",
        "using System;",
        "using System.Collections.Generic;",
        "",
        "namespace AlbionDataAvalonia.Maps;",
        "",
        "internal static partial class DepthsMapData",
        "{",
        "    private static readonly Dictionary<string, string> Maps = new(StringComparer.OrdinalIgnoreCase)",
        "    {",
    ]
    for name, master in sorted(maps.items()):
        lines.append(f'        ["{name}"] = "{master}",')
    lines += [
        "    };",
        "",
        "    private static readonly Dictionary<string, Floor> Floors = new(StringComparer.OrdinalIgnoreCase)",
        "    {",
    ]
    for name, (level, low, high, slots, sections, rects, found) in sorted(floors.items()):
        slot_text = ", ".join(
            f'({number(x)}, {number(z)}, {number(sections[medium][0])}, "{sections[medium][1]}")'
            for x, z, medium in slots)
        rect_text = ", ".join(f"({', '.join(number(v) for v in rect)})" for rect in rects)
        lines += [
            f'        ["{name}"] = new({level}, ({number(low[0])}, {number(low[1])}), ({number(high[0])}, {number(high[1])}),',
            f"            [{slot_text}],",
            f"            [{rect_text}],",
        ]
        lines += [f"            [{points(found[kind])}]," for kind in ("chest", "coffer", "key", "shrine", "up", "down")]
        lines.append(f"            [{points(found['soul'])}],")
        lines.append(f"            [{', '.join(f'({number(x)}, {number(z)}, {grade})' for x, z, grade in found['altar'])}]),")
    lines += ["    };", "}", ""]
    target = ROOT / "AlbionDataAvalonia" / "Maps" / "DepthsMapData.g.cs"
    target.write_text("\n".join(lines), encoding="utf-8")
    print(f"Wrote {len(maps)} Depths maps on {len(floors)} floors to {target}")
    for name, floor in sorted(floors.items()):
        print(f"  {name}: level {floor[0]}, {len(floor[3])} slots, {len(floor[5])} outline rects, "
              + ", ".join(f"{len(v)} {k}" for k, v in floor[6].items()))


if __name__ == "__main__":
    if len(sys.argv) != 2:
        raise SystemExit("usage: generate-depths-map-data.py <ao-bin-dumps directory>")
    main(sys.argv[1])
