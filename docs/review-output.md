# Review output and validation

When the model can express a fix within the reported lines, the console report shows it under the finding and the JSON includes it as `suggestedFix`. Suggestions are never applied.

Console report plus, with `--json`:

```json
{
  "version": 1,
  "reviewedFiles": 1,
  "findings": [
    { "ruleId": "wrong-operator", "severity": "high", "title": "add() subtracts",
      "message": "Uses - instead of +.", "filePath": "calc.py", "startLine": 2,
      "confidence": 0.95, "fingerprint": "517d4ce89541b97a" }
  ],
  "skippedFiles": [ { "path": "package-lock.json", "reason": "excluded" } ],
  "rejected": [ "calc.py:99: line not in diff" ]
}
```

`endLine` appears when a finding spans lines. Findings from the model are rejected (and listed) if:
- the file isn't in the reviewed diff (paths like `/x`, `./x`, `b/x`, `x\y` are normalized first),
- `startLine` isn't a line this change **added** (unchanged context lines don't count),
- `endLine` is before `startLine` or leaves the contiguous lines shown in that hunk,
- or a field is missing or invalid, including a missing, malformed, unknown or cross-file source reference.

Unparseable model output fails the run with exit 2 `[invalid-model-output]`.

The `fingerprint` hashes rule, path, and the *content* of the start line, so it survives unrelated line shifts. Legacy fingerprints support publication and checkpoints. Within a fresh run, equivalent canonical categories at the same validated location/range merge; distinct locations are preserved. Fingerprint collisions are disambiguated.

## Ruleset coverage and severity

Reports identify ruleset name, version and SHA-256. Applicable checks are sent to the model; counts record directly attributed standard IDs, not proof of compliance. Rejected findings can mark a request's checks incomplete. Skipped files limit coverage; cached reports lack fresh check coverage. Ruleset severity policies can enforce minimum or fixed severity; effective severity drives current findings and quality gates. See [ruleset reference](../rulesets/README.md). Source references are validated against the request's added lines and file; valid but semantically wrong references remain possible.
