# Enterprise runnable system — what the config means

Target file: [config/enterprise.yml](../config/enterprise.yml)

## Honest split

| Capability | Config says | Code today |
|------------|-------------|------------|
| Jira status `AssignedToAgent` → webhook | Yes | Parser + filter exist; needs public URL |
| Fetch real Jira ticket | Yes | `JiraTicketSource` exists; needs `JIRA_TOKEN` |
| Gemini plan | Yes | Works |
| Human approval before code | Yes | CLI gate exists |
| Clone allowlisted GitHub repo | Yes | `--live` clone/push/PR exists |
| **Agent edits product code** | `AgenticImplement` | **Not built** |
| **dotnet build must pass** | `require_build: true` | **Not a hard gate** |
| **dotnet test** | `require_tests: true` | Best-effort only |
| **Jira comment + transition** | `writeback` | **Log only, no API POST** |
| No auto-merge | `auto_merge: false` | Enforced (we never merge) |
| Database | Not required for v1 | None (files in `runs/`) |

YAML does not implement `AgenticImplement`. That is code. The config is the contract so we do not redesign after the VM.

## Production flow this config encodes

1. Human moves issue to **AssignedToAgent** on `rgarchitects.atlassian.net`
2. Jira webhook POST `https://autocoder.praveenyzfr.com/webhook/jira` (JSON body, JQL on that status)
3. AutoCoder claims one run per ticket
4. Plan → human approve
5. Sandbox clone `PraveenYzfr/SimpleApp` `master` → branch `autocoder/{KEY}`
6. Agent changes **application source**, not only markdown
7. `dotnet build` fails the run if compile fails
8. `dotnet test`; red tests → draft PR still allowed
9. Secret scan; then push + PR; **never merge**
10. Jira comment with PR URL; status **In Review**

## What you fill (secrets only)

See [enterprise-env.example](enterprise-env.example): `JIRA_TOKEN`, `GITHUB_TOKEN`, `GEMINI_API_KEY`, `JIRA_WEBHOOK_SECRET`.
