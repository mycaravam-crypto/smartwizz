# Local usage

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
| `--staged` | Review staged Git index changes locally, without a commit or PR. Excludes unstaged/untracked changes. |
| `--head <ref>` | PR head, default `HEAD`. |
| `--since <sha>` | Optional full SHA of the last successfully reviewed commit. Review only changes since it when it is an ancestor of head; otherwise fall back to a full PR review. |
| `--full-review` | Review the complete PR diff even when `--since` is given. Earlier findings are still re-checked. |
| `--state-in <path>` | Previous review checkpoint (requires `--since` and `--state-out`). See [Incremental review](ci-integration.md#incremental-review). |
| `--state-out <path>` | Write the review checkpoint after a fully successful run. |
| `--repo <path>` | Git checkout, default current directory. |
| `--config <path>` | Config file, default `./smartwizz.json` if present. |
| `--verbose` | List individual untracked paths and per-request latency, prompt size, token usage (when supplied), and finding counts. |
| `--plain` | Disable rich Spectre.Console rendering; automatically used for CI, redirected streams, `NO_COLOR`, and `TERM=dumb`. |
| `--json <path>` | Also write the JSON report. |
| `--format <name>` | Console output: `console` (default) or `problems`, one line per finding for editors. See [VS Code](local-usage.md#vs-code). |
| `--fail-on <severity>` | Exit 1 if any finding is at/above `low`/`medium`/`high`/`critical`. |
| `--annotated-diff <path>` | Write the PR unified diff with AI review comments directly after affected added lines; report-only. |
| `--publish` | Post findings as Azure DevOps PR threads. |

**Exit codes:**

| Code | Meaning |
|---|---|
| `0` | Review completed; nothing at/above `--fail-on` (or no threshold) |
| `1` | Review completed; findings at/above `--fail-on` |
| `2` | Execution failure. The message names the kind: `[usage]`, `[configuration]`, `[git]`, `[provider]`, `[timeout]`, `[invalid-model-output]`, `[publish]` |
| `130` | Cancelled (Ctrl+C / SIGTERM). Outstanding requests stop, git is killed, and no partial review is reported. A second signal exits immediately. |

A failure in any request batch fails the whole run; a partial review is never reported as complete.

## Quick local check

[Install the `smartwizz` command](../README.md#install) once. Then, inside any Git repo:

```sh
smartwizz check            # all uncommitted changes to tracked files
smartwizz check --staged   # only what is staged
smartwizz check --plain    # line-based progress and report
smartwizz check --verbose --json review.json # request performance metrics
```

`check` sends those changes to the provider configured in `~/.config/smartwizz/smartwizz.json`. It never reads `smartwizz.json` or `.env` from the repo being reviewed, so a cloned repo cannot redirect your API key. Untracked files are listed but not reviewed; `git add -N <path>` includes them. `--json`, `--annotated-diff` and `--fail-on` work as usual.

## Install as a .NET tool

Follow the [installation commands](../README.md#install) in the root README. On Linux, create the user configuration:

```sh
conf="${XDG_CONFIG_HOME:-$HOME/.config}/smartwizz"
mkdir -p "$conf"
cp -r smartwizz.json rulesets "$conf/"
cp .env.example "$conf/.env"
chmod 600 "$conf/.env"
```

On Windows, copy the same files into `%APPDATA%\smartwizz`. Set the provider key in the protected user environment or .env file. Update the copied ruleset deliberately when upgrading.

Uninstall: `dotnet tool uninstall --global MyCaravam.SmartWizz`. Remove the user configuration if no longer needed; see [local storage](local-storage.md) for metadata deletion.

## VS Code

SmartWizz can show `smartwizz check` findings in the editor, as squiggles and entries in the Problems panel. It uses a VS Code task, so no extension is needed.

**Set it up.** [Install the `smartwizz` command](../README.md#install), then copy the task into the repo you want to review, from that repo's root:

```sh
mkdir -p .vscode && cp /path/to/smartwizz/.vscode/tasks.json .vscode/
```

If the repo already has a `.vscode/tasks.json`, add the `SmartWizz: check` entry to its `tasks` array instead. Open the repo root as your VS Code folder. On Windows with WSL, use a WSL window so the task finds the command.

**Run it.** Choose **Terminal > Run Task > SmartWizz: check**. To put it on a key, open **Preferences: Open Keyboard Shortcuts (JSON)** and add:

```json
{ "key": "ctrl+alt+r", "command": "workbench.action.tasks.runTask", "args": "SmartWizz: check" }
```

**Read the results.** Affected lines get a squiggle, and hovering shows the problem and any suggested fix. `Ctrl+Shift+M` opens the Problems panel, and `F8` jumps to the next finding.

| Finding severity | VS Code level |
|---|---|
| critical, high | Error |
| medium | Warning |
| low | Info |

**How it works.** The task runs `smartwizz check --format problems`, which prints one line per finding:

```
src/Cart.cs:42:error: Total ignores discount (wrong-calculation): Uses the subtotal. Suggested fix: return subtotal - discount; [fp 517d4ce89541b97a]
```

The `problemMatcher` in `tasks.json` splits each line into file, line number, severity and message, and VS Code turns the result into diagnostics. Line numbers match the files in your editor because `check` reviews the working tree. Anything else the command prints, such as the summary or an untracked-files note, stays in the task's terminal.

**Limits:**

- Findings are a snapshot. Re-run the task after editing to refresh them.
- Each finding marks its whole first line, even when it covers several lines.
- The task reviews uncommitted changes. To review only staged changes, add `"--staged"` to `args`.
- Fixes are shown, not applied. A multi-line fix is flattened onto one line, so run `smartwizz check` in a terminal to see it formatted.

## Review locally before committing

Stage the changes you want reviewed with `git add -p`. Then run SmartWizz from its checkout (configured as above):

```sh
dotnet run --project src/SmartWizz -- --repo ../my-repo --staged
```

Only staged changes are sent to the configured AI provider. Unstaged and untracked files are ignored. The review does not change files, create commits, or publish comments. Use `--json` or `--annotated-diff` for optional local reports; `--staged` is intentionally incompatible with PR publication, `--since`, and checkpoint options.