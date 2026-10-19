# SmartWizz reviewer benchmark

This benchmark evaluates **LLM review findings**, not whether a compiler or analyzer catches every engineering rule. [ENGINEERING_STANDARD.md](../ENGINEERING_STANDARD.md) is the authoritative rule source.

## Execution

1. Keep the intentionally defective benchmark PR separate and unmerged. Use a fixed commit for the benchmark fixture and a pinned SmartWizz revision for each run.
2. Run the SmartWizz reviewer on the benchmark PR. The benchmark PR installs a self-review workflow calling `.github/workflows/review.yml@main`; a `SMARTWIZZ_API_KEY` Actions secret is needed for a live model run. CI compilation intentionally does not include the fixture.
3. Collect the `smartwizz-report` JSON artifact from that PR's Actions run, or use the JSON report from a local SmartWizz invocation. Record the actual model name from the report.
4. Save the baseline and candidate reports locally. Run `python3 benchmarks/score.py benchmarks/ground-truth.json baseline.json candidate.json --fixture /path/to/benchmark/benchmarks/fixtures/RuleViolations.cs`. This prints per-run candidate precision/recall/F1, ruleset versions/hashes, models and first-to-last deltas.
5. Review every candidate finding semantically. For a verified run create `adjudication.json` mapping **each** finding fingerprint to its defect ID (e.g. `B01`) or `null` for a false positive. Run `python3 benchmarks/score.py benchmarks/ground-truth.json report.json --fixture /path/to/benchmark/benchmarks/fixtures/RuleViolations.cs --adjudication adjudication.json`. Verified precision, recall and F1 are reported only if every finding has a verdict.

The ground truth should **not** be included in the defective PR. Only seed cases/actual source edits are sent to the LLM; reviewers must not receive the answer key. Keep the benchmark PR open and **never merge it**. It has intentional flaws and may cause CI failure if quality gates include the fixture.

## Metrics

- **Recall** = matched distinct seeded defects / total seeded defects.
- **Precision** = distinct matched findings / total findings; unmatched and duplicate findings are false positives until manually adjudicated.
- **F1** = harmonic mean of precision and recall.
- **LLM prompt cost:** compare `systemPromptCharacters` and `userPromptCharacters` (exact characters sent per batch). When the model API supplies complete usage metadata, also compare `promptTokens` and `completionTokens`; missing token counts are `null`, not zero. Character counts are not token estimates.
- **False negatives** = seeded cases not matched.
- Record exact model, configuration, commit SHA, ruleset version and SHA-256, plus review time and manual adjudication. Repeat runs to quantify nondeterminism; use the same pinned model/config/fixture for controlled before/after comparisons.

The canonical versioned rules live in [rules/engineering-rules.json](../rules/engineering-rules.json). Only `detection: "llm"` rules participate in LLM standard checks; `static` and `manual` rules are verified through other gates. The 16-defect corpus deliberately includes generic bugs plus coding-rule violations, including defects that could also be detected statically. Report the total-seeded denominator and any subset exclusions transparently when comparing reviewer versions.

The corpus samples representative objective violations but does not exhaust every normative requirement. In particular, architectural simplicity, DRY, naming and workflow quality involve context-sensitive human judgment. Style findings may be suppressed by the default reviewer instruction and should not be confused with compiler enforcement.

## Ground truth

The versioned `ground-truth.json` identifies each planted defect, standard section and expected location. Its `anchor` is an exact substring of the added line; matching tolerates line shifts while requiring a same-line anchor. It must be kept out of the benchmark diff and must not be included in model prompts.
