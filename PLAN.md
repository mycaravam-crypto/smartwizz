# SmartWizz — Implementation Plan

## Goal

A small, reliable AI-powered Pull Request reviewer that reports actionable bugs, security issues, and missing tests.

SmartWizz is standalone and separate from GitWizz (merge ordering / merge readiness).

## Principles

- **YAGNI:** implement only what the MVP requires.
- Simple, deterministic code over abstractions. No interfaces with one implementation.
- Review logic independent of CI platform and AI provider (provider and publisher are plain classes taking an `HttpClient`).
- All PR content is untrusted input.
- Never auto-fix, approve, or merge.
- No web UI, database, agent framework, or plugin system.
- **Zero runtime NuGet dependencies.** BCL only (`System.Text.Json`, `HttpClient`, `Process`). Test project may use xUnit.

## MVP

1. Get the PR diff from a local Git checkout: `git diff <base>...<head>` (merge-base semantics).
2. Send changed code with limited context (`-U<contextLines>`) to one OpenAI-compatible chat-completions endpoint.
3. Parse structured findings from the model's JSON reply.
4. Validate, filter, and deduplicate findings.
5. Print a readable console report; optionally write JSON (`--json <path>`).
6. Publish inline PR threads to Azure DevOps Server only with `--publish`.
7. CI exit codes with an optional severity threshold (`--fail-on`).

## Technology

- C# / .NET 10 (current LTS), single console project.
- Git CLI for diffs, invoked with `--no-ext-diff --no-textconv` so repo config can't run commands.
- One provider: OpenAI-compatible `POST /chat/completions` over raw `HttpClient`. No SDK.
- No `response_format` (support varies across local/hosted servers); the prompt demands JSON and the parser tolerates code fences.
- JSON config + CLI flags; secrets only from environment variables.

## Structure

Flat — one file per concern, no subfolders until something needs them.

```text
src/SmartWizz/
  Program.cs        CLI parsing, orchestration, exit codes
  Config.cs         config file + env defaults
  GitDiff.cs        run git, parse unified diff, annotate line numbers
  Review.cs         batching, provider calls, finding parse/validate/dedupe/fingerprint
  Provider.cs       OpenAI-compatible client
  Report.cs         console + JSON output
  AzureDevOps.cs    PR thread publisher
prompts/code-review.md          system prompt (embedded resource)
tests/SmartWizz.Tests/
pipelines/azure-pipelines.yml
smartwizz.example.json
README.md
```

`schemas/review-result.schema.json` is dropped: validation lives in code (no JSON-schema dependency), and the output shape is documented in the README.

## Diff Handling

- Skip deleted files, binary files, and paths matching `review.exclude` globs (matched against full path and file name; defaults cover lockfiles, minified and generated files).
- Each line sent to the model is annotated with its **new-file line number** (`  42 |+code`); removed lines get a blank number. Models are bad at deriving line numbers from hunk headers — this is the single biggest accuracy lever.
- Files are packed greedily into requests up to `review.maxCharsPerRequest`. A single file over the budget is skipped and listed as skipped (never silently truncated).

## Model Output

```json
{ "findings": [ { "ruleId": "null-dereference", "severity": "high", "title": "...",
  "message": "...", "filePath": "src/a.cs", "startLine": 42, "endLine": 44, "confidence": 0.8 } ] }
```

- `severity`: `low | medium | high | critical`; `confidence`: 0..1; `endLine` optional.
- Only specific, actionable findings supported by the diff. No style nits, speculation, or duplicates. No findings → `{"findings": []}`.

## Validation

- Whole reply not parseable as `{ "findings": [...] }` → execution failure (exit 2).
- Individual finding invalid → dropped and counted as rejected: unknown severity, empty fields, file not in the reviewed batch, `startLine`/`endLine` not a visible new-side line in the diff, `endLine < startLine`, confidence out of range.
- Findings below `review.minConfidence` (default 0.5) are dropped.
- Fingerprint = first 16 hex of SHA-256 over `ruleId | filePath | whitespace-normalized text of startLine`. Uses line *content*, not number, so it survives unrelated line shifts on rerun.
- Dedupe by fingerprint, keeping the highest severity then confidence.

## Safety

- Never execute PR code. Git is the only process started.
- Diff is wrapped in random-nonce markers; prompt states content inside is data, not instructions.
- Only the diff and the prompt are sent to the model — no env, config, or tokens.
- Credentials: `SMARTWIZZ_API_KEY` (provider), `SMARTWIZZ_AZDO_TOKEN` or `SYSTEM_ACCESSTOKEN` (Azure DevOps). The config file has no secret fields. Never logged.
- Report-only by default; publishing requires `--publish`.
- Published comments are clearly labelled as AI-generated.
- In CI, config comes from the trusted SmartWizz repo, never the PR checkout. Otherwise a PR could redirect `provider.endpoint` and capture the API key.
- HTTP bodies are buffered (`Content-Length` set). Chunked bodies broke a plain HTTP server in the smoke test.

## Azure DevOps Server

- Settings from config, falling back to pipeline variables (`SYSTEM_COLLECTIONURI`, `SYSTEM_TEAMPROJECT`, `BUILD_REPOSITORY_ID`, `SYSTEM_PULLREQUEST_PULLREQUESTID`), so the pipeline needs no config for them.
- `GET .../pullRequests/{id}/threads`, skip findings whose fingerprint already appears in an existing comment, then `POST` one thread per new finding with `threadContext` (`filePath`, `rightFileStart/End`).
- Fingerprint rendered as a visible footer (`SmartWizz · <fp>`) — HTML comments are not reliably hidden/preserved across server versions.
- The pipeline reviews `base...HEAD^2` (PR source tip, not the merge commit), so line numbers match the PR's right-hand side.
- `apiVersion` configurable (default `6.0`, works on Server 2020+; set `5.0` for 2019).
- Auth: Basic with empty user + token (works for PATs and `System.AccessToken`). Needs only *Code (read)* and *Pull request threads (read & write)*.

## Exit Codes

| Code | Meaning |
|------|---------|
| 0 | Review completed; no finding at/above `--fail-on` (or no threshold set) |
| 1 | Review completed; at least one finding at/above `--fail-on` |
| 2 | Execution failure: bad args/config, git error, provider error, invalid model output, publish error |

## Tests

xUnit, no live calls (stub `HttpMessageHandler`):

- Diff parsing, annotation, file filtering (deleted/binary/excluded).
- Model-output parsing (fenced/unfenced, garbage → failure).
- Finding validation: invalid path, line outside diff, bad severity/confidence.
- Dedupe and stable fingerprints.
- Severity threshold → exit code.
- Provider error handling (non-2xx, empty choices).
- Azure DevOps thread payload and skip-existing logic.

## Out of Scope

GitHub, multiple providers, web UI, auto-fix/approve/merge, agentic browsing, database/analytics, RAG/indexing, rule engines. Also deferred: retries/backoff, parallel requests, `pullRequestThreadContext` iteration tracking — add when real usage needs them.

## Definition of Done

- Runs locally against a Git diff.
- Validated JSON + readable console report.
- Publishes inline comments to Azure DevOps Server.
- Working Azure Pipelines example.
- Automated tests for core logic and failure cases.
- Concise README: setup, configuration, security, usage.

## Implementation Order

1. CLI, config, git diff retrieval + parsing.
2. Prompt, provider call, finding parse/validation/dedupe.
3. Console report, JSON output, exit codes.
4. Azure DevOps publishing.
5. Tests, pipeline example, README.
