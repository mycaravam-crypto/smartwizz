# Structured engineering rules

The authoritative source for all engineering rules is [engineering-rules.json](engineering-rules.json).
Never manually edit the generated [ENGINEERING_STANDARD.md](../ENGINEERING_STANDARD.md); run
`python3 tools/engineering_rules.py --write` and commit both files.

Each rule has a stable `standard-*` ID, a standard section, a normative requirement,
an `appliesTo` path scope (empty means any file), a detection mode
(`llm`, `static`, `manual`), and severity metadata. Optional examples and
exceptions disambiguate semantic rules. Do not renumber existing IDs when
inserting rules: assign a new unique ID. Increment `version` whenever the
catalog changes.

At review time, SmartWizz loads the trusted JSON catalog from its own checkout,
selects only `llm` rules relevant to files in each request batch, and accepts
only existing, selected `standard-*` identifiers returned by the model.
Generic bug/security findings retain their normal freeform categories.
With `review.llmChecks`, the model sees aggregated checks instead; see [LLM checks](#llm-checks).
This design reduces irrelevant prompt context while preventing unknown standard
IDs from being published. `static` and `manual` rules remain documented;
enforce static rules using compiler/analyzer/linter and human judgment where
applicable. TypeScript `@/` imports are temporarily designated `llm` review
candidates and should also be enforced by ESLint in TypeScript consumers.

`ENGINEERING_STANDARD.md` is regenerated from the same JSON rule requirements,
so the human-readable standard cannot drift unnoticed (CI checks the generator).
Legacy `review.guidelines` Markdown is still accepted, but does not benefit
from scoped rules or catalog validation. Configure `review.guidelines` to
`rules/engineering-rules.json` for structured review.

## LLM checks

[llm-checks.json](llm-checks.json) is the model-facing view of the catalog. It is
enabled with `review.llmChecks` and is meant for small local models, which follow
a few short, concrete instructions much better than a long list of normative
rules. The catalog stays the authoritative standard; this file only decides how
its `llm` rules are put to the model.

Each check aggregates one or more related `llm` rules:

```json
{
  "id": "std-swallowed-exception",
  "rules": ["standard-4-1-05"],
  "files": ["@code"],
  "triggers": ["\\bcatch\\b|\\bexcept\\b"],
  "severity": "medium",
  "check": "A catch/except block must handle the error, add context and rethrow, or log it with context.",
  "bad": "catch (Exception) { return 0; }",
  "good": "catch (IOException ex) { throw new ReviewException(\"Read failed\", ex); }",
  "allowed": ["Top-level handlers that report and exit"]
}
```

- `id` — short descriptive `std-*` name. The model returns it as `ruleId`. SmartWizz
  reports the finding under the **first** rule in `rules`, so fingerprints and
  published threads keep using catalog IDs. A rule ID the check covers is accepted too.
- `files` — globs, or `@group` names from `fileGroups`. Narrower than the catalog's
  `appliesTo` on purpose: e.g. code-only checks are not sent for YAML.
- `triggers` — regexes (.NET syntax, keep them to what Python `re` also accepts)
  matched against **added** lines of in-scope files. The check is sent only if one
  matches. Keep triggers loose (high recall): the model decides whether the line
  really violates the check. An empty list means "always send for these files".
- `severity` — the hint shown to the model, and the order used when
  `review.maxStandardChecksPerRequest` has to drop checks.
- `check` — one or two imperative sentences, concrete enough to judge from a diff.
- `bad` / `good` / `allowed` — optional; one short line each.

Every `llm` rule must be covered by exactly one check or listed under `notChecked`
with a reason (for rules a diff cannot show, such as duplication across the
codebase). SmartWizz refuses to start otherwise, so new catalog rules cannot be
silently left out. The ruleset SHA-256 covers both files, so editing a check
triggers a full re-review like a catalog change.

To tune the file:

```sh
python3 tools/llm_checks.py --check               # coverage, groups and regexes (CI runs this)
python3 tools/llm_checks.py --stats               # checks and prompt size per file type
git diff origin/main... | python3 tools/llm_checks.py --preview -   # what a diff would send
```

Measure the effect with the [benchmark](../benchmarks/README.md): compare
recall/precision and `systemPromptCharacters` with and without `llmChecks`, on the
model you actually run.
