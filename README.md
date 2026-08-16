# AutoCoder

> **Ticket → plan → code → PR.** Self-hosted AI automation with human approval before any code change.

AutoCoder turns a tracker ticket into a pull request on your repo. Inspired by [Agent Smith](https://github.com/holgerleichsenring/agent-smith) (MIT) — re-implemented cleanly for a thinner v1, not a blind fork.

| Status | Phase 0 scaffold |
|--------|------------------|
| Stack | .NET 8+ orchestrator (C#) · optional Python workers later |
| Trackers | Jira first (interfaces ready; dry-run uses sample JSON) |
| Code hosts | GitHub first |
| Models | Routed cheap/costly: DeepSeek Flash (coding/summarize) + Claude Sonnet or GPT-4o (planning) |
| Safety | HITL plan approval by default · no auto-merge · secrets via env · repo allowlist |

## Quick start (dry-run CLI)

```bash
dotnet run --project src/AutoCoder.Cli -- dry-run --ticket samples/ticket.json
```

No API keys, Docker, Jira, or GitHub required for a local demo (heuristic planner if no keys; DeepSeek + Claude/OpenAI when keys are set).

## Real run (ticket key + configured Jira)

Set in `.env` (never commit):

```bash
JIRA_BASE_URL=https://your-site.atlassian.net
GITHUB_REPO_URL=https://github.com/your-org/your-repo
JIRA_EMAIL=you@company.com
JIRA_TOKEN=...
DEEPSEEK_API_KEY=...
ANTHROPIC_API_KEY=...   # costly planning; or OPENAI_API_KEY
```

Then pass the ticket at runtime:

```bash
# Plan only / fake PR
dotnet run --project src/AutoCoder.Cli -- run --ticket AC-101 --config config/autocoder.yml --yes

# LIVE: clone repo, commit plan artifacts, push branch, open real GitHub PR (no merge)
dotnet run --project src/AutoCoder.Cli -- run --ticket AC-101 --config config/autocoder.yml --live --yes
```

`--live` requires `GITHUB_TOKEN` + `GITHUB_REPO_URL`. Jira fetch still needs `JIRA_BASE_URL` + `JIRA_EMAIL` + `JIRA_TOKEN`.

You can also live-test from sample JSON (no Jira):

```bash
dotnet run --project src/AutoCoder.Cli -- dry-run --ticket samples/ticket.json --live --yes
```

## Quick start (Jira webhook server)

```bash
dotnet run --project src/AutoCoder.Server -- --config config/autocoder.yml
# another terminal:
curl -s -X POST http://localhost:8081/webhook/jira -H "Content-Type: application/json" --data-binary @samples/jira-webhook.json
```

Webhooks are **switchable**: `webhooks.enabled` + `triggers.mode` (`cli` | `webhook` | `both`). See [docs/webhooks.md](docs/webhooks.md).

## What it does

1. **Ingest** a ticket when Jira status becomes **AssignedToAgent** (webhook) — or CLI for testing.
2. **Resolve** which project/repo it belongs to (catalog config).
3. **Plan** the change (costly model: Claude Sonnet or GPT-4o). Coding stays on cheap DeepSeek.
4. **Wait for human approval** before any code mutation (unless `--yes`).
5. **Implement** in a sandbox, run tests, commit, open a **PR** (never merge in v1).
6. **Write back** to the ticket with PR links and cost/outcome.

## Architecture overview

```
TicketSource → Orchestrator → Planner (LlmProvider) → ApprovalGate
                    ↓                                      ↓
              Run artifacts                    SandboxRunner → RepoHost → PR
                    ↓
              Ticket writeback
```

Details: [docs/architecture.md](docs/architecture.md) · lessons from Agent Smith: [docs/agent-smith-lessons.md](docs/agent-smith-lessons.md) · roadmap: [docs/roadmap.md](docs/roadmap.md).

## Repo layout

```
AutoCoder.slnx             # .NET solution
src/
  AutoCoder.Abstractions   # TicketSource, RepoHost, LlmProvider, SandboxRunner, Pipeline
  AutoCoder.Core           # Pipeline runner, approval gate, dry-run + webhook helpers
  AutoCoder.Cli            # autocoder dry-run
  AutoCoder.Server         # HTTP webhooks (Jira) + /health
config/autocoder.yml       # local switchable config (webhooks on by default)
docs/                      # architecture, lessons, roadmap, config example, webhooks
samples/                   # sample ticket + jira-webhook JSON
docker-compose.yml         # stub for Phase 1+ self-hosting
runs/                      # local dry-run output (gitignored)
```

## Configuration

See [docs/config-example.yml](docs/config-example.yml). Secrets are `${ENV}` references only — never commit real tokens.

## Open questions (you decide)

1. Jira Cloud vs Server/Data Center first?
2. Approval UX: CLI prompt only, or dashboard/comment “approve” on the ticket?
3. Default model provider for your org (Azure OpenAI / OpenAI / Anthropic)?
4. Single-repo only until Phase 2, or early multi-repo?

## License

MIT. Patterns learned from Agent Smith; no large proprietary chunks copied.
