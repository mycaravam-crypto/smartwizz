# SmartWizz reviewer benchmark

This benchmark evaluates **LLM review findings**, not whether a compiler or analyzer catches every engineering rule. [ENGINEERING_STANDARD.md](../ENGINEERING_STANDARD.md) is the authoritative rule source.

## Execution

1. Merge the separate README/methodology PR first, if desired; do **not** merge the intentionally defective benchmark PR.
2. Run the SmartWizz reviewer on the benchmark PR. The benchmark PR installs a self-review workflow calling `.github/workflows/review.yml@main`; a `SMARTWIZZ_API_KEY` Actions secret is needed for a live model run. CI compilation intentionally does not include the fixture.
3. Collect the `smartwizz-report` JSON artifact from that PR's Actions run, or use the JSON report from a local SmartWizz invocation. Record the actual model name from the report.
4. Save the report as `report.json` locally. Run `python3 benchmarks/score.py benchmarks/ground-truth.json report.json`.
5. Review each auto-matched finding and false positive manually. The matcher accepts only a matching added-line location in the same file; it does not establish semantic correctness. A finding must describe the intended defect to count as a true positive. Refine the manually verified results if necessary.

The ground truth should **not** be included in the defective PR. Only seed cases/actual source edits are sent to the LLM; reviewers must not receive the answer key. Keep the benchmark PR open and **never merge it**. It has intentional flaws and may cause CI failure if quality gates include the fixture.

## Metrics

- **Recall** = matched distinct seeded defects / total seeded defects.
- **Precision** = distinct matched findings / total findings; unmatched and duplicate findings are false positives until manually adjudicated.
- **F1** = harmonic mean of precision and recall.
- **False negatives** = seeded cases not matched.
- Record exact model, configuration, commit SHA and review time, plus manual adjudication. Repeat runs to quantify nondeterminism and compare models.

The corpus samples representative objective violations but does not exhaust every normative requirement. In particular, architectural simplicity, DRY, naming and workflow quality involve context-sensitive human judgment. Style findings may be suppressed by the default reviewer instruction and should not be confused with compiler enforcement.

## Ground truth

The versioned `ground-truth.json` identifies each planted defect, standard section and expected location. Its `anchor` is an exact substring of the added line; matching tolerates line shifts while requiring a same-line anchor. It must be kept out of the benchmark diff and must not be included in model prompts.
