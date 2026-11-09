Review the changes in the diff above and report only specific, actionable problems introduced or exposed by the change.

Report:
- Bugs: logic errors, wrong conditions, off-by-one, null/undefined dereference, unhandled errors, race conditions, resource leaks, broken edge cases.
- Security issues: injection, missing authorization/validation at trust boundaries, secrets in code, unsafe deserialization, path traversal, weak crypto.
- Missing tests: new or changed non-trivial logic with no corresponding test change in the diff. Anchor the finding on the changed logic.

Do not report:
- Style, formatting, naming, or documentation preferences, unless a listed standard check covers them.
- Speculation you cannot support from the visible code.
- Problems in unchanged code unless the change makes them reachable.
- The same problem more than once.

Use a short kebab-case category as `ruleId`, e.g. `null-dereference`, `sql-injection`, `missing-test`.
