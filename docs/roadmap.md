# AutoCoder roadmap

## Phase 0 — Scaffold (current)

**Goal:** Understandable architecture + runnable dry-run with zero external deps.

Acceptance criteria:

- [x] Git repo with MIT LICENSE and README
- [x] Docs: architecture, Agent Smith lessons, roadmap, config example
- [x] .NET solution with Abstractions / Core / Cli / Server
- [x] Interfaces for TicketSource, RepoHost, LlmProvider, SandboxRunner, Pipeline
- [x] `dotnet run … dry-run` prints plan + fake PR payload and writes `runs/{id}/`
- [x] Switchable Jira webhook server (`webhooks.enabled` + `triggers.mode`)
- [x] `docker-compose.yml` stub + `.env.example`

## Phase 1 — Real integrations (thin vertical slice)

**Goal:** One ticket path works end-to-end on a single allowlisted GitHub repo with human approval.

Acceptance criteria:

- [ ] `ILlmProvider` for Azure OpenAI or OpenAI produces a real plan
- [ ] CLI approval prompt (approve / reject / edit notes) before implement
- [ ] `IRepoHost` creates branch + opens PR (no merge)
- [ ] `ITicketSource` for Jira: fetch issue + comment writeback with PR URL
- [ ] Claim store (SQLite is enough) — one live run per ticket
- [ ] Local or Docker sandbox runs tests via allowlisted commands
- [ ] `result.md` includes token usage and estimated USD cost
- [ ] Secret scan on staged diff before commit
- [ ] Compose brings up orchestrator (+ SQLite volume); webhook endpoint optional stub

Suggested implementation order:

1. Planner + LLM  
2. GitHub PR  
3. Jira fetch/writeback + claim  
4. Sandbox  

## Phase 2 — Production triggers and hardening

**Goal:** Self-hosted daily driver with safe automation.

Acceptance criteria:

- [ ] Jira webhook (HMAC when available) + poll fallback
- [ ] Label / status / comment triggers from config
- [ ] Repo allowlist enforcement + project resolution by Jira label/tag
- [ ] Optional headless mode (explicit config; not default)
- [ ] Cost caps and run cancellation
- [ ] Failure writeback (`failed` status/label + reason)
- [ ] Draft PR when verification is red
- [ ] Basic run list UI or structured JSON API for runs
- [ ] Still **no auto-merge**

## Out of scope until later

- Multi-repo single ticket / path-prefix routing
- Azure DevOps / GitLab trackers and hosts
- Multi-skill discussion pipelines, security scanners, Slack/Teams
- Kubernetes multi-replica HA

## Open decisions

1. Jira Cloud vs Server first?
2. Approval channel: CLI vs ticket comment keyword vs small web UI?
3. Org default LLM provider and model tiering (cheap scout vs strong coder)?
4. When to introduce Redis (queue) vs stay on DB-only claims?
