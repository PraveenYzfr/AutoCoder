# Build brief: model pickers in the run dashboard

Add dropdowns to the dashboard so the model used for the next run can be changed without editing
`config/enterprise.yml` and redeploying.

## Scope

**Minimum: three dropdowns — `scout`, `summarize`, `coding`.** Add the rest of the roles too if it
is no extra work, since they come from the same map in `config/enterprise.yml`:

```yaml
role_tiers:
  summarize: cheap      planning: costly
  comment:   cheap      thinking: costly
  scout:     cheap      decision: costly
  coding:    cheap      primary:  costly
```

`planning` is the one that most affects output quality and cost, so include it if you can.

## Where the model list comes from — read this before writing any code

**Do NOT hardcode a model list. Fetch it from each provider's `/models` endpoint at runtime and
cache it briefly (10–15 minutes is plenty).**

This is not a style preference. Hardcoded model names have broken this estate **five times**:

| model | what happened |
|---|---|
| `gemini-2.0-flash` | 404 |
| `gemini-2.5-flash` | "no longer available to new users" — reads exactly like a bad API key |
| `deepseek-chat` | retired 2026-07-24, still in docs |
| `llama-3.1-8b-instant` | retired by Groq within **5 days** of being benchmarked as working |
| `llama-3.3-70b-versatile` | retired in the same window |

A dropdown of stale names is worse than no dropdown: the user picks something, the run fails with a
404, and it looks like the dashboard is broken rather than the model being gone.

**Five providers are wired** (keys already in `.env`):

| provider | list endpoint |
|---|---|
| DeepSeek | `GET https://api.deepseek.com/models` |
| Groq | `GET https://api.groq.com/openai/v1/models` |
| OpenAI | `GET https://api.openai.com/v1/models` |
| Anthropic | `GET https://api.anthropic.com/v1/models` |
| Gemini | `GET https://generativelanguage.googleapis.com/v1beta/models` |

**Filter to chat-capable models.** These lists include things that will not work as an LLM tier —
Groq alone returns Whisper (speech-to-text), Orpheus (text-to-speech), and prompt-guard classifiers
with 512-token context windows. Exclude by capability where the API exposes it, and by a
known-unusable list otherwise. Better to omit a usable model than to offer a broken one.

**Show the provider next to the model** — `deepseek / deepseek-v4-pro`, not just `deepseek-v4-pro`.
Several of these serve models with confusingly similar names, and `openai/gpt-oss-120b` on **Groq**
is a different thing from OpenAI's own models.

**Degrade gracefully.** If a provider's list call fails (bad key, rate limit, outage), show that
provider's group as unavailable with the reason — do not fail the whole dropdown, and do not fall
back to a hardcoded list.

## Where the selection is stored

**Do not write to `config/enterprise.yml`.** It is bind-mounted read-only from the host, it is the
committed default, and a browser must not be able to rewrite it.

Write overrides to a separate file in the runs root instead:

```
{AUTOCODER_CONTAINER_WORKSPACE_ROOT}/model-overrides.json
```

```json
{
  "updatedAt": "2026-08-22T18:00:00Z",
  "roles": {
    "scout":     { "provider": "deepseek", "model": "deepseek-v4-flash" },
    "coding":    { "provider": "groq",     "model": "openai/gpt-oss-120b" }
  }
}
```

Rules:

- Read it when a run **starts**, not per LLM call — a run must use one consistent set of models
  from beginning to end, even if someone changes a dropdown mid-run
- A role absent from the file falls back to `config/enterprise.yml` — the config stays the default,
  overrides are the exception
- Provide a **Reset to config** control per role and one for all roles, which removes the entry
- Never lose the file on container recreate. `{CONTAINER_WORKSPACE_ROOT}` is bind-mounted, so this
  works — but do not put it anywhere else

## Show the effect before it is applied

Each dropdown must display **what is in effect right now** and where it came from:

```
scout      deepseek / deepseek-v4-flash    (from config)
coding     groq / openai/gpt-oss-120b      (overridden — Reset)
```

Also show, near the pickers:

- **"Applies to the next run"** — stated plainly. This does not affect a run in progress.
- The **daily call budget** (`AUTOCODER_LLM_DAILY_CALL_BUDGET`) and calls used today, so the cost
  consequence of switching a hot role to an expensive model is visible at the moment of choosing.

## Security — this changes what the dashboard is

The original brief specified read-only: *"a window, not a control panel"*. This deliberately breaks
that, so bound it tightly:

- The write endpoint (`POST`/`PUT /api/ui/model-overrides` or similar) must sit behind the **same**
  auth as the rest of the dashboard — Cloudflare Access or `AUTOCODER_UI_TOKEN`. Never
  unauthenticated.
- **`/health` and `POST /webhook/jira` stay unauthenticated and unchanged.** A liveness probe
  carries no credentials and Jira cannot log in.
- **Only these role→model mappings are writable.** No editing prompts, allowlists, repo URLs, Jira
  settings, budgets or sandbox limits from the browser.
- Validate on write: the provider must be one of the five wired, and the model must be present in
  that provider's current `/models` response. Reject anything else with a clear error rather than
  storing a value that will 404 at run time.
- Log every override change to the run log — who (Access email if present), when, which role, from
  what to what.

## Definition of done

- Dropdowns for at least `scout`, `summarize`, `coding` — ideally all eight roles
- Model lists fetched live from all five providers, grouped by provider, non-chat models filtered out
- A provider whose list call fails shows as unavailable without breaking the others
- Selection persists across container recreate
- Each role shows its effective model and whether it is overridden, with a per-role Reset
- A run started after a change uses the new model; a run already in flight does not
- Write endpoint refuses unauthenticated requests; `/health` and the webhook still work
- Invalid provider/model combinations are rejected on write, not at run time

## Notes

Currently deployed and working, for context:

```
cheap   deepseek / deepseek-v4-flash
costly  deepseek / deepseek-v4-pro     (was anthropic/claude-sonnet-5 — rejected `temperature`)
coding  deepseek / deepseek-v4-flash
```

The operator's cost strategy is **DeepSeek first, then Groq**, with Anthropic, OpenAI and Gemini
reserved for deliberate benchmarking rather than routine runs. The dropdowns should make that easy
to follow and easy to depart from on purpose — not easy to depart from by accident. If you can order
or badge options by cost, do.

Written by Claude (SeekandDestroy / deployment owner). Hub plan:
`D:\Praveen\Projects\_multi-system-hub\SHARED_PLAN.md`.
