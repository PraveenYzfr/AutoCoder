# How to wire Jira → AutoCoder webhooks

## Switches

| Knob | Config | Env overlay |
|------|--------|-------------|
| Accept webhooks at all | `webhooks.enabled` | `AUTOCODER_WEBHOOKS_ENABLED` |
| Include webhook as a trigger | `triggers.mode: webhook` or `both` | `AUTOCODER_TRIGGERS_MODE` |
| Dry-run adapters | `webhooks.dry_run` | `AUTOCODER_WEBHOOKS_DRY_RUN` |
| Port | `webhooks.listen_port` | `AUTOCODER_WEBHOOKS_PORT` |
| Secret | `webhooks.secret_env` + env value | `JIRA_WEBHOOK_SECRET` |

Both `webhooks.enabled=true` **and** `triggers.mode` in `{webhook,both}` are required for a run to start.

## Run the server

```bash
dotnet run --project src/AutoCoder.Server -- --config config/autocoder.yml
```

Health: `GET http://localhost:8081/health`

## Local smoke test (no Jira)

```bash
curl -s -X POST http://localhost:8081/webhook/jira ^
  -H "Content-Type: application/json" ^
  --data-binary @samples/jira-webhook.json
```

Ticket must use status **AssignedToAgent** (see `trigger_statuses` in config). Optional project label: `autocoder`.

See [jira-trigger.md](jira-trigger.md) for the status → webhook → run flow.

## Point Jira at AutoCoder

1. Make the server reachable (ngrok / Cloudflare Tunnel / reverse proxy).
2. Jira → System → Webhooks → URL: `https://<host>/webhook/jira`
3. Events: Issue created / Issue updated.
4. Optional JQL: `project = AC AND labels = autocoder-sample-api`
5. Set `JIRA_WEBHOOK_SECRET` and send it as `X-AutoCoder-Token` (or `Authorization: Bearer …`). Enable `webhooks.require_secret: true`.

Jira Cloud system webhooks often omit HMAC signatures — use the shared token header or network controls.

## What gets updated

The repo comes from `projects.*.repos` → `repos.*.url` in config (allowlisted). Ticket text never chooses an arbitrary remote.
