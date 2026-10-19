#!/usr/bin/env python3
"""Score a SmartWizz JSON report against independent defect anchors (stdlib only)."""
import json
import sys

def score(key, report):
    defects = key["defects"]
    findings = report.get("findings", [])
    source = key["fixture"]
    # Anchors resolve against the fixture at evaluation time (not fed to the model).
    from pathlib import Path
    fixture = Path(source)
    if not fixture.exists():
        raise SystemExit(f"Fixture not found: {fixture} (run from benchmark PR checkout with key copied locally)")
    lines = fixture.read_text(encoding="utf-8").splitlines()
    expected = {}
    for d in defects:
        matches = [i for i, line in enumerate(lines, 1) if d["anchor"] in line]
        if len(matches) != 1:
            raise SystemExit(f"Expected a unique anchor for {d['id']}, got {len(matches)}")
        expected[d["id"]] = matches[0]
    matched = set()
    true_findings = 0
    for finding in findings:
        path = finding.get("filePath", "").replace("\\", "/").removeprefix("./")
        line = finding.get("startLine")
        hits = [d for d in defects if path == source and expected[d["id"]] == line and d["id"] not in matched]
        if hits:
            matched.add(hits[0]["id"])
            true_findings += 1
    false_positive = len(findings) - true_findings
    false_negative = len(defects) - len(matched)
    precision = true_findings / len(findings) if findings else 0
    recall = len(matched) / len(defects) if defects else 0
    f1 = 2 * precision * recall / (precision + recall) if precision + recall else 0
    return {"seeded": len(defects), "reported": len(findings), "matched": true_findings,
            "falsePositiveCandidates": false_positive, "falseNegativeCandidates": false_negative,
            "precisionCandidate": round(precision, 4), "recallCandidate": round(recall, 4),
            "f1Candidate": round(f1, 4), "matchedIds": sorted(matched),
            "unmatchedIds": sorted(d["id"] for d in defects if d["id"] not in matched),
            "note": "Location-only automatic matching. Manually validate finding semantics before final scoring."}

if __name__ == "__main__":
    if len(sys.argv) != 3:
        raise SystemExit("Usage: python3 benchmarks/score.py benchmarks/ground-truth.json report.json")
    with open(sys.argv[1], encoding="utf-8") as stream:
        truth = json.load(stream)
    with open(sys.argv[2], encoding="utf-8") as stream:
        report = json.load(stream)
    print(json.dumps(score(truth, report), indent=2))
