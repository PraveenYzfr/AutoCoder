# How AutoCoder picks up a Jira ticket

You do **not** paste the ticket key into AutoCoder for normal use.

## Intended flow (what you asked for)

```text
You move Jira issue → status "AssignedToAgent"
        │
        ▼
Jira sends webhook POST → AutoCoder /webhook/jira
        │
        ▼
AutoCoder reads issue key + fields from the payload
        │
        ▼
Runs plan → (live) clone/PR on allowlisted GitHub repo
```

The ticket key arrives **inside the webhook JSON** (`issue.key`). AutoCoder extracts it. No CLI typing required.

## What you configure once

1. Create status **`AssignedToAgent`** on the Jira board workflow.  
2. AutoCoder `trigger_statuses: ["AssignedToAgent"]` (already set in `config/autocoder.yml`).  
3. Run AutoCoder Server (reachable by Jira — local tunnel or Azure VM).  
4. In Jira: System → Webhooks → URL `https://<host>/webhook/jira`, events Issue updated.

Optional: label `autocoder` if you use tag-based project routing (can be relaxed when only one project is configured).

## What `--ticket` is for

| Command | Purpose |
|---------|---------|
| Webhook + status change | **Real product path** |
| `run --ticket AC-101` | Manual test / debug without webhook |
| `dry-run --ticket file.json` | Offline demo |

## Local test without real Jira

```bash
dotnet run --project src/AutoCoder.Server --no-launch-profile
# other terminal:
Invoke-RestMethod -Method Post -Uri http://localhost:8081/webhook/jira `
  -ContentType "application/json" -InFile samples/jira-webhook.json
```

That sample payload already has status `AssignedToAgent`.
