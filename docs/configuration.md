# Configuration and tuning

## Configuration

See [`smartwizz.example.json`](../smartwizz.example.json). Notable settings:

- `review.contextLines` — unchanged lines shown around each change (default 10).
- `review.maxCharsPerRequest` — files are packed into requests up to this size; a single larger file is skipped and listed, never truncated.
- `review.minConfidence` — findings below this are dropped (default 0.5). The prompt also tells the model this floor, so it doesn't write out findings that would be dropped.
- `review.suggestFixes` — `false` stops asking the model for `suggestedFix`, the longest output field (default `true`).
- `review.exclude` — glob patterns matched against path and file name; replaces the built-in list (lockfiles, minified, generated, migrations).
- `provider.temperature` — `null` to omit for models that reject it.
- `provider.maxRetries` (default 2) and `provider.fallbackModels` (default none) — see *Overload handling* below.
- `provider.jsonSchema` — `true` constrains every reply to the finding format with `response_format: json_schema` (strict), to constrain the response shape (validation is still required). Default `false`, because server support varies: llama.cpp server, vLLM, Ollama 0.5+, LM Studio, OpenAI and Gemini accept it on the chat-completions endpoint. If the server answers 400, the run fails with a `[configuration]` error naming this setting; it never silently retries without the schema. Replies are still fully validated (paths, lines, rule IDs, confidence).
- `review.ruleset` — path (relative to the config file) to the [ruleset](../rulesets/README.md) of the coding guide to check compliance with (default `rulesets/engineering-standard.json`). One file per guide: point this at another file to review against another guide. Only checks whose triggers match the added lines are sent; findings may only use the IDs of checks sent in that request. The report records `rulesetName`, `rulesetVersion` and `rulesetSha256`. Keep the file in the trusted SmartWizz checkout in CI, never the untrusted PR.
- `review.maxChecksPerRequest` — `0` (default) sends all applicable checks with the review. A positive number sends at most that many per request, highest severity first; the rest go to extra standards-only requests over the same diff (or, when their checks apply to well under half of it, only the files they apply to). All selected checks are assigned to passes; this is not proof of detection. `requests` in the JSON report counts the calls.
- `review.guidelines` — legacy alternative to `ruleset`: a Markdown guide pasted whole into the prompt. Only sensible for large models; not combinable with `ruleset`.

Secrets are read only from the environment. For local runs you can put them in a git-ignored `./.env` (see [`.env.example`](../.env.example)). Variables that are already set take precedence.

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

## Small local models

SmartWizz is built to work with local models below ~30B parameters. Start from [`smartwizz.local.example.json`](../smartwizz.local.example.json): copy it to `smartwizz.json` and set your model.

What each piece does for a small model:

- **The ruleset** sends only checks whose triggers match the change, as short instructions with a Bad/Good example. A small change usually gets 0–3 checks instead of the whole guide.
- **`maxChecksPerRequest`** keeps each prompt to a few checks. Larger changes get extra standards-only requests rather than one long list the model half-follows. A batch takes `applicable checks ÷ 8` requests, rounded up (at most 4 with the default ruleset).
- **`maxCharsPerRequest` / `contextLines`** keep the diff small enough for the context window. One request is roughly the system prompt (~1.7k characters), the diff batch, and the task (~1k characters plus ~450 per check).
- **Prompt layout for prefix caching:** every request of a batch starts with the same system prompt and the same diff block; the pass-specific task and its checks come last, after the diff's end marker. Servers that reuse a matching prompt prefix (llama.cpp's prompt cache, vLLM's automatic prefix caching, Ollama) then process the diff once per batch rather than once per pass. Keep prompt caching enabled on the server.
- **Follow-up passes** send only the files their checks apply to when that is under half of the batch; otherwise they resend the full diff, which may benefit from server-side prefix caching.
- **Context window:** make sure the server gives the model enough context, or it silently drops the start of the prompt. With the example values, allow at least 8k tokens. Ollama is configured by `OLLAMA_CONTEXT_LENGTH` or the model's `num_ctx`; the chat-completions API cannot set it.
- **Output:** the model must answer with JSON. The example enables `provider.jsonSchema`, which makes the server constrain output to the finding schema (llama.cpp server, vLLM, Ollama 0.5+ and LM Studio support it); follow-up passes may then only produce the check IDs they were sent. On a server without support, set it to `false`. If a model still often fails (`[invalid-model-output]`), try a larger or code-tuned model before loosening anything else.
- **Lean output:** generating tokens is usually the slowest step on local hardware, so the example sets `suggestFixes: false`, and the model is told the `minConfidence` floor rather than writing out findings that get dropped.

Measure on your own model with the [benchmark](../benchmarks/README.md) before relying on it. `tools/ruleset.py preview` shows what a diff will send.

## Large PR performance

Set `review.maxParallelRequests` to an integer from 1 to 8 (default 1). Independent file batches may overlap, while passes within a batch remain sequential. Keep the default 1 until measured; compare 1, 2, 4 and 8 on the same immutable PR diff. Use `--verbose --json review.json` to capture timings, attempts, reasoning tokens when returned by the provider, and effective output throughput. Cumulative request time can exceed wall time with concurrency. Compare finding recall, severity and fingerprints as well as latency; do not increase concurrency if timeout or retry rates rise. Use separate report files outside the reviewed checkout. Existing contextLines, maxCharsPerRequest, maxChecksPerRequest and suggestFixes settings can be tuned independently. Changing review configuration may invalidate an existing incremental checkpoint scope.

## Measured starting profile

For the tested nvidia-nemotron-30b model and the 16-defect fixture, single-pass with `suggestFixes: false` is the provisional preferred profile. Keep `maxParallelRequests: 1` until independent-batch benchmarks justify changing it. See [benchmark methodology](../benchmarks/README.md) and [issue #39](https://github.com/mycaravam-crypto/smartwizz/issues/39). This is not a universal default or a guarantee of improved latency.
