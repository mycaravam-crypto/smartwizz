# Security and trust boundaries

## Security

- PR content is untrusted. The diff is wrapped in random-nonce markers, and the prompt tells the model to ignore instructions inside it. Every finding is validated against the diff.
- Normal review sends the prompt, applicable ruleset instructions and diff to the model. Environment values and authentication tokens are not included in model messages; source code itself may contain sensitive data. Use a provider approved for the reviewed code.
- Optional local history and cache store metadata in two unencrypted SQLite databases outside the checkout, not source, paths, prompts, messages or fixes. HMAC identities are not encryption. The key is separate (Windows current-user DPAPI or `SMARTWIZZ_HISTORY_KEY`); protect the local account and storage directory. An existing key can enable local storage under current defaults; `--stateless` disables it and CI does not use it.
- Explicit `--revalidate` may send bounded visible source excerpts associated with historical findings (including diff context) and opaque finding IDs to the configured provider. Verdicts are advisory; local history does not suppress findings or determine CI checkpoint state.
- Git runs with `--no-ext-diff --no-textconv`, so repository config can't execute commands. The Azure DevOps PR lookup can also invoke the installed ado CLI.
- **Keep `smartwizz.json` out of the reviewed repository in CI.** A PR that can edit it can point `provider.endpoint` at its own server and receive `SMARTWIZZ_API_KEY`.
- Published comments are model-generated text and are labelled as AI output. Treat them like any other untrusted review comment.
- Tokens are sent only in request headers and never logged.

See [CI integration](ci-integration.md) for token permissions and publication boundaries, and [local storage](local-storage.md) for retention and key handling. Suggested fixes are untrusted, including textual remediation advice. Disabling replacement-code output does not validate advice.
