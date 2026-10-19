#!/usr/bin/env python3
"""Generate or verify the readable engineering standard from the canonical JSON rules."""
import argparse
import json
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
SOURCE = ROOT / "rules" / "engineering-rules.json"
TARGET = ROOT / "ENGINEERING_STANDARD.md"
HEADER = "<!-- Generated from rules/engineering-rules.json; do not edit by hand. -->\n"


def render(catalog):
    if catalog.get("schemaVersion") != 1 or not catalog.get("version"):
        raise ValueError("Unsupported or missing rule-catalog schema/version")
    seen = set()
    output = []
    for item in catalog["lines"]:
        if "text" in item:
            output.append(item["text"])
            continue
        rule = item["rule"]
        rule_id = rule["id"]
        if not rule_id.startswith("standard-") or rule_id in seen:
            raise ValueError(f"Invalid or duplicate rule ID: {rule_id}")
        seen.add(rule_id)
        output.append(f'- {rule["requirement"]} <!-- {rule_id} -->')
    if not seen:
        raise ValueError("Empty rule catalog")
    return HEADER + "\n".join(output)


def main():
    parser = argparse.ArgumentParser()
    group = parser.add_mutually_exclusive_group(required=True)
    group.add_argument("--write", action="store_true")
    group.add_argument("--check", action="store_true")
    args = parser.parse_args()
    expected = render(json.loads(SOURCE.read_text(encoding="utf-8")))
    if args.write:
        TARGET.write_text(expected, encoding="utf-8")
    elif not TARGET.exists() or TARGET.read_text(encoding="utf-8") != expected:
        raise SystemExit("ENGINEERING_STANDARD.md differs from the rule catalog. Run python3 tools/engineering_rules.py --write.")
    print("Engineering standard documentation is synchronized with the rule catalog.")


if __name__ == "__main__":
    main()
