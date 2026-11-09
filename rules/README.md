# Structured engineering rules

[engineering-rules.json](engineering-rules.json) is the authoritative source of this
repository's engineering standard. Never manually edit the generated
[ENGINEERING_STANDARD.md](../ENGINEERING_STANDARD.md); run
`python3 tools/engineering_rules.py --write` and commit both files.

Each rule has a stable `standard-*` ID, a standard section, a normative requirement,
an `appliesTo` path scope (empty means any file), a detection mode
(`llm`, `static`, `manual`), and severity metadata. Optional examples and
exceptions disambiguate semantic rules. Do not renumber existing IDs when
inserting rules: assign a new unique ID. Increment `version` whenever the
catalog changes.

SmartWizz does not read this catalog. The model sees the `llm` rules through
[rulesets/engineering-standard.json](../rulesets/engineering-standard.json), which
translates them into short, trigger-gated checks; each check lists the rules it
enforces in `refs`. CI runs
`python3 tools/ruleset.py check --catalog rules/engineering-rules.json`, which fails
if an `llm` rule is neither referenced by exactly one check nor listed under
`notChecked`, so a new rule cannot be forgotten. When you add or change an `llm`
rule, update the ruleset in the same change.

`static` and `manual` rules remain documented; enforce static rules with the
compiler, analyzers and linters, and manual ones in human review. TypeScript `@/`
imports are temporarily `llm` review candidates and should also be enforced by
ESLint in TypeScript consumers.
