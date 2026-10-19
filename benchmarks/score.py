#!/usr/bin/env python3
"""Compare SmartWizz JSON reports against a versioned, independent defect catalog."""
import argparse
import json
from pathlib import Path


def locations(key, fixture):
    lines = fixture.read_text(encoding="utf-8").splitlines()
    found = {}
    for defect in key["defects"]:
        matches = [n for n, line in enumerate(lines, 1) if defect["anchor"] in line]
        if len(matches) != 1:
            raise ValueError(f"Expected a unique anchor for {defect['id']}; got {len(matches)}")
        found[defect["id"]] = matches[0]
    return found


def score(key, report, expected, adjudication=None):
    defects = key["defects"]
    findings = report.get("findings", [])
    if not isinstance(findings, list):
        raise ValueError("Report 'findings' must be an array")
    source = key["fixture"]
    matches = set()
    matched_fps = set()
    for finding in findings:
        path = finding.get("filePath", "").replace("\\", "/").removeprefix("./")
        line = finding.get("startLine")
        candidate = next((defect for defect in defects if path == source
                          and expected[defect["id"]] == line
                          and defect["id"] not in matches), None)
        if candidate is not None:
            matches.add(candidate["id"])
            matched_fps.add(finding["fingerprint"])

    n, total = len(findings), len(defects)
    tp = len(matches)
    precision = tp / n if n else 0.0
    recall = tp / total if total else 0.0
    f1 = 2 * precision * recall / (precision + recall) if precision + recall else 0.0
    result = {
        "seeded": total, "reported": n, "matched": tp,
        "falsePositiveCandidates": n - tp, "falseNegativeCandidates": total - tp,
        "precisionCandidate": round(precision, 4), "recallCandidate": round(recall, 4),
        "f1Candidate": round(f1, 4), "matchedIds": sorted(matches),
        "unmatchedIds": sorted(defect["id"] for defect in defects if defect["id"] not in matches),
        "models": report.get("models", []),
        "rulesetVersion": report.get("rulesetVersion"),
        "rulesetSha256": report.get("rulesetSha256"),
        "note": "Automatic matches are only location candidates; review semantics manually."
    }
    if adjudication is not None:
        # Explicitly classify every finding by fingerprint: defect ID or null (false positive).
        if set(adjudication) != {f["fingerprint"] for f in findings}:
            raise ValueError("Adjudication must contain exactly one verdict per distinct finding fingerprint")
        known_ids = {d["id"] for d in defects}
        approved = set()
        for finding in findings:
            defect_id = adjudication[finding["fingerprint"]]
            if defect_id is None:
                continue
            if defect_id not in known_ids or defect_id in approved:
                raise ValueError(f"Invalid or duplicate verified defect ID: {defect_id}")
            if (finding.get("filePath", "").replace("\\", "/").removeprefix("./") != source
                    or finding.get("startLine") != expected[defect_id]):
                raise ValueError(f"Verified defect ID {defect_id} does not match location")
            approved.add(defect_id)
        tp = len(approved)
        p = tp / n if n else 0.0
        r = tp / total if total else 0.0
        f = 2 * p * r / (p + r) if p + r else 0.0
        result["verified"] = {"truePositive": tp, "falsePositive": n - tp,
            "falseNegative": total - tp, "precision": round(p, 4),
            "recall": round(r, 4), "f1": round(f, 4), "matchedIds": sorted(approved)}
    return result


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("ground_truth", type=Path)
    parser.add_argument("reports", nargs="+", type=Path)
    parser.add_argument("--fixture", type=Path, help="Path to the checked-out defective source fixture")
    parser.add_argument("--adjudication", type=Path,
                        help="Optional JSON object of fingerprint -> defect ID or null; one report only")
    args = parser.parse_args()
    if args.adjudication and len(args.reports) != 1:
        parser.error("--adjudication requires exactly one report")

    key = json.loads(args.ground_truth.read_text(encoding="utf-8"))
    fixture = args.fixture or Path(key["fixture"])
    if not fixture.is_file():
        parser.error(f"Fixture not found: {fixture}. Provide --fixture from the benchmark PR checkout.")
    expected = locations(key, fixture)
    verdicts = json.loads(args.adjudication.read_text(encoding="utf-8")) if args.adjudication else None
    scores = {}
    for report_path in args.reports:
        report = json.loads(report_path.read_text(encoding="utf-8"))
        scores[str(report_path)] = score(key, report, expected, verdicts)
    if len(scores) > 1:
        first = next(iter(scores.values()))
        last = list(scores.values())[-1]
        scores["deltaLastVsFirst"] = {
            metric: round(last[metric] - first[metric], 4)
            for metric in ("precisionCandidate", "recallCandidate", "f1Candidate")
        }
    print(json.dumps(scores, indent=2))


if __name__ == "__main__":
    main()
