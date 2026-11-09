#!/usr/bin/env python3
"""Validate and tune a SmartWizz ruleset (see rulesets/README.md).

  python3 tools/ruleset.py check   [RULESET] [--catalog CATALOG]   validate; with a catalog, every llm rule must be covered
  python3 tools/ruleset.py stats   [RULESET]                       checks in scope and prompt size per file type
  python3 tools/ruleset.py preview [RULESET] --diff FILE|- [--max N]  checks a diff would send, split into passes

SmartWizz validates the same file when it loads it; this tool gives fast feedback while you write one.
Sizes are characters of check text (roughly 4 per token for English).
"""
import argparse
import fnmatch
import json
import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
DEFAULT = ROOT / "rulesets" / "engineering-standard.json"
SEVERITY = {"low": 0, "medium": 1, "high": 2, "critical": 3}
SAMPLES = ["src/App.cs", "tests/AppTests.cs", "web/page.tsx", "web/util.js", "tool.py", "main.go",
           "deploy.sh", "settings.json", ".github/workflows/ci.yml", "README.md"]
KEBAB = r"[a-z0-9]+(?:-[a-z0-9]+)*"


def load(path):
    doc = json.loads(Path(path).read_text(encoding="utf-8"))
    errors = []
    if doc.get("schemaVersion") != 1:
        errors.append("unsupported schemaVersion")
    if not str(doc.get("name", "")).strip() or not str(doc.get("version", "")).strip():
        errors.append("name and version are required")
    groups = doc.get("fileGroups", {})
    for name, globs in groups.items():
        if not re.fullmatch(KEBAB, name) or not globs or any(not g.strip() for g in globs):
            errors.append(f"invalid file group: {name}")
    ids = set()
    for check in doc.get("checks", []):
        cid = check.get("id", "")
        if not re.fullmatch("std-" + KEBAB, cid) or cid.lower() in ids:
            errors.append(f"invalid or duplicate check id: {cid}")
        ids.add(cid.lower())
        if check.get("severity") not in SEVERITY:
            errors.append(f"{cid}: invalid severity")
        if not check.get("check", "").strip() or not check.get("files"):
            errors.append(f"{cid}: check and files are required")
        check["_globs"] = []
        for entry in check.get("files", []):
            if entry.startswith("@"):
                if entry[1:] not in groups:
                    errors.append(f"{cid}: unknown file group {entry}")
                check["_globs"] += groups.get(entry[1:], [])
            else:
                check["_globs"].append(entry)
        check["_triggers"] = []
        for trigger in check.get("triggers", []):
            try:
                check["_triggers"].append(re.compile(trigger))
            except re.error as e:
                errors.append(f"{cid}: invalid trigger {trigger!r}: {e}")
    if not doc.get("checks"):
        errors.append("no checks defined")
    for entry in doc.get("notChecked", []):
        if not entry.get("ref", "").strip() or not entry.get("reason", "").strip():
            errors.append("every notChecked entry needs a ref and a reason")
    return doc, errors


def coverage(doc, catalog_path):
    """Each llm rule of the catalog must be referenced by exactly one check or notChecked entry."""
    catalog = json.loads(Path(catalog_path).read_text(encoding="utf-8"))
    llm = {line["rule"]["id"] for line in catalog["lines"] if "rule" in line and line["rule"]["detection"] == "llm"}
    owners, errors = {}, []
    refs = [(c["id"], r) for c in doc["checks"] for r in c.get("refs", [])]
    refs += [("notChecked", n["ref"]) for n in doc.get("notChecked", [])]
    for owner, ref in refs:
        if ref not in llm:
            errors.append(f"{owner}: {ref} is not an llm rule in {catalog_path}")
        elif ref in owners:
            errors.append(f"{owner}: {ref} is already covered by {owners[ref]}")
        owners[ref] = owner
    missing = sorted(llm - owners.keys())
    if missing:
        errors.append("llm rules without a check or notChecked entry: " + ", ".join(missing))
    return errors


def scoped(check, path):
    name = path.rsplit("/", 1)[-1].lower()
    return any(fnmatch.fnmatchcase(path.lower(), g.lower()) or fnmatch.fnmatchcase(name, g.lower()) for g in check["_globs"])


def render(check):
    text = f"[{check['id']}] {check['severity']}: {check['check']}\n"
    if "bad" in check:
        text += f"  Bad: {check['bad']}\n"
    if "good" in check:
        text += f"  Good: {check['good']}\n"
    if check.get("allowed"):
        text += "  Allowed: " + "; ".join(check["allowed"]) + "\n"
    return text


def added_lines(diff_text):
    files, path = {}, None
    for line in diff_text.splitlines():
        if line.startswith("+++ "):
            target = line[4:].strip()
            path = None if target == "/dev/null" else target.removeprefix("b/")
            if path:
                files.setdefault(path, [])
        elif path and line.startswith("+"):
            files[path].append(line[1:])
    return files


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("command", choices=["check", "stats", "preview"])
    parser.add_argument("ruleset", nargs="?", default=str(DEFAULT))
    parser.add_argument("--catalog", help="rule catalog whose llm rules the ruleset must cover (check)")
    parser.add_argument("--diff", help="unified diff file, or - for stdin (preview)")
    parser.add_argument("--max", type=int, default=0, help="review.maxChecksPerRequest to simulate (preview)")
    args = parser.parse_args()

    doc, errors = load(args.ruleset)
    if not errors and args.catalog:
        errors = coverage(doc, args.catalog)
    if errors:
        raise SystemExit(f"{args.ruleset} is invalid:\n  " + "\n  ".join(errors))
    checks = doc["checks"]

    if args.command == "check":
        covered = f", covers every llm rule of {args.catalog}" if args.catalog else ""
        print(f"{doc['name']} {doc['version']}: {len(checks)} checks valid{covered}.")
    elif args.command == "stats":
        print(f"{'file':28} {'checks in scope':>15} {'max chars':>10} {'~tokens':>8}")
        for path in SAMPLES:
            inscope = [c for c in checks if scoped(c, path)]
            chars = sum(len(render(c)) for c in inscope)
            print(f"{path:28} {len(inscope):>15} {chars:>10} {chars // 4:>8}")
        always = [c["id"] for c in checks if not c.get("triggers")]
        print("\nmax = every trigger fires. Checks without triggers are always sent for their files: " + (", ".join(always) or "none"))
    else:
        if not args.diff:
            parser.error("preview needs --diff")
        files = added_lines(sys.stdin.read() if args.diff == "-" else Path(args.diff).read_text(encoding="utf-8"))
        selected = [c for c in checks if any(scoped(c, p) and (not c["_triggers"] or any(t.search(l) for t in c["_triggers"] for l in lines))
                                             for p, lines in files.items())]
        selected.sort(key=lambda c: -SEVERITY[c["severity"]])
        size = args.max if args.max > 0 else max(len(selected), 1)
        passes = [selected[i:i + size] for i in range(0, len(selected), size)] or [[]]
        print(f"{len(selected)} of {len(checks)} checks in {len(passes)} request(s) per batch "
              f"(the first also runs the general bug/security review)")
        for n, chunk in enumerate(passes, 1):
            text = "".join(render(c) for c in chunk)
            print(f"\n--- request {n}: {len(chunk)} checks, {len(text)} chars (~{len(text) // 4} tokens)\n{text}", end="")


if __name__ == "__main__":
    main()
