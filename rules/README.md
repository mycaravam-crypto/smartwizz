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
