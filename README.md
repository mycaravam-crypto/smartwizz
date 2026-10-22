# SmartWizz

Small, lean AI pull request reviews. SmartWizz reads a PR diff from a local Git checkout, asks one OpenAI-compatible model for **bugs, security issues, and missing tests**, validates what comes back, and prints a report — optionally posting inline threads to Azure DevOps Server.

It never runs PR code, never fixes, approves, or merges anything, and is report-only by default.

## Requirements

- .NET 10 SDK
- `git`
- An OpenAI-compatible chat-completions endpoint: public (OpenAI, Gemini) or internal (vLLM, Ollama, LiteLLM, Azure OpenAI, ...)

## Usage

```sh
cp .env.example .env                       # put SMARTWIZZ_API_KEY in it (git-ignored)
# smartwizz.json is preconfigured for Gemini (Google AI Studio key); see smartwizz.example.json for others

dotnet run --project src/SmartWizz -- --repo ../my-repo --base origin/main
```

| Option | Meaning |
|---|---|
| `--base <ref>` | Branch/commit the PR merges into (alternative to `--pr`). Diff is `base...head` (from the merge-base). |
| `--pr <id>` | Resolve an Azure DevOps PR using the installed `ado` CLI, fetch its refs, and review its exact commit IDs. |
| `--head <ref>` | PR head, default `HEAD`. |
| `--since <sha>` | Optional full SHA of the last successfully reviewed commit. Review only changes since it when it is an ancestor of head; otherwise fall back to a full PR review. |
| `--full-review` | Review the complete PR diff even when `--since` is given. Earlier findings are still re-checked. |
| `--state-in <path>` | Previous review checkpoint (requires `--since` and `--state-out`). See [Incremental review](#incremental-review). |
| `--state-out <path>` | Write the review checkpoint after a fully successful run. |
| `--repo <path>` | Git checkout, default current directory. |
| `--config <path>` | Config file, default `./smartwizz.json` if present. |
| `--json <path>` | Also write the JSON report. |
| `--fail-on <severity>` | Exit 1 if any finding is at/above `low`/`medium`/`high`/`critical`. |
| `--annotated-diff <path>` | Write the PR unified diff with AI review comments directly after affected added lines; report-only. |\n| `--publish` | Post findings as Azure DevOps PR threads. |

**Exit codes:**

| Code | Meaning |
|---|---|
| `0` | Review completed; nothing at/above `--fail-on` (or no threshold) |
| `1` | Review completed; findings at/above `--fail-on` |
| `2` | Execution failure. The message names the kind: `[usage]`, `[configuration]`, `[git]`, `[provider]`, `[timeout]`, `[invalid-model-output]`, `[publish]` |
| `130` | Cancelled (Ctrl+C / SIGTERM). Outstanding requests stop, git is killed, and no partial review is reported. A second signal exits immediately. |

A failure in any request batch fails the whole run; a partial review is never reported as complete.

## Review an Azure DevOps PR with ado-devops (report-only)

Install and authenticate [ado-devops](https://github.com/mycaravam-crypto/ado-devops)
and run SmartWizz **inside a clone of the PR's repository**:

```sh
# SMARTWIZZ_ENDPOINT / SMARTWIZZ_MODEL configure the internal AI endpoint.
# SMARTWIZZ_API_KEY supplies the model gateway token.
smartwizz --repo . --pr 142 --annotated-diff review-142.diff --json review-142.json
```

SmartWizz invokes `ado pr context 142` to resolve the PR's exact target/source
commit IDs, then `ado pr diff 142` to fetch their Git objects. No branch checkout
is required, and your working tree is not modified. The resulting `review-142.diff`
contains inline `# REVIEW` annotations, so it is **not** a git-applicable patch.

The CLI performs **no Azure DevOps comment writes by default**. To publish validated
findings explicitly, supply `--publish` along with configured Azure DevOps collection
and project and an appropriate Azure DevOps token. For review-first workflows, inspect
the generated diff before a separate publication run. Publishing is not a selective
approval flow in this version.

`--base` and `--pr` are mutually exclusive; `--pr` also cannot be combined
with `--head`. The internal provider can be an OpenAI-compatible endpoint such as
Open WebUI's `/api/chat/completions`.

## Incremental review

Without `--since`, SmartWizz reviews the full PR diff. With `--since <40-character-SHA>`, it reviews only the changes since that previously reviewed commit. To track findings across runs, pass `--state-out <path>` on every run, and `--since <sha> --state-in <path>` from the second run on. The checkpoint (version 2) records the reviewed head SHA, a hash of the review configuration, and every finding with its status:

- **open**: reported and not yet verified as fixed. Unresolved findings from earlier runs stay in the `--fail-on` gate.
- **resolved**: a re-check of the *current* code confirmed the fix, or the file no longer exists. A finding is never resolved just because a rerun did not report it again.
- **regressed**: reported again after it was resolved. It counts toward the gate like an open finding.

On each run, earlier unresolved findings that were not reported again are re-checked if their file changed (after a complete review, all of them are). The re-check sends the model the finding and the current code around it, using a separate prompt (`prompts/revalidate.md`). Only an explicit, explained `resolved` verdict resolves a finding. Missing, contradictory or unknown verdicts keep it open, and malformed output fails the run. Findings in untouched files are carried forward without a model call.

SmartWizz runs a complete review instead of an incremental one when:

- `--full-review` is given (for example a security or release review),
- the checkpoint is missing, corrupt or from an older version,
- the review configuration, prompts or ruleset changed,
- the checkpoint SHA does not match `--since`,
- `--since` is not in the clone or not an ancestor of the head (force-push or rebase).

Earlier unresolved findings are still re-checked after a complete review, so none are silently dropped. The checkpoint is written only as the last step of a fully successful run. A failed review, re-check or publish never advances the baseline. The console report and the JSON report (`mode`, `modeReason`, `trackedFindings`) show which mode ran and why.

The reusable GitHub Actions workflow stores the checkpoint as a per-PR artifact and restores the newest one. It uses only checkpoints that this repository's own runs stored for the PR's branch, where the storing run reviewed exactly the commit the checkpoint names. Fork PRs therefore always get complete reviews. If checkpoints cannot be read (for example because the caller does not grant `actions: read`), the workflow warns and runs a complete review. Runs for the same PR are serialized by a concurrency group, and a queued run is replaced by a newer push. Set the `full-review` input to force a complete review.

## Configuration

See [`smartwizz.example.json`](smartwizz.example.json). Notable settings:

- `review.contextLines` — unchanged lines shown around each change (default 10).
- `review.maxCharsPerRequest` — files are packed into requests up to this size; a single larger file is skipped and listed, never truncated.
- `review.minConfidence` — findings below this are dropped (default 0.5).
- `review.exclude` — glob patterns matched against path and file name; replaces the built-in list (lockfiles, minified, generated, migrations).
- `provider.temperature` — `null` to omit for models that reject it.
- `provider.maxRetries` (default 2) and `provider.fallbackModels` (default none) — see *Overload handling* below.
- `review.guidelines` — path (relative to the config file) to a trusted versioned [JSON engineering rule catalog](rules/README.md) (`rules/engineering-rules.json`, default) or legacy Markdown standard. Only applicable LLM rules are injected per request batch. JSON standards enforce an exact `standard-*` rule ID allowlist and record `rulesetVersion` / `rulesetSha256` in the report. Keep this file in the trusted SmartWizz checkout in CI, never the untrusted PR.

Secrets are read only from the environment. For local runs you can put them in a git-ignored `./.env` (see [`.env.example`](.env.example)). Variables that are already set take precedence.

| Variable | Used for |
|---|---|
| `SMARTWIZZ_API_KEY` | Provider bearer token |
| `SMARTWIZZ_AZDO_TOKEN` or `SYSTEM_ACCESSTOKEN` | Azure DevOps PAT / pipeline token |

## Overload handling

Hosted models regularly answer "high demand" (503) or "rate limited" (429), and in CI that would fail a PR for reasons unrelated to the code. So:

1. **Retry:** on 429, 500, 502, 503, 504 or a dropped connection, the same model is retried up to `provider.maxRetries` times. The wait follows the server's `Retry-After`, otherwise 2 s then 4 s, capped at 30 s.
2. **Fall back:** if the model stays unavailable, the next entry in `provider.fallbackModels` is tried, with the same endpoint and key. Capacity is usually per model, so this tends to succeed immediately.
3. **Never mask real errors:** 400, 401, 403, 404 (wrong key, bad request, unknown model), timeouts and malformed answers fail at once and are not retried.

Every retry and fallback is logged to stderr. The report and JSON name the models that actually reviewed (`Reviewed by:` / `"models"`), because a fallback model may review differently.

## Output

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
- or a field is missing or invalid.

Unparseable model output fails the run with exit 2 `[invalid-model-output]`.

The `fingerprint` hashes rule, path, and the *content* of the start line, so it survives unrelated line shifts. It is used to dedupe within a run and to avoid re-posting the same thread on reruns.

## Azure DevOps Server

`--publish` posts one active thread per new finding on the PR's right-hand (new) side. Threads already carrying a finding's fingerprint are skipped.

Connection settings come from `azureDevOps.*` in the config, falling back to the pipeline variables `System.CollectionUri`, `System.TeamProject`, `Build.Repository.ID`, and `System.PullRequest.PullRequestId`. `apiVersion` defaults to `6.0` (Server 2020+); use `5.0` for Server 2019. `timeoutSeconds` (default 60) bounds each request. The token needs only code read and PR thread read/write.

Deduplication fails closed. The existing threads are read before anything is posted, and if that lookup fails, times out, or returns an unexpected shape, nothing is posted. The threads List API documents no paging in any version (it returns all threads). If a server paginates anyway (`x-ms-continuationtoken`), or `count` doesn't match the threads received, SmartWizz can't prove the list is complete, so it refuses to publish rather than risk duplicates. If a POST fails partway, the threads already posted are recognised on the next run.

See [`pipelines/azure-pipelines.yml`](pipelines/azure-pipelines.yml) for a branch-policy build. It reviews `origin/<target>...HEAD^2`, the PR source tip, so line numbers match the PR view.

## Internal / self-hosted model APIs

Any server that speaks the OpenAI chat-completions format works: vLLM, Ollama, LiteLLM, TGI, LM Studio, or an Azure OpenAI gateway. Nothing about the code is public-API specific.

```json
{
  "provider": {
    "endpoint": "https://llm.internal.example/v1/chat/completions",
    "model": "your-internal-model",
    "apiKeyHeader": "api-key"
  }
}
```

- **Auth:** by default the key is sent as `Authorization: Bearer <key>`. Set `provider.apiKeyHeader` to send the raw key in another header (e.g. `api-key` for Azure OpenAI). Leave `SMARTWIZZ_API_KEY` unset for servers without auth.
- **Endpoint/model per environment:** `SMARTWIZZ_ENDPOINT` and `SMARTWIZZ_MODEL` override the config file.
- **Internal CA:** add the CA to the machine's trust store, or on Linux point `SSL_CERT_FILE` / `SSL_CERT_DIR` at it. There is deliberately no "skip TLS verification" switch.
- **Proxy:** `HTTPS_PROXY` / `NO_PROXY` are honoured. Internal hosts usually belong in `NO_PROXY`.
- **Plain HTTP:** works, but SmartWizz warns, because code and key travel unencrypted.
- **Network reach:** the machine running SmartWizz must reach the endpoint. In GitHub that means a self-hosted runner (see below); GitHub-hosted runners can't see your internal network.

## GitHub

SmartWizz ships a reusable workflow. Add this file to any repository as `.github/workflows/smartwizz.yml`:

```yaml
name: SmartWizz
on: pull_request

jobs:
  smartwizz:
    # Fork PRs get no secrets and a read-only token: skip them.
    if: github.event.pull_request.head.repo.full_name == github.repository
    permissions:
      contents: read
      pull-requests: write                # inline review comments
      actions: read                       # restore the previous review checkpoint
    uses: mycaravam-crypto/smartwizz/.github/workflows/review.yml@main
    with:
      fail-on: high                       # optional; omit for report-only
      # comments: false                   # job summary + artifact only
      # full-review: true                # ignore the checkpoint's baseline (security/release review)
      # Internal API on a self-hosted runner:
      # runner: '["self-hosted", "linux"]'
      # endpoint: https://llm.internal.example/v1/chat/completions
      # model: your-internal-model
    secrets:
      SMARTWIZZ_API_KEY: ${{ secrets.SMARTWIZZ_API_KEY }}
```

Then add the `SMARTWIZZ_API_KEY` secret to that repository (*Settings → Secrets and variables → Actions*), or once at organization level.

What each PR gets:
- **Inline comments:** one PR review (`event: COMMENT`) with a comment on each new finding's line. It never approves and never requests changes. Comments sit on the finding's start line, which is always an added line; a multi-line range is named in the text, because a range could reach past GitHub's own diff context and get the whole review rejected.
- **Job summary and artifact:** the full report in the job summary, plus a `smartwizz-report` JSON artifact.
- **Required check:** with `fail-on`, the check fails and can be made required in branch protection.

Reruns don't repeat comments. Existing review comments are read first (all pages), and findings whose fingerprint is already there are skipped. If that lookup fails, nothing is posted. `@mentions` in model text are neutralized, so a review can't ping people.

The workflow reviews `base...head` of the PR and checks out SmartWizz itself for config and the engineering standard. Its token can read code and write review comments, nothing else. Outside Actions, `--publish-github` needs `GITHUB_TOKEN` (or `SMARTWIZZ_GITHUB_TOKEN`), `GITHUB_REPOSITORY`, `SMARTWIZZ_GITHUB_PR` and `SMARTWIZZ_GITHUB_SHA`, or the `gitHub.*` config keys.

Pin `@main` to a tag or commit SHA if you want reviews to stay unchanged until you upgrade deliberately (also pass the same value as `smartwizz-ref`).

Security notes:
- Use `on: pull_request`, **never** `pull_request_target`. PRs from forks then get no secrets, so they can't extract the key; for those PRs the review just fails.
- Anyone who can push a branch to the repository can change the workflow file, so they are trusted with the secret either way. That is the same boundary as any CI secret.

## Security

- PR content is untrusted. The diff is wrapped in random-nonce markers, and the prompt tells the model to ignore instructions inside it. Every finding is validated against the diff.
- Only the prompt and diff go to the model. No environment, config, or credentials are sent.
- Git runs with `--no-ext-diff --no-textconv`, so repository config can't execute commands. No other process is started.
- **Keep `smartwizz.json` out of the reviewed repository in CI.** A PR that can edit it can point `provider.endpoint` at its own server and receive `SMARTWIZZ_API_KEY`.
- Published comments are model-generated text and are labelled as AI output. Treat them like any other untrusted review comment.
- Tokens are sent only in request headers and never logged.

## Engineering standard at a glance

[ENGINEERING_STANDARD.md](ENGINEERING_STANDARD.md) is generated from the canonical [rules/engineering-rules.json](rules/engineering-rules.json) using `python3 tools/engineering_rules.py --write`. CI verifies synchronization. Its requirements are mandatory for production code, tests, configuration, build scripts, CI and AI-assisted changes. This table is a **summary, not a replacement** for the full normative text. Rules that cannot be checked mechanically still require a review.

| Section | Required rule (summary) | Verification focus |
|---|---|---|
| 1. Type safety and deterministic behavior | Explicit types at public boundaries; immutable state by default; strict nullability; typed domain states; validate untrusted values. | Compiler/nullability checks, analyzers, edge-case tests, reviewer |
| 2. Code hygiene and production readiness | Precise names, useful comments only, no obsolete/TODO-only code, debug output, unused code, secret logging or unjustified dependencies. | Formatter, static analysis, secret scanning, reviewer |
| 3. Functions, modularity and DRY | Single responsibility, isolate side effects, avoid duplicated business rules and needless abstractions; name constants and validate configuration. | Architecture/code review, focused unit tests |
| 4. Error handling and state safety | Validate at boundaries; never silently swallow errors; make state transitions atomic where needed; dispose resources; propagate cancellation and enforce timeouts. | Failure-path, concurrency and cancellation tests; reviewer |
| 5. Architecture and dependency boundaries | Stable domain logic must not depend on transport or providers; expose small explicit contracts; separate concerns without overengineering. | Dependency/architecture review and integration tests |
| 6. API, serialization and external data | Explicit schemas, validated deserialization and AI output, identifiers, paths and ranges; handle malformed/unsupported data without silent coercion. | Contract, malformed-input and negative tests |
| 7. Security by default | Least privilege; never execute untrusted PR content or obey embedded instructions; prevent traversal/injection, unsafe deserialization and secret disclosure; fail closed. | Security review, adversarial tests, CI permission audit |
| 8. Testing and verification | Test business rules, boundaries, edge cases and failures deterministically; use integration tests for contracts; run relevant checks and disclose failures. | Automated test suite, coverage of changed behavior, CI |
| 9. Language-specific enforcement | .NET: nullable, analyzers as errors, responsible `async`/`await`, cancellation and disposal. TypeScript: strict types, `unknown` narrowing, exhaustive unions and mandatory `@/` aliases for cross-directory internal imports (instead of `../`/`../../`). | `dotnet build`, `dotnet format`, TypeScript checks where applicable |
| 10. Automated enforcement | Run standard formatters, compilers, linters and analyzers in CI; block violations; justify narrow suppressions, avoid a custom rule engine. | Required CI checks and ruleset review |
| 11. AI-assisted change workflow | Inspect architecture first, implement the smallest focused increment, avoid unrelated refactors, verify and report limitations before completion. | PR diff, engineering review and verification evidence |
| 12. Definition of Done | Correct behavior, types/nullability, controlled errors/side effects, security, meaningful tests, green checks and no speculative complexity. | PR completion checklist and review |

**Benchmarking the LLM reviewer:** See [Reviewer benchmark methodology](benchmarks/README.md). The seeded-defect PR must stay unmerged. Its independent answer key is maintained separately so the model cannot simply copy expected findings from the PR diff. A passing compilation check alone does **not** prove that the semantic rules are applied.

## Development

Same checks as CI (`.github/workflows/ci.yml`):

```sh
python3 tools/engineering_rules.py --check  # generated Markdown has not drifted
dotnet format --verify-no-changes   # whitespace + .editorconfig style; run `dotnet format` to fix
dotnet build -c Release             # analyzers + code style enforced, warnings are errors
dotnet test -c Release
```

- `global.json` pins the SDK.
- `rules/engineering-rules.json` contains stable IDs, enforcement type, severity, language scopes and optional examples/exceptions. The LLM receives only applicable rules, while mechanical rules belong in compiler, analyzer and linter checks. Catalog version and SHA-256 identify each benchmark run.
- `.editorconfig` encodes the mechanical rules of [`ENGINEERING_STANDARD.md`](ENGINEERING_STANDARD.md): explicit types unless obvious, no unused code, readonly fields.
- Tests stub all HTTP and need no credentials. They make no live model or Azure DevOps calls.
- One test starts a fake `git` (Linux only) to prove cancellation kills the git process.
