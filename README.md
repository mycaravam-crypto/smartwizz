# SmartWizz

A lean AI-powered code reviewer for developers and CI pipelines. SmartWizz reviews Git changes against a configurable engineering standard, reports actionable bugs, security issues and missing tests, and validates model findings before reporting or publishing them.

One shared review engine supports local checks, GitHub Actions and Azure DevOps. It never executes reviewed code, applies fixes, approves or merges PRs. Publication is explicit.

## Requirements

- .NET 10 SDK (see [global.json](global.json)) and Git.
- An authorized OpenAI-compatible chat-completions endpoint and credentials if required.
- The .NET global tool directory on PATH.

## Install

Build from a trusted SmartWizz checkout:

```sh
VERSION=0.1.$(git rev-list --count HEAD)
dotnet pack src/SmartWizz -c Release -p:Version="$VERSION" -o ./nupkg
dotnet tool update --global MyCaravam.SmartWizz --source ./nupkg --version "$VERSION"
```

Create a user configuration directory: `~/.config/smartwizz` (or `$XDG_CONFIG_HOME/smartwizz`) on Linux, `%APPDATA%\smartwizz` on Windows. Copy [smartwizz.json](smartwizz.json) and the [rulesets](rulesets/) directory there. Configure the provider and set `SMARTWIZZ_API_KEY` in your environment or protected user .env file. See [local setup](docs/local-usage.md) and [configuration](docs/configuration.md).

To update, pull the desired revision and repeat the install commands; update the copied ruleset separately when appropriate.

## Local review

From your application repository:

```sh
smartwizz check
smartwizz check --staged
smartwizz check --stateless --verbose --json review.json
```

The first command reviews uncommitted tracked changes; the second reviews staged changes only. Untracked files are not reviewed unless added to the index (intent-to-add is sufficient for working-tree review). `--stateless` disables optional local history and cache.

Use `--format problems` for editor integration. See [local usage and VS Code](docs/local-usage.md).

## CI review

Review a committed range using trusted configuration:

```sh
smartwizz --base origin/main --head HEAD --config /path/to/trusted/smartwizz.json --json review.json --fail-on high
```

### GitHub Actions

```yaml
name: SmartWizz
on: pull_request
jobs:
  smartwizz:
    if: github.event.pull_request.head.repo.full_name == github.repository
    permissions:
      contents: read
      pull-requests: write
      actions: read
    uses: mycaravam-crypto/smartwizz/.github/workflows/review.yml@main
    with:
      fail-on: high
    secrets:
      SMARTWIZZ_API_KEY: ${{ secrets.SMARTWIZZ_API_KEY }}
```

Pin the workflow and matching `smartwizz-ref` to a trusted tag or commit for controlled upgrades. The reusable workflow supports inline comments, report artifacts and incremental PR checkpoints. Never use `pull_request_target` for reviewing untrusted changes.

### Azure DevOps

Use the [pipeline example](pipelines/azure-pipelines.yml) or review a PR locally with the authenticated [ado-devops](https://github.com/mycaravam-crypto/ado-devops) CLI:

```sh
smartwizz --pr 142 --json review.json
```

Add `--publish` only when publication is intended and credentials are configured. See [CI integration](docs/ci-integration.md) for permissions, checkpoints and deduplication.

### Quality gates

| Exit code | Meaning |
|---|---|
| 0 | Completed; no finding at or above the configured threshold |
| 1 | Completed; threshold exceeded or met |
| 2 | Execution failure |
| 130 | Cancelled |

A failed request does not produce a completed partial review. Rejected findings and skipped files are reported; a passing gate is not proof of full detection or compliance.

## Engineering standard and configuration

[ENGINEERING_STANDARD.md](ENGINEERING_STANDARD.md) is generated from the canonical [rule catalog](rules/engineering-rules.json). Semantic review uses one configured [ruleset](rulesets/README.md); mechanical rules belong in compiler, analyzer and linter checks.

Start with [smartwizz.example.json](smartwizz.example.json). Keep request concurrency at one until representative measurements justify increasing it. Pass size and suggested fixes are configurable; see [configuration](docs/configuration.md) and [benchmarks](benchmarks/README.md).

## Security

Use an approved provider: source and applicable review instructions are transmitted to it. Reviewed code is untrusted, and generated findings and fixes require human judgment. Keep CI configuration and rulesets in a trusted checkout, never the untrusted PR. Local metadata storage is disabled in CI; its databases are not encrypted. See [security](docs/security.md) and [local storage](docs/local-storage.md).

## Documentation

| Topic | Reference |
|---|---|
| Architecture, principles and scope | [Architecture](docs/architecture.md) |
| CLI, installation and editor integration | [Local usage](docs/local-usage.md) |
| GitHub, Azure DevOps, checkpoints and gates | [CI integration](docs/ci-integration.md) |
| Provider, ruleset and performance settings | [Configuration](docs/configuration.md) |
| Reports, finding validation and coverage | [Review output](docs/review-output.md) |
| Trust boundaries and credentials | [Security](docs/security.md) |
| Optional history, cache and lifecycle | [Local storage](docs/local-storage.md) |
| Build, tests and Definition of Done | [Development](docs/development.md) |
| Evaluation and measured decisions | [Benchmarks](benchmarks/README.md) |

## Development

```sh
python3 tools/engineering_rules.py --check
python3 tools/ruleset.py check --catalog rules/engineering-rules.json
dotnet format --verify-no-changes
dotnet build -c Release
dotnet test -c Release
```

See [development](docs/development.md) for verification expectations. Open work is tracked in [GitHub issues](https://github.com/mycaravam-crypto/smartwizz/issues).
