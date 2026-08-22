# Build brief: AutoCoder run dashboard

Build a web UI for AutoCoder showing current run progress, run history, and per-run detail
including Jira state. Today `GET /` returns an empty 404 — there is no UI at all, and the only way
to see what a run did is `docker logs` or reading files on the VM.

## What already exists — build on this, do not reinvent it

**Server:** `src/AutoCoder.Server/Program.cs`, ASP.NET Core minimal API, listens on **8081**.
Two routes today:

```
GET  /health          -> JSON status (config path, triggers mode, llm routing, sandbox)
POST /webhook/jira    -> Jira webhook receiver
```

**Runs are already persisted to disk.** `AutoCoder.Core/Logging/RunLog.cs` writes structured JSONL
to `runs/{runId}/run.log`, one event per line, with at least:

```json
{"ts":"...","event":"...","runId":"...","ticket":"PROJ-123","pipeline":"...","dryRun":false}
```

plus per-event custom fields. Individual steps also write human-readable artefacts into the same
run directory:

| file | written by |
|---|---|
| `run.log` | `RunLog` — JSONL event stream, the primary data source |
| `plan.md` | `FixBugSteps` — the plan the model produced |
| `scout.md` | `ScoutRepoStep` — repo reconnaissance |
| `ticket-brief.md` | `ScoutRepoStep` — the ticket as the agent understood it |

**Run root:** `/app/runs` inside the container (`AUTOCODER_CONTAINER_WORKSPACE_ROOT`), bind-mounted
from `/var/lib/autocoder/runs` on the host. Read from the container path.

**Pipeline stages**, in order — these are the progress steps to render:

```
FetchTicket -> Plan -> Approval -> ProvisionSandbox -> ScoutRepo
            -> AgenticImplement -> Build -> Test -> SecretScan
            -> PR -> WritebackTicket -> PersistRunResult
```

**Also available:** `RunSpend` (`AutoCoder.Abstractions/Pipeline/IPipeline.cs`) for LLM cost,
`RunConcurrency` and `TicketRunLease` (`AutoCoder.Core/Runs/`) for what is currently executing, and
the routed LLM config surfaced by `/health` (`cheap`, `costly`, `coding`).

**Jira polls every 300 seconds** for `status = "AssignedToAgent"`, in addition to the webhook.

## What to build

**A JSON API plus a static page. No SPA framework, no build step.** The server already serves
minimal-API endpoints; add a few more and serve static files from `wwwroot`. Plain HTML + CSS +
vanilla JS is enough and keeps the Docker image unchanged.

### Endpoints

```
GET /api/runs                 list, newest first, paginated
GET /api/runs/{runId}         full detail for one run
GET /api/runs/{runId}/log     raw JSONL events (optionally ?since= for polling)
GET /api/runs/current         whatever is executing right now, or null
```

### Pages

**1. Run list (`/`)** — the landing page. One row per run:

- Run id, Jira ticket key, pipeline name
- **Status**: running / succeeded / failed / dry-run
- Started, duration
- Final stage reached — and for failures, **which stage failed**
- PR link if one was opened
- LLM spend if available

Newest first. Filter by status and by ticket key. List rows also show **coding progress**
(files written / tool calls / finished?) and a one-line **model mix** (e.g. “plan: Claude ·
code: DeepSeek”) when `llm.call` events exist.

**2. Run detail (`/runs/{id}`)** — the important page:

- **Stage progress**: all pipeline stages listed, each marked done / running / failed / skipped, with
  per-stage duration. This is the thing a person actually wants — *where is it, and where did it
  stop*.
- **Coding progress** (AgenticImplement): turns used vs cap, tool-call count vs `max_tool_calls`,
  files written, whether `finish` was called, and a short list of `write_file` paths. A run that is
  still implementing should show “turn 7 / 40, 3 files written, not finished yet” — not only a
  spinning stage name.
- **Models in use**: the configured routing (cheap / costly / coding) plus **what this run actually
  called**. Group `llm.call` events by role:
  - `scout` / `summarize` / `comment` → cheap (DeepSeek Flash today)
  - `planning` / `thinking` / `decision` → costly (Claude Sonnet today)
  - `coding` → cheap coding loop (DeepSeek Flash unless `AUTOCODER_CODING_TIER=costly`)
  Show provider, model, call count, tokens, and estimated USD per role. A person must be able to
  answer “which model wrote the plan vs which model edited the files” without reading logs.
- **Jira**: ticket key as a link to the real Jira issue, summary, current status, and what AutoCoder
  wrote back during `WritebackTicket`.
- **Artefacts**: render `plan.md`, `scout.md` and `ticket-brief.md` as markdown, collapsed by
  default.
- **Event log**: the `run.log` stream, filterable by level, newest last.
- **Outcome**: PR URL, branch name, files changed, secret-scan result, build and test output.

**3. Live updates.** While a run is active, poll `/api/runs/current` every 2–3 seconds and update
the stage list in place. Do not use SSE or WebSockets unless it is genuinely simpler — a run takes
minutes, and polling is fine at this scale.

## Constraints

**Do not break `/health` or `POST /webhook/jira`.** `/health` is used as a liveness probe and the
webhook is Jira's entry point. Keep both paths and their response shapes exactly as they are.

**The UI must be read-only.** No triggering runs, no cancelling, no editing config from the browser.
This is a window, not a control panel. Anything that mutates state is a separate decision.

**Read the run directory, do not add a database.** `AutoCoder` exists as a SQL database name but the
app is file-based today, and the run directory already has everything needed. Do not introduce EF
Core or a schema for this.

**Handle a partially-written run.** A run in progress has an incomplete `run.log` and may be missing
`plan.md` entirely. Parse defensively — a half-finished run is the normal case for the page that
matters most, not an error.

**No new runtime dependencies if avoidable.** The image is `mcr.microsoft.com/dotnet/aspnet:8.0`.
A markdown renderer is acceptable; a Node build pipeline is not.

## Security — read this before exposing anything

**This service is already public** at `https://autocoder.praveenyzfr.com` via a Cloudflare tunnel,
and **`/health` is unauthenticated**. Right now that is acceptable because `/` returns 404 and
`/health` leaks little.

**A run dashboard changes that.** It would expose Jira ticket contents, repository names, branch
names, generated plans, code diffs, build output and LLM spend — to anyone who finds the URL.

**So: the UI must not be publicly readable.** Two acceptable options:

1. **Cloudflare Access in front of the hostname** (preferred — no app changes). Email-gated, free,
   and Claude configures it. If you choose this, say so and change nothing in the app.
2. **Application auth** on the UI and `/api/*` routes. If you build this, `/health` and
   `POST /webhook/jira` must stay unauthenticated — a liveness probe never carries credentials and
   Jira cannot log in.

**Never** put the dashboard on an unauthenticated public route. Confirm which option you have taken
in your handoff note.

**Auth decision (AutoCoder agent, 2026-08-22):** both. `/health` and `POST /webhook/jira` stay
open. `/`, `/runs/*`, and `/api/runs*` / `/api/ui/*` require **either** Cloudflare Access
(`Cf-Access-Authenticated-User-Email`) **or** `AUTOCODER_UI_TOKEN` (header, cookie, or `?token=`).
If neither is present the dashboard returns 401 and does not list runs. Claude: set the env var on
the VM and/or put Access on `autocoder.praveenyzfr.com`.

## Definition of done

- `/health` and `POST /webhook/jira` unchanged, verified
- Run list renders with real runs from `/app/runs`
- Run detail shows all twelve stages with correct state, including a failed run stopping at the
  right stage
- A run in progress updates live without a manual refresh
- A half-written run renders without throwing
- Works on a phone — this gets checked from a mobile more often than not
- Auth decision made and stated
- Coding progress visible on list + detail (turns, tools, files, finish)
- Per-role model usage visible (which model did scout / plan / code / summarize)

## Notes

Deployed behind a Cloudflare tunnel that routes the hostname to `http://autocoder:8081` — so the app
sees plain HTTP and must not assume HTTPS or redirect to it. Cloudflare terminates TLS.

Written by Claude (SeekandDestroy / deployment owner) at Praveen's request. Hub plan:
`D:\Praveen\Projects\_multi-system-hub\SHARED_PLAN.md`.
