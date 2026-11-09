#!/usr/bin/env python3
"""Validate rules/llm-checks.json against the rule catalog and show what it costs per request.

  python3 tools/llm_checks.py --check             validate (also run in CI)
  python3 tools/llm_checks.py --stats             checks and prompt size per file type
  python3 tools/llm_checks.py --preview DIFF      checks a unified diff would trigger, with the prompt text

SmartWizz validates the same file at load time; this tool is for fast feedback while editing.
Prompt sizes are approximate (characters, ~4 per token for English text).
"""
import argparse
import fnmatch
import json
import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
CATALOG = ROOT / "rules" / "engineering-rules.json"
CHECKS = ROOT / "rules" / "llm-checks.json"
SEVERITY = {"low": 0, "medium": 1, "high": 2, "critical": 3}
SAMPLES = ["src/App.cs", "tests/AppTests.cs", "web/page.tsx", "web/util.js", "tool.py", "main.go",
           "deploy.sh", "settings.json", ".github/workflows/ci.yml", "README.md"]


def load():
    catalog = json.loads(CATALOG.read_text(encoding="utf-8"))
    doc = json.loads(CHECKS.read_text(encoding="utf-8"))
    llm = {line["rule"]["id"] for line in catalog["lines"] if "rule" in line and line["rule"]["detection"] == "llm"}
    errors = []
    if doc.get("schemaVersion") != 1:
        errors.append("unsupported schemaVersion")
    groups = doc.get("fileGroups", {})
    covered, ids = {}, set()
    for check in doc.get("checks", []):
        cid = check.get("id", "")
        if not re.fullmatch(r"std-[a-z0-9]+(?:-[a-z0-9]+)*", cid) or cid in ids:
            errors.append(f"invalid or duplicate check id: {cid}")
        ids.add(cid)
        if check.get("severity") not in SEVERITY:
            errors.append(f"{cid}: invalid severity")
        if not check.get("check", "").strip() or not check.get("rules") or not check.get("files"):
            errors.append(f"{cid}: check, rules and files are required")
        globs = []
        for entry in check.get("files", []):
            if entry.startswith("@"):
                if entry[1:] not in groups:
                    errors.append(f"{cid}: unknown file group {entry}")
                globs += groups.get(entry[1:], [])
            else:
                globs.append(entry)
        check["_globs"] = globs
        check["_triggers"] = []
        for trigger in check.get("triggers", []):
            try:
                check["_triggers"].append(re.compile(trigger))
            except re.error as e:
                errors.append(f"{cid}: invalid trigger {trigger!r}: {e}")
        for rule in check.get("rules", []):
            if rule not in llm:
                errors.append(f"{cid}: {rule} is not an llm rule in the catalog")
            elif rule in covered:
                errors.append(f"{cid}: {rule} already covered by {covered[rule]}")
            covered[rule] = cid
    for skipped in doc.get("notChecked", []):
        if not skipped.get("reason", "").strip():
            errors.append(f"notChecked {skipped.get('rule')}: reason is required")
        if skipped.get("rule") in covered:
            errors.append(f"notChecked {skipped.get('rule')}: already covered by {covered[skipped['rule']]}")
        covered[skipped.get("rule")] = "notChecked"
    missing = sorted(llm - covered.keys())
    if missing:
        errors.append("llm rules without a check or notChecked entry: " + ", ".join(missing))
    return doc, errors


def scoped(check, path):
    name = path.rsplit("/", 1)[-1]
    return any(fnmatch.fnmatchcase(path.lower(), g.lower()) or fnmatch.fnmatchcase(name.lower(), g.lower())
               for g in check["_globs"])


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
    """Map each new-side path to its added lines."""
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
    parser = argparse.ArgumentParser()
    group = parser.add_mutually_exclusive_group(required=True)
    group.add_argument("--check", action="store_true")
    group.add_argument("--stats", action="store_true")
    group.add_argument("--preview", metavar="DIFF", help="unified diff file, or - for stdin")
    args = parser.parse_args()

    doc, errors = load()
    if errors:
        raise SystemExit("rules/llm-checks.json is invalid:\n  " + "\n  ".join(errors))
    checks = doc["checks"]
    if args.check:
        rules = sum(len(c["rules"]) for c in checks)
        print(f"LLM checks are valid: {len(checks)} checks cover {rules} llm rules, {len(doc.get('notChecked', []))} not checked.")
    elif args.stats:
        print(f"{'file':28} {'checks in scope':>15} {'max chars':>10} {'~tokens':>8}")
        for path in SAMPLES:
            inscope = [c for c in checks if scoped(c, path)]
            chars = sum(len(render(c)) for c in inscope)
            print(f"{path:28} {len(inscope):>15} {chars:>10} {chars // 4:>8}")
        print("\nmax = every trigger fires. Untriggered checks are not sent; checks without triggers always are:")
        print("  " + (", ".join(c["id"] for c in checks if not c.get("triggers")) or "(none)"))
    else:
        diff = sys.stdin.read() if args.preview == "-" else Path(args.preview).read_text(encoding="utf-8")
        files = added_lines(diff)
        selected = []
        for c in checks:
            hits = [p for p, lines in files.items() if scoped(c, p)
                    and (not c["_triggers"] or any(t.search(l) for t in c["_triggers"] for l in lines))]
            if hits:
                selected.append((c, hits))
        prompt = "".join(render(c) for c, _ in selected)
        for c, hits in selected:
            print(f"{c['id']:30} {c['severity']:7} {', '.join(hits)}")
        print(f"\n{len(selected)} of {len(checks)} checks, {len(prompt)} chars (~{len(prompt) // 4} tokens):\n")
        print(prompt, end="")


if __name__ == "__main__":
    main()
