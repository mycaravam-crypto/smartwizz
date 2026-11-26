# Development and verification

## Development

Same checks as CI (`.github/workflows/ci.yml`):

```sh
python3 tools/engineering_rules.py --check  # generated Markdown has not drifted
python3 tools/ruleset.py check --catalog rules/engineering-rules.json  # ruleset valid and covers every llm rule
dotnet format --verify-no-changes   # whitespace + .editorconfig style; run `dotnet format` to fix
dotnet build -c Release             # analyzers + code style enforced, warnings are errors
dotnet test -c Release
```

- `global.json` pins the SDK.
- `rules/engineering-rules.json` is the human-facing engineering standard (it generates `ENGINEERING_STANDARD.md`). The model sees its `llm` rules only through `rulesets/engineering-standard.json`; mechanical rules belong in compiler, analyzer and linter checks. Ruleset name, version and SHA-256 identify each benchmark run.
- `.editorconfig` encodes the mechanical rules of [`ENGINEERING_STANDARD.md`](../ENGINEERING_STANDARD.md): explicit types unless obvious, no unused code, readonly fields.
- Tests stub all HTTP and need no credentials. They make no live model or Azure DevOps calls.
- One test starts a fake `git` (Linux only) to prove cancellation kills the git process.

## Definition of Done

- Shared review engine works in local and CI modes.
- Validated reports, publication and quality gates preserve their contracts.
- Meaningful automated tests cover changed behavior and failure paths; CI is green.
- Measure semantic review changes against pinned fixtures; disclose unverified behavior.
- Keep documentation current, concise and aligned with the [engineering standard](../ENGINEERING_STANDARD.md).
