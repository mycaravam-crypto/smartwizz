# CI and pull request integration

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

On each run, earlier unresolved findings that were not reported again are re-checked if their file changed (after a complete review, all of them are). The re-check sends the model the finding and the current code around it, using a separate prompt (`prompts/revalidate.md`). Only an explicit, explained `resolved` verdict resolves a finding. Missing, contradictory or unknown verdicts keep it open, and malformed output fails the run. Findings in untouched files are carried forward without a model call. Changes that only arrived by merging the base into the PR are not reviewed.

SmartWizz runs a complete review instead of an incremental one when:

- `--full-review` is given (for example a security or release review),
- the checkpoint is missing, corrupt or from an older version,
- the review configuration, prompts or ruleset changed,
- the checkpoint SHA does not match `--since`,
- `--since` is not in the clone or not an ancestor of the head (force-push or rebase).

Earlier unresolved findings are still re-checked after a complete review, so none are silently dropped. The checkpoint is written only as the last step of a fully successful run. A failed review, re-check or publish never advances the baseline. The console report and the JSON report (`mode`, `modeReason`, `trackedFindings`) show which mode ran and why.

The reusable GitHub Actions workflow stores the checkpoint as a per-PR artifact and restores the newest one. It uses only checkpoints that this repository's own runs stored for the PR's branch, where the storing run reviewed exactly the commit the checkpoint names. Fork PRs therefore always get complete reviews. If checkpoints cannot be read (for example because the caller does not grant `actions: read`), the workflow warns and runs a complete review. Runs for the same PR are serialized by a concurrency group, and a queued run is replaced by a newer push. Set the `full-review` input to force a complete review.

## Azure DevOps Server

`--publish` posts one active thread per new finding on the PR's right-hand (new) side. Threads already carrying a finding's fingerprint are skipped.

Connection settings come from `azureDevOps.*` in the config, falling back to the pipeline variables `System.CollectionUri`, `System.TeamProject`, `Build.Repository.ID`, and `System.PullRequest.PullRequestId`. `apiVersion` defaults to `6.0` (Server 2020+); use `5.0` for Server 2019. `timeoutSeconds` (default 60) bounds each request. The token needs only code read and PR thread read/write.

Deduplication fails closed. The existing threads are read before anything is posted, and if that lookup fails, times out, or returns an unexpected shape, nothing is posted. The threads List API documents no paging in any version (it returns all threads). If a server paginates anyway (`x-ms-continuationtoken`), or `count` doesn't match the threads received, SmartWizz can't prove the list is complete, so it refuses to publish rather than risk duplicates. If a POST fails partway, the threads already posted are recognised on the next run.

See [`pipelines/azure-pipelines.yml`](../pipelines/azure-pipelines.yml) for a branch-policy build. It reviews `origin/<target>...HEAD^2`, the PR source tip, so line numbers match the PR view.

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
- Use `on: pull_request`, **never** `pull_request_target`. PRs from forks then get no secrets, so they can't extract the key; the example skips fork PRs.
- Anyone who can push a branch to the repository can change the workflow file, so they are trusted with the secret either way. That is the same boundary as any CI secret.