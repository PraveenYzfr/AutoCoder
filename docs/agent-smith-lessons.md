# Lessons from Agent Smith

Source studied: [holgerleichsenring/agent-smith](https://github.com/holgerleichsenring/agent-smith) (MIT) and [docs.agent-smith.org](https://docs.agent-smith.org).

This document maps what we learned, then what AutoCoder **adopts**, **adapts**, or **avoids**. We re-implement patterns; we do not copy large skill packs, branding, or proprietary-feeling prompt corpora.

## Reverse-engineering map

### Trigger model

| Mode | Agent Smith | Notes |
|------|-------------|-------|
| Webhook | `POST /webhook` (+ platform routes) | HMAC/token verify; filter by status, label, comment keyword |
| Poll | Interval + jitter; leader lease | Fallback when no public URL |
| Label | `pipeline_from_label` / global `pipeline_triggers` | Lifecycle labels must not re-trigger |
| Assignee | Jira-specific (`assignee_name`) | Optional niche trigger |
| CLI | One-shot `--ticket` / `--project` | Best for demos and ops |

**Claim:** DB lease so webhook∩poll cannot double-run the same ticket.

### Ticket → project / repo

- Catalog: `agents`, `trackers`, `repos` → `projects` references by name.
- Resolution strategies: tag, area_path (ADO), repo URL, to_address.
- Ambiguous matches can fan out; `ScopeRepos` narrows sandboxes before provision.
- Per-repo bootstrap via `.agentsmith/context.yaml` (`init-project`).

### Sandbox / toolchain

- One sandbox per affected repo; stock images (`dotnet/sdk`, `node:20`, `python:3.12`).
- Sandbox-agent injected (init/emptyDir); toolchain image stays unmodified.
- Multi-repo: path-prefix routing (`repoKey/...` → sandbox).
- Server ↔ agent over Redis streams (not kubectl exec).

### Plan → implement → PR → writeback

Operator story: fetch → scope → clone → plan/spec → approval → agentic code+test → secret-scan → commit → **one PR per repo** → ticket done + PR URLs.

- Success requires real code change + green verification (regression-aware).
- Red verify → draft PR, not silent success.
- **No auto-merge** — product ends at PR + ticket comment.
- Artifacts: `plan.md`, `result.md`, `decisions.md` under run id; cost/token in result.

### Skills / pipelines

- Pipeline = ordered commands + handlers sharing `PipelineContext`.
- Coding pipelines collapsed toward a single `code` pipeline (aliases: fix-bug, add-feature, …).
- Orchestration types: hierarchical / structured / discussion — powerful, heavy.
- Skills: separate catalog, embedded in releases; fail fast if none match.

### Config shape

- Catalog-first YAML; secrets only `${ENV}`.
- Server may use DB as system of record + Config Studio; CLI still reads YAML.
- Cost caps, limits, deployment image pins, persistence, Redis.

### Observability

- Run timeline, per-LLM-call cost, dashboard, failure comment on ticket, draft PR on red verify.
- Liveness from orchestrator/pod + DB reconcile.

## Adopt / adapt / avoid

| Pattern | Decision | Why |
|---------|----------|-----|
| Ticket → PR closed loop | **Adopt** | Core product value |
| Plan approval before code | **Adopt** (HITL default, stricter than Smith headless webhooks) | Trust for v1 |
| Catalog config + `${ENV}` secrets | **Adopt** | Clear ops model |
| Claim/lease one-run-per-ticket | **Adopt** (Phase 1) | Correctness under races |
| PR-only landing, no merge | **Adopt** | Human review ownership |
| plan / result / decisions + cost | **Adopt** | Auditability |
| Per-repo Docker sandbox + injected agent | **Adapt** | Start with local/in-process; Docker in Phase 1 |
| Command-list pipelines | **Adapt** | One `fix-bug` pipeline first |
| Multi-tracker / multi-host | **Adapt** | Jira + GitHub only in v1 |
| ScopeRepos multi-repo | **Avoid for v1** | Single-repo projects until proven |
| Three orchestration modes + skills repo | **Avoid for v1** | Complexity without product-market fit yet |
| Dashboard / Config Studio / Redis streams | **Avoid for v0–1** | Thin CLI + files first |
| Spec dialogue (Slack/Teams), legal/MAD pipelines | **Avoid** | Not the MVP |
| Branding, design tokens, skill prompt bodies | **Avoid** | Original product identity |

## Intentional differences in AutoCoder

1. **Own abstractions** — `ITicketSource`, `IRepoHost`, `ILlmProvider`, `ISandboxRunner`, `IPipeline` (not Agent Smith type names).
2. **HITL always on by default** — headless only as an explicit Phase 2 opt-in.
3. **Thinner YAML** — fewer catalogs and knobs until we need them.
4. **Build order** — planner + LLM → GitHub PR → Jira trigger → sandbox (sandbox not first).
5. **Python workers optional** — only where agent/LLM libs clearly beat C#.
