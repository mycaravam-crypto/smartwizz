# SmartWizz

Small, lean AI pull request reviews. SmartWizz reads a PR diff from a local Git checkout, asks one OpenAI-compatible model for **bugs, security issues, and missing tests**, validates what comes back, and prints a report — optionally posting inline threads to Azure DevOps Server.

It never runs PR code, never fixes, approves, or merges anything, and is report-only by default.

## Requirements

- .NET 10 SDK
- `git`
- An OpenAI-compatible chat-completions endpoint (OpenAI, Azure OpenAI-compatible gateways, Ollama, vLLM, LM Studio, ...)

## Usage

```sh
cp .env.example .env                       # put SMARTWIZZ_API_KEY in it (git-ignored)
# smartwizz.json is preconfigured for Gemini (Google AI Studio key); see smartwizz.example.json for others

dotnet run --project src/SmartWizz -- --repo ../my-repo --base origin/main
```

| Option | Meaning |
|---|---|
| `--base <ref>` | Branch/commit the PR merges into (required). Diff is `base...head` (from the merge-base). |
| `--head <ref>` | PR head, default `HEAD`. |
| `--repo <path>` | Git checkout, default current directory. |
| `--config <path>` | Config file, default `./smartwizz.json` if present. |
| `--json <path>` | Also write the JSON report. |
| `--fail-on <severity>` | Exit 1 if any finding is at/above `low`/`medium`/`high`/`critical`. |
| `--publish` | Post findings as Azure DevOps PR threads. |

**Exit codes:** `0` review done (nothing at/above threshold) · `1` findings at/above `--fail-on` · `2` execution failure (bad args/config, git, provider, invalid model output, publishing).

## Configuration

See [`smartwizz.example.json`](smartwizz.example.json). Notable settings:

- `review.contextLines` — unchanged lines shown around each change (default 10).
- `review.maxCharsPerRequest` — files are packed into requests up to this size; a single larger file is skipped and listed, never truncated.
- `review.minConfidence` — findings below this are dropped (default 0.5).
- `review.exclude` — glob patterns matched against path and file name; replaces the built-in list (lockfiles, minified, generated, migrations).
- `provider.temperature` — `null` to omit for models that reject it.

Secrets are read only from the environment. For local runs you can put them in a git-ignored `./.env` (see [`.env.example`](.env.example)). Variables that are already set take precedence.

| Variable | Used for |
|---|---|
| `SMARTWIZZ_API_KEY` | Provider bearer token |
| `SMARTWIZZ_AZDO_TOKEN` or `SYSTEM_ACCESSTOKEN` | Azure DevOps PAT / pipeline token |

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

`endLine` appears when a finding spans lines. Findings from the model are rejected (and listed) if the file isn't in the diff, the lines aren't visible new-side lines, or a field is missing/invalid. Unparseable model output fails the run with exit 2.

The `fingerprint` hashes rule, path, and the *content* of the start line, so it survives unrelated line shifts. It is used to dedupe within a run and to avoid re-posting the same thread on reruns.

## Azure DevOps Server

`--publish` posts one active thread per new finding on the PR's right-hand (new) side. Threads already carrying a finding's fingerprint are skipped.

Connection settings come from `azureDevOps.*` in the config, falling back to the pipeline variables `System.CollectionUri`, `System.TeamProject`, `Build.Repository.ID`, and `System.PullRequest.PullRequestId`. `apiVersion` defaults to `6.0` (Server 2020+); use `5.0` for Server 2019. The token needs only code read and PR thread read/write.

See [`pipelines/azure-pipelines.yml`](pipelines/azure-pipelines.yml) for a branch-policy build. It reviews `origin/<target>...HEAD^2`, the PR source tip, so line numbers match the PR view.

## Security

- PR content is untrusted. The diff is wrapped in random-nonce markers, and the prompt tells the model to ignore instructions inside it. Every finding is validated against the diff.
- Only the prompt and diff go to the model. No environment, config, or credentials are sent.
- Git runs with `--no-ext-diff --no-textconv`, so repository config can't execute commands. No other process is started.
- **Keep `smartwizz.json` out of the reviewed repository in CI.** A PR that can edit it can point `provider.endpoint` at its own server and receive `SMARTWIZZ_API_KEY`.
- Published comments are model-generated text and are labelled as AI output. Treat them like any other untrusted review comment.
- Tokens are sent only in request headers and never logged.

## Development

```sh
dotnet test
```

Tests stub all HTTP. They make no live model or Azure DevOps calls.
