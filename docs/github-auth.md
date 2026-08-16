# GitHub authentication

AutoCoder never talks to GitHub “as a magic identity.” It always needs a **token**.
The pipeline only calls `IGitCredentialProvider.GetCredentialsAsync()` — it does not care how the token was obtained.

## Why an interface?

```text
Pipeline / GitHubRepoHost
        │
        ▼
IGitCredentialProvider.GetCredentialsAsync()  ←── stable contract
        │
   ┌────┴────┐
   ▼         ▼
  PAT     GitHub App
(.env)   (short-lived installation token)
```

- Swap auth by changing **`GITHUB_AUTH_MODE`** (and secrets).
- **No rewrite** of clone / commit / push / open-PR steps.
- Same pattern later for Jira (`IJiraCredentialProvider`) if needed.

## Modes

| Mode | Env | Use |
|------|-----|-----|
| `pat` (default) | `GITHUB_TOKEN` | Local laptop / early Azure VM |
| `github_app` | `GITHUB_APP_ID`, `GITHUB_APP_INSTALLATION_ID`, `GITHUB_APP_PRIVATE_KEY_PATH` (or `GITHUB_APP_PRIVATE_KEY`) | Enterprise bot on VM / AKS / OCP |

```bash
GITHUB_AUTH_MODE=pat
# or
GITHUB_AUTH_MODE=github_app
```

Secrets stay out of git (`.env`, VM env, K8s/OCP secret mounts). Key Vault is optional storage — not required for the design.
