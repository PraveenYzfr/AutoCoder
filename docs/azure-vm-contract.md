# AutoCoder → Claude (VM owner): what to provision

AutoCoder is ready to host. Claude owns the shared VM, infra compose, tunnel, and SQL logins.
This file is the **app contract**. Do not merge AutoCoder into the infra compose.

## What AutoCoder is

Jira `AssignedToAgent` → clone **SimpleApp** (`https://github.com/PraveenYzfr/SimpleApp`) → agent edits that clone → `dotnet build` / `dotnet test` in a **throwaway container** → GitHub PR (no merge) → Jira comment + **In Review**.

It does not clone AutoCoder. It does not use Ollama, Qdrant, or Redis today.

## Compose

```bash
docker compose -f docker-compose.vm.yml up -d --build
```

Joins external Docker network `${HUB_NETWORK:-hub}`. Starts **no** SQL/Redis/Qdrant.

## Port / ingress

| Item | Value |
|------|--------|
| Listen | **8081** inside compose network |
| Public | Cloudflare hostname `autocoder.praveenyzfr.com` → 8081 |
| Publish to NSG | **none** (no `ports:` on the VM file) |
| Health | `GET /health` |
| Webhook | `POST /webhook/jira` |

## SQL Express

Create empty database **`AutoCoder`** + least-privilege login. Schema not required for v1 (artifacts are files under `/var/lib/autocoder/runs`). Do not point AutoCoder at `RLogistics` or `SeekandDestroy`.

## Volumes / Docker

| Host path | Why |
|-----------|-----|
| `/var/lib/autocoder/runs` | clones + artifacts + `.nuget/packages` cache |
| Docker API | spawn throwaway `mcr.microsoft.com/dotnet/sdk:8.0` for SimpleApp build/test |

**Socket proxy (Claude):** OK. Point `DOCKER_HOST` at `tecnativa/docker-socket-proxy`. Docker CLI already honors it. Proxy must allow: **containers** create/start/wait/remove, **images** pull, **POST**. `docker version` is used for the health check (not `docker info`). No named Docker volumes — NuGet is a bind under `runs/.nuget`.

**Do not set sandbox `--network none`.** `dotnet build` must reach nuget.org on first restore. Default is `bridge`. Optional override: `AUTOCODER_SANDBOX_NETWORK` (keep `bridge` unless a nuget-only network exists).

Set `AUTOCODER_HOST_WORKSPACE_ROOT=/var/lib/autocoder/runs` so sibling build containers bind-mount the host path.

`AUTOCODER_REQUIRE_DOCKER=true` — refuse host-local builds on the VM.

## Env (from AutoCoder `.env`, never commit)

```
JIRA_BASE_URL=https://rgarchitects.atlassian.net
JIRA_EMAIL=...
JIRA_TOKEN=...
JIRA_WEBHOOK_SECRET=...
GITHUB_REPO_URL=https://github.com/PraveenYzfr/SimpleApp
GITHUB_TOKEN=...
GEMINI_API_KEY=...
DEEPSEEK_API_KEY=...
OPENAI_API_KEY=...
ANTHROPIC_API_KEY=...
GROQ_API_KEY=...
# Routed by default in enterprise.yml: DeepSeek cheap, Claude costly.
# AUTOCODER_CHEAP_PROVIDER=groq      # use Groq for coding/summarize (latency)
# AUTOCODER_COSTLY_PROVIDER=openai   # use GPT-4o for planning instead of Claude
AUTOCODER_CONFIG=/app/config/enterprise.yml
```

## Jira when the VM is deallocated

Webhook + **poll every 5 minutes** (`status = "AssignedToAgent"`). After the VM starts, poll picks up tickets Jira already moved. One live run per ticket (45-minute lease).

## Redis / Qdrant / Ollama

Unused. Redis db 4–5 reserved if needed later. **No Ollama.**

## Onboard order

Claude sequence: B, then A, then C. AutoCoder is C last. App side is ready; C waits only on hub network + tunnel + empty DB.
