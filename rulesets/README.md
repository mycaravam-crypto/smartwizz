# Rulesets

A ruleset is a coding guide translated into checks that a small model can apply to a
diff. **One file is one guide.** To review against another guide, point
`review.ruleset` at another file. Nothing else changes.

```json
{ "review": { "ruleset": "rulesets/engineering-standard.json" } }
```

| File | Guide |
|---|---|
| [engineering-standard.json](engineering-standard.json) | This repository's [Production Engineering Standard](../ENGINEERING_STANDARD.md) |

Every review also looks for bugs, security issues and missing tests. A ruleset adds
compliance with the guide on top of that; without one, SmartWizz reviews only bugs,
security issues and missing tests.

## How SmartWizz uses it

1. For each batch of files, a check is **applicable** when a file matches its
   `files` and one of its `triggers` matches an **added** line. A check without
   triggers applies to every matching file.
2. Applicable checks are sorted by severity. The first request sends the general
   review plus the first `review.maxChecksPerRequest` checks. Further requests
   send only the remaining checks, with a prompt that asks for nothing else.
   `0` means all checks go in the first request.
3. A finding may use only the IDs of checks sent in its request. Any other `std-*`
   ID, or anything other than a check in a follow-up request, is rejected and
   listed under `rejected` in the report.
4. The report records the ruleset's `name`, `version` and SHA-256. Editing the
   file changes the hash, which makes the next incremental review a full one.

## Format

```jsonc
{
  "schemaVersion": 1,
  "name": "Production Engineering Standard",   // shown in the report
  "version": "1.0.0",                          // bump when you change checks
  "fileGroups": {                              // reusable glob lists, referenced as "@code"
    "code": ["*.cs", "*.ts", "*.py"],
    "tests": ["*Tests.cs", "*.test.ts", "tests/*", "*/tests/*"]
  },
  "checks": [
    {
      "id": "std-swallowed-exception",          // std- + kebab-case; what the model returns as ruleId
      "severity": "medium",                    // low | medium | high | critical; also the pass order
      "files": ["@code"],                      // globs or @groups, matched on path and file name
      "triggers": ["\\bcatch\\b|\\bexcept\\b"],  // regexes on added lines; [] = always for these files
      "check": "A catch/except block must handle the error, add context and rethrow, or log it with context.",
      "bad": "catch (Exception) { return 0; }", // optional, one line
      "good": "catch (IOException ex) { throw new ReviewException(\"Read failed\", ex); }",
      "allowed": ["Top-level handlers that report and exit"],  // optional exceptions
      "refs": ["4.1.05"]                        // optional: guide sections/rule IDs; never sent to the model
    }
  ],
  "notChecked": [                              // optional: guide parts deliberately not checked, and why
    { "ref": "3.2.01", "reason": "Duplication across the codebase is not visible in a diff." }
  ]
}
```

The file is plain JSON (the comments above are only for explanation). Triggers use .NET regex syntax; stay within what Python `re`
also accepts, so that `tools/ruleset.py` can test them.

## Writing checks that small models follow

- **One idea per check**, in one or two imperative sentences that can be judged
  from the added lines alone. "Pass the caller's CancellationToken to async calls"
  works; "keep the architecture clean" does not.
- **Always add a Bad/Good pair.** Small models copy patterns far more reliably than
  they apply abstract rules. Keep each to one line in the guide's main language.
- **Merge rules that say the same thing** (e.g. two "no commented-out code" rules)
  into one check with both refs, so they cannot be reported twice.
- **Triggers are for recall, the model is for precision.** A trigger should fire on
  anything that *could* violate the check (`catch`, `password\s*=`, `.Result`); the
  model decides whether it really does. A trigger that never fires silently disables
  its check, so test it with `preview`.
- **Scope tightly.** Code checks should not be sent for YAML or JSON; use file groups.
- **Leave out what a diff cannot show**: duplication across files, dependency
  cycles, overall design. List those under `notChecked` with the reason, so a
  reader can see the gap is deliberate.
- **Use severity on purpose.** With `maxChecksPerRequest`, high-severity checks go in
  the first request together with the bug review; low ones may come last.

## Translating a coding guide

1. Copy `engineering-standard.json` as a template, set `name` and `version`, and
   delete its checks.
2. Go through the guide section by section. For each rule, decide: a **check**
   (visible in a diff), **notChecked** (needs whole-codebase or human judgment), or
   left to a **linter/compiler** (formatting, naming, explicit types). Do not ask
   the model to do a linter's job.
3. Write the checks as described above. A large model can draft them for you; give
   it the guide and this instruction, then review every check yourself:

   > Translate this coding guide into a SmartWizz ruleset (format below). One check per
   > idea that can be judged from added diff lines alone; merge duplicates. Each check:
   > a `std-` kebab-case id, severity, file globs, regex triggers that fire on any line
   > that could violate it, a one- or two-sentence imperative `check`, a one-line `bad`
   > and `good` example, and `refs` naming the guide section. List rules that need the
   > whole codebase or human judgment under `notChecked` with a reason. Leave out
   > formatting and naming rules a linter can enforce.

4. Validate and tune:

   ```sh
   python3 tools/ruleset.py check   rulesets/my-guide.json
   python3 tools/ruleset.py stats   rulesets/my-guide.json
   git diff origin/main... | python3 tools/ruleset.py preview rulesets/my-guide.json --diff - --max 8
   ```

   If the guide also exists as a rule catalog like
   [rules/engineering-rules.json](../rules/engineering-rules.json), add
   `--catalog <file>` to `check`: it fails unless every `llm` rule is referenced by
   exactly one check or `notChecked` entry.

5. Measure: run the [benchmark](../benchmarks/README.md), or a few real PRs with known
   problems, on the model you will use, and compare findings before and after changes.
