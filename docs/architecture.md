# Architecture and product principles

## Goal

A small, reliable AI-powered Pull Request reviewer that reports actionable bugs, security issues, and missing tests.

SmartWizz is standalone and separate from GitWizz (merge ordering / merge readiness).

## Principles

- **YAGNI:** implement only what demonstrated product needs require.
- Simple, deterministic code over abstractions. No interfaces with one implementation.
- Review logic independent of CI platform and AI provider (provider and publisher are plain classes taking an `HttpClient`).
- All PR content is untrusted input.
- Never auto-fix, approve, or merge.
- No web UI, server-side database, agent framework, plugin system or custom static-analysis engine.
- Prefer the BCL. Runtime dependencies require a demonstrated benefit, security review and explicit justification.
- One configured ruleset per engineering guide leads semantic review. Do not add defect-specific application branches or duplicate compiler/linter checks.
- Measure actionable detection quality and fresh-review latency before extending supporting features.

## Decisions since MVP

- Bounded independent request concurrency (default one) was added for large diffs. Keep the default until representative latency and detection-quality benchmarks justify changing it.
- Spectre.Console provides console rendering; SQLite and its native bundle provide local metadata-only history/cache; ProtectedData protects the local Windows key. These are narrow exceptions to the original dependency-free MVP, not permission for an analytics platform or additional persistence features.
- CI checkpoints support PR continuity and publication state. Local history is advisory, checkout-scoped memory; it must not suppress fresh findings or drive CI lifecycle decisions. Cache hits are historical results, not fresh reviews.
- Freeze additional lifecycle, storage and language-specific analysis features until the existing review is benchmarked. Retain legacy taxonomy only for compatibility pending a measured migration; add no new hardcoded defect patterns.


## Runtime architecture

## Technology

- C# / .NET 10 (current LTS), single console project.
- Git CLI for diffs, invoked with `--no-ext-diff --no-textconv` so repo config can't run commands.
- One provider: OpenAI-compatible `POST /chat/completions` over raw `HttpClient`. No SDK.
- `response_format: json_schema` only when `provider.jsonSchema` is on (default off: support varies across local/hosted servers). Without it the prompt demands JSON and the parser tolerates code fences; either way the parser validates every finding.
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
prompts/review-format.md        shared system prompt: role, untrusted-data rules, output format (embedded resource)
prompts/code-review.md          general review task, sent after the diff
prompts/standards-review.md     follow-up (standards-only) task, sent after the diff
tests/SmartWizz.Tests/
pipelines/azure-pipelines.yml
smartwizz.example.json
README.md
```

`schemas/review-result.schema.json` is dropped: validation lives in code (no JSON-schema dependency), and the output shape is documented in the README.


The shared review engine handles both local and CI reviews. Git diff retrieval, rule selection, bounded batching, model requests, validation, reporting and optional publishing remain separate concerns. Local storage is advisory; CI checkpoints preserve PR continuity and are not a local cache. See [review output](review-output.md), [CI integration](ci-integration.md) and [local storage](local-storage.md).

## Out of Scope

Provider-specific review logic, web UI, auto-fix/approve/merge, agentic browsing, centralized database/analytics, RAG/indexing and custom rule engines remain out of scope. Local metadata storage and bounded concurrency are the limited exceptions documented above. Defer further expansion until measured review quality or developer effort justifies it.


## Delivery and verification

See [development](development.md) and [benchmarks](../benchmarks/README.md). Historical MVP implementation order is superseded by the current code and issue backlog.
