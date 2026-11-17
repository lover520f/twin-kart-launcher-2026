"""Convert measured local handling into reviewable compatibility configuration.

Inputs stay on the owner's machine. The output contains numeric gameplay
configuration and resource identifiers, without account records or game files.
"""
import argparse
import base64
import hashlib
import json
from pathlib import Path
import struct


def load(path):
    return json.loads(path.read_text(encoding="utf-8-sig"))


def float32(value):
    return struct.unpack("<f", struct.pack("<f", value))[0]


def neutral_body(catalog, capture):
    body = bytearray(base64.b64decode(capture["packedBase"], validate=True))
    if len(body) != 205:
        raise ValueError("Expected the supported Steam body layout")
    touched = set()
    for gear, variants in catalog["effects"].items():
        points = capture["settings"].get(gear, 0)
        if points <= 0:
            continue
        for name, effect in variants[str(points)].items():
            if name in touched:
                raise ValueError("The input has overlapping adjustment fields")
            touched.add(name)
            if not effect:
                continue
            field = catalog["fields"][name]
            offset, kind = field["offset"], field["kind"]
            if kind == "bool":
                body[offset] = 0
            elif kind == "u32":
                current = struct.unpack_from("<I", body, offset)[0]
                struct.pack_into("<I", body, offset, current - int(effect))
            elif kind == "f32":
                current = struct.unpack_from("<f", body, offset)[0]
                struct.pack_into("<f", body, offset, current - float32(effect))
            else:
                raise ValueError("Unknown field encoding")
    return bytes(body)


def export(physics, rules, settings):
    catalog = load(physics)
    tuning = load(rules)["rules"]
    defaults = load(settings)
    compatible = {name: rule for name, rule in tuning.items() if rule.get("clientCompatible", True)}
    defaults = {name: value for name, value in defaults.items() if name in compatible}
    for name, points in defaults.items():
        if type(points) is not int or points not in compatible[name]["values"]:
            raise ValueError("Default gear choices do not match the local rules")

    profiles = {}
    vehicles = {}
    profile_ids = {}
    for kart, capture in sorted(catalog["vehicles"].items()):
        body = neutral_body(catalog, capture)
        if body not in profile_ids:
            identifier = "compatibility-" + str(len(profiles) + 1)
            profile_ids[body] = identifier
            values = {}
            for name, field in catalog["fields"].items():
                kind, offset = field["kind"], field["offset"]
                if kind == "bool":
                    if body[offset] > 1:
                        raise ValueError("Invalid local boolean")
                    values[name] = bool(body[offset])
                else:
                    values[name] = struct.unpack_from("<I" if kind == "u32" else "<f", body, offset)[0]
            profiles[identifier] = values
        vehicles[kart] = {"engineType": capture["engineType"], "bodyProfile": profile_ids[body]}

    return {
        "schema": 1,
        "source": "measured-local-compatibility",
        "factoryParametersVerified": False,
        "supportedGameSha256": "5041d9f5d45adb4b1c932d8056a89f70db7fa98f93614ac30685cd1adeb9d9c2",
        "description": "Compatibility handling reconstructed from the working local setup. Original per-kart factory parameters remain unverified.",
        "fields": catalog["fields"],
        "bodyProfiles": profiles,
        "vehicles": vehicles,
        "tuningRules": tuning,
        "tuningEffects": catalog["effects"],
        "defaultGearSettings": defaults,
    }


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--physics", required=True, type=Path)
    parser.add_argument("--rules", required=True, type=Path)
    parser.add_argument("--default-settings", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    args = parser.parse_args()
    result = export(args.physics, args.rules, args.default_settings)
    with args.output.open("x", encoding="utf-8") as output:
        json.dump(result, output, indent=2, ensure_ascii=False, allow_nan=False)
        output.write("\n")
    print("Compatibility vehicle records", len(result["vehicles"]), "neutral profiles", len(result["bodyProfiles"]))
    print("Output SHA-256", hashlib.sha256(args.output.read_bytes()).hexdigest())


if __name__ == "__main__":
    main()
