"""Export only mechanical unlock IDs and equipment slots, never account state or art."""
import argparse
import json
import re
from pathlib import Path

ITEM = re.compile(r"[A-Za-z][A-Za-z_]*:[0-9]{1,18}\Z")
TRACK = re.compile(r"[A-Za-z][A-Za-z0-9_]{0,95}\Z")
CHARACTER = {"Costume", "MotionFinish", "MotionRetire", "MotionVictory", "MotionDefeat"}
KART = {"PartFront", "PartSide", "PartBack", "PartWheel", "PartHandle", "PartPlate", "KartBooster", "Balloon", "SkidMark"}

def export(unlocks, parts, tracks):
    items = sorted({row["id"] for row in unlocks["items"]})
    if not items or not all(ITEM.fullmatch(item) for item in items):
        raise ValueError("Invalid baseline item identifiers")
    known = set(items)
    bodies = sorted(set(unlocks["kartBodies"]))
    if not set(bodies) <= known or not all(item.startswith("Kart:") for item in bodies):
        raise ValueError("Invalid baseline body identifiers")
    def outfits(group, slots, prefix):
        result = {}
        for item, row in sorted(parts[group].items()):
            if item not in known or not item.startswith(prefix) or set(row) - slots:
                raise ValueError("Unknown outfit or slot")
            if not all(value in known for value in row.values()):
                raise ValueError("Outfit references an unknown item")
            result[item] = dict(sorted(row.items()))
        return result
    upgrades = {}
    for item, row in sorted(unlocks["kartUpgrades"].items()):
        if item not in known or not item.startswith("Kart:") or row["engineType"] not in (0, 1):
            raise ValueError("Invalid baseline upgrade definition")
        upgrades[item] = {"engineType": row["engineType"], "grade": row["grade"], "level": row["level"]}
    track_ids = list(dict.fromkeys(tracks["tracks"]))
    if not track_ids or not all(TRACK.fullmatch(track) for track in track_ids):
        raise ValueError("Invalid baseline tracks")
    common = parts.get("common", {})
    if set(common) - KART or not all(value in known for value in common.values()):
        raise ValueError("Invalid baseline common equipment")
    return {
        "schema": 1,
        "source": "retained-local-unlock-catalog",
        "supportedGameSha256": "5041d9f5d45adb4b1c932d8056a89f70db7fa98f93614ac30685cd1adeb9d9c2",
        "description": "Ownership/catalog baseline. Inclusion does not establish installed assets, factory handling or driving acceptance.",
        "items": items, "kartBodies": bodies,
        "common": dict(sorted(common.items())),
        "characters": outfits("characters", CHARACTER, "Character:"),
        "vehicles": outfits("vehicles", KART, "Kart:"),
        "upgradeDefinitions": upgrades, "tracks": track_ids,
    }

def main():
    parser = argparse.ArgumentParser(description=__doc__)
    for key in ("unlocks", "parts", "tracks", "output"):
        parser.add_argument("--" + key, type=Path, required=True)
    args = parser.parse_args()
    result = export(*(json.loads(getattr(args, key).read_text(encoding="utf-8-sig")) for key in ("unlocks", "parts", "tracks")))
    with args.output.open("x", encoding="utf-8") as out:
        json.dump(result, out, indent=2, ensure_ascii=False)
        out.write("\n")
    print({key: len(result[key]) for key in ("items", "kartBodies", "characters", "vehicles", "tracks")})

if __name__ == "__main__":
    main()
