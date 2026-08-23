# Task Spec: Implement Anthropic Coding Tool Client

## Handoff note
Written for a parallel implementation pass (e.g. by "AutoCoder Pro" or any other
agent/dev) working on a *different* copy/branch of this same codebase. Follow this
spec exactly so the two implementations converge on the same design and don't
conflict when merged. If anything here is ambiguous, prefer matching the existing
`GeminiToolClient.cs` pattern as the tie-breaker.

## Problem

`AutoCoder.Core/Agent/` has a coding tool-loop (`CodingAgentLoop.cs`) that drives an
LLM through function-calling tools (`list_files`, `read_file`, `write_file`, `grep`,
`finish`) to actually implement a ticket. Two tool clients exist today:

- `OpenAiToolClient.cs` — shared by DeepSeek, OpenAI, Groq (all OpenAI-compatible
  `tools`/`tool_calls` wire format).
- `GeminiToolClient.cs` — Gemini's own `functionDeclarations`/`functionCall`/
  `functionResponse` format.

**Anthropic/Claude has no tool client.** When a user selects `anthropic`/`claude`
as the `coding` role (this is selectable today via the model-override dropdown in
the dashboard UI — `ModelOverrideStore.Providers` includes `"anthropic"` for every
role, including `coding`), `CodingAgentLoop` silently ignores the choice and falls
back to DeepSeek:

```csharp
// src/AutoCoder.Core/Agent/CodingAgentLoop.cs
case "anthropic":
case "claude":
    Console.WriteLine("[agent] Anthropic has no coding tool loop; using cheap DeepSeek for file edits.");
    await RunOpenAiCompatibleAsync(
        context, work, ticket, cancellationToken,
        "DEEPSEEK_API_KEY",
        DeepSeekModels.Flash,
        "https://api.deepseek.com/v1",
        "deepseek");
    break;
```

This is misleading UX (the dropdown implies Claude can code) and wastes the user's
selection. Goal: implement a real `AnthropicToolClient` and wire it in so choosing
Anthropic for `coding` actually runs Claude with tools.

## Why Anthropic needs its own client (not reuse OpenAI's)

Anthropic's Messages API (`POST https://api.anthropic.com/v1/messages`) uses a
genuinely different wire format from OpenAI's chat-completions:

| Aspect | OpenAI-compatible | Anthropic |
|---|---|---|
| Auth | `Authorization: Bearer <key>` | `x-api-key: <key>` + `anthropic-version: 2023-06-01` header |
| System prompt | message with `role: "system"` inside `messages[]` | top-level `system` string field, **not** in `messages[]` |
| Message roles | `system`/`user`/`assistant`/`tool` | only `user`/`assistant` |
| Tool declaration | `tools: [{ type: "function", function: {...} }]` | `tools: [{ name, description, input_schema }]` (flatter, no `type`/`function` wrapper, field is `input_schema` not `parameters`) |
| Model requests a tool call | `message.tool_calls[]` with `function.name`/`function.arguments` (string JSON) | `content[]` array containing a block with `type: "tool_use"`, `id`, `name`, `input` (already a JSON **object**, not a string) |
| Returning tool results | `{ role: "tool", tool_call_id, content }` message | a `user` message whose `content` is `[{ type: "tool_result", tool_use_id, content }]` |
| Assistant turn with tool_use must be replayed back | `{ role: "assistant", content, tool_calls }` | `{ role: "assistant", content: [...original content blocks...] }` — must replay the exact `content` blocks Claude returned (text blocks + tool_use blocks), not a synthesized shape |
| Stop reason for "wants to call a tool" | tool_calls array is present | top-level `stop_reason == "tool_use"` |
| Usage fields | `usage.prompt_tokens` / `usage.completion_tokens` | `usage.input_tokens` / `usage.output_tokens` |
| `temperature` | always accepted | **must be omitted** for `claude-sonnet-5`/`claude-4.x`/`opus-4`/`haiku-4` models (400 error otherwise) — see `AnthropicLlmProvider.AcceptsTemperature` |

Reference existing code before writing anything:
- `src/AutoCoder.Core/Llm/AnthropicLlmProvider.cs` — the non-tool Anthropic client
  (system prompt handling, auth headers, `AcceptsTemperature`, retry/usage pattern).
- `src/AutoCoder.Core/Agent/GeminiToolClient.cs` — the closest existing example of a
  *native* (non-OpenAI-shaped) tool client wired into `CodingAgentLoop` — mirror its
  structure (a `GenerateAsync` returning a shared turn/part model, a private
  `ToolDefs()`, usage recording, error handling).
- `src/AutoCoder.Core/Agent/OpenAiToolClient.cs` — for the overall shape of a tool
  client class (ctor, retry via `TransientRetry.SendAsync`, `LlmDailyBudget.Consume()`
  at the top of `GenerateAsync`).
- `src/AutoCoder.Core/Agent/CodingAgentLoop.cs` — the turn-loop driver; you'll add a
  new `RunAnthropicTurnsAsync` alongside `RunGeminiTurnsAsync`/`RunOpenAiTurnsAsync`.
- `src/AutoCoder.Core/Agent/WorkspaceTools.cs` — the 5 tools' actual implementations
  (`ListFiles`, `ReadFile`, `WriteFile`, `Grep`) — tool defs must match these exactly
  (same 5 tools, same params: `list_files(path)`, `read_file(path)`,
  `write_file(path, content)`, `grep(pattern, path)`, `finish(summary)`).
- `src/AutoCoder.Core/Llm/LlmUsage.cs` — add a new `AddAnthropicUsage(model, rawJson)`
  helper mirroring `AddGeminiUsage`/`AddOpenAiUsage`, reading `usage.input_tokens` /
  `usage.output_tokens`, then call `Add("anthropic", model, prompt, completion)`.

## Implementation steps

1. **New file** `src/AutoCoder.Core/Agent/AnthropicToolClient.cs`:
   - `internal sealed class AnthropicToolClient` with ctor `(HttpClient http, string apiKey, string model)`.
     Set `x-api-key` and `anthropic-version: 2023-06-01` headers (see
     `AnthropicLlmProvider` ctor for the exact pattern).
   - `public async Task<AnthropicTurn> GenerateAsync(string system, List<object> messages, CancellationToken ct)`:
     - Call `LlmDailyBudget.Consume()` first.
     - Build payload: `model`, `max_tokens` (use 8192 to match other tool clients),
       `system` (raw string, top-level field — not in `messages`), `messages`, `tools`
       (from `ToolDefs()`). Only add `temperature = 0.2` if
       `AnthropicLlmProvider.AcceptsTemperature(model)` is true — reuse that static
       method (it's `internal static`, same assembly, so it's directly callable from
       `AutoCoder.Core.Agent`).
     - POST via `TransientRetry.SendAsync("agent.anthropic", ...)` to
       `https://api.anthropic.com/v1/messages`.
     - On non-success status, throw `InvalidOperationException` with a truncated body,
       matching the other two clients' error message style.
     - Record usage: call a new `LlmUsage.AddAnthropicUsage(model, raw)`.
     - Parse `content[]` array from the response root (not nested under `candidates`
       like Gemini, not `choices[0].message` like OpenAI — it's a **top-level**
       `content` array, same shape as `AnthropicLlmProvider` already parses, but now
       also handling `type: "tool_use"` blocks in addition to `type: "text"`).
     - Return an `AnthropicTurn` containing:
       - `Parts`: list of parsed blocks (text or tool_use), so `CodingAgentLoop` can
         treat them uniformly.
       - `RawContentBlocks`: the **raw/original** `content` JSON array (as `object`,
         e.g. via `JsonSerializer.Deserialize<List<object>>` or just re-serialize the
         raw `JsonElement`s) — needed because the next request's `assistant` message
         must replay these exact blocks verbatim (Anthropic requires the assistant
         turn's content to match what it produced, including tool_use `id`s).
       - `StopReason` (string) — useful for clarity/logging, not strictly required
         for control flow if you key off "any tool_use blocks present" like the other
         two clients do.
   - Define supporting types in the same file (mirror `GeminiTurn`/`GeminiPart`):
     ```csharp
     internal sealed class AnthropicTurn
     {
         public List<AnthropicPart> Parts { get; init; } = [];
         public List<object> RawContentBlocks { get; init; } = [];
         public string Raw { get; init; } = "";
     }

     internal sealed class AnthropicPart
     {
         public string? Text { get; init; }
         public string? ToolUseId { get; init; }
         public string? FunctionName { get; init; }
         public string? FunctionArgsJson { get; init; } // JSON-serialized input object
         public bool IsFunction => !string.IsNullOrWhiteSpace(FunctionName);
     }
     ```
   - `private static object[] ToolDefs()` — same 5 tools as the other two clients,
     but in Anthropic's flatter shape:
     ```csharp
     new {
         name = "write_file",
         description = "Create or overwrite a text file. Use this to implement the fix or feature.",
         input_schema = new {
             type = "object",
             properties = new Dictionary<string, object> {
                 ["path"] = new { type = "string", description = "Relative file path" },
                 ["content"] = new { type = "string", description = "Full file contents" }
             },
             required = new[] { "path", "content" }
         }
     }
     ```
     Keep tool names/descriptions **byte-for-byte identical** to the strings already
     used in `GeminiToolClient.ToolDefs()` / `OpenAiToolClient.ToolDefs()` so behavior
     is consistent across providers.

2. **`src/AutoCoder.Core/Llm/LlmUsage.cs`**: add
   ```csharp
   public static void AddAnthropicUsage(string model, string rawJson)
   {
       if (!TryReadAnthropicUsage(rawJson, out var prompt, out var completion))
           return;
       Add("anthropic", model, prompt, completion);
   }

   private static bool TryReadAnthropicUsage(string raw, out int prompt, out int completion)
   {
       prompt = 0; completion = 0;
       try
       {
           using var doc = System.Text.Json.JsonDocument.Parse(raw);
           if (!doc.RootElement.TryGetProperty("usage", out var usage))
               return false;
           prompt = usage.TryGetProperty("input_tokens", out var p) ? p.GetInt32() : 0;
           completion = usage.TryGetProperty("output_tokens", out var c) ? c.GetInt32() : 0;
           return prompt > 0 || completion > 0;
       }
       catch { return false; }
   }
   ```

3. **`src/AutoCoder.Core/Agent/CodingAgentLoop.cs`**:
   - Replace the `case "anthropic": case "claude":` branch (currently falling back to
     DeepSeek) with a real dispatch to a new `RunAnthropicAsync(...)` method, mirroring
     `RunGeminiAsync`'s shape:
     ```csharp
     case "anthropic":
     case "claude":
         await RunAnthropicAsync(context, work, ticket, cancellationToken, model);
         break;
     ```
   - Add `RunAnthropicAsync` (mirrors `RunGeminiAsync`):
     ```csharp
     private async Task RunAnthropicAsync(
         PipelineContext context, string work, Ticket ticket, CancellationToken cancellationToken, string model)
     {
         var key = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY")
                   ?? throw new InvalidOperationException("ANTHROPIC_API_KEY is required for the coding agent.");
         if (string.IsNullOrWhiteSpace(model) || model.Contains("deepseek", ...) || ...)
             model = "claude-sonnet-5"; // sane default, mirror Gemini's guard against cross-provider model names
         var tools = new WorkspaceTools(work);
         using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
         var client = new AnthropicToolClient(http, key, model);
         await RunAnthropicTurnsAsync(context, ticket, tools, client, model, TurnCap(), cancellationToken);
     }
     ```
   - Add `RunAnthropicTurnsAsync` — mirrors `RunGeminiTurnsAsync`'s loop structure
     (same `Prompt(...)`, same `Execute(...)` tool dispatch, same "no product change
     yet → nudge and continue" logic, same `RunLog.Event(...)` calls with
     `("provider", "anthropic")`), but building Anthropic's message shape each turn:
     - Initial: `messages = [ { role: "user", content: user } ]` (plain string content
       is fine for a simple user turn).
     - After a turn with tool_use blocks:
       - Append `{ role: "assistant", content: reply.RawContentBlocks }` (the exact
         blocks Claude returned — required for Anthropic's stateless-but-strict
         message replay).
       - Append `{ role: "user", content: [ { type: "tool_result", tool_use_id: id, content: result } for each call ] }`.
     - After a turn with **no** tool_use blocks (plain text, nudge case): append
       `{ role: "assistant", content: text }` then `{ role: "user", content: "You have not changed product source yet. Use write_file, then finish." }` — same nudge text as the other two loops, for consistency.
     - `finish` tool call handling: identical to the other two loops — set
       `context.AgentSummary = result` and break after processing all calls in that
       turn.
   - Set `LlmCallContext.CurrentRole = "coding"` / `CurrentTier = "cheap"` at the start,
     same as the other two loop methods (even though Claude is usually the "costly"
     tier elsewhere — match existing convention for the coding role specifically,
     since `coding` role is always tagged `"cheap"` tier in this loop regardless of
     provider; grep `CurrentTier = "cheap"` in this file to confirm before changing).

4. **No changes needed** to `ModelOverrideStore.cs`, `ModelCatalog.cs`, or the
   dashboard `app.js` — Anthropic is already a valid provider option everywhere in
   the UI/config layer; only the actual execution path (`CodingAgentLoop`) was missing
   support.

## Acceptance criteria

- Selecting `anthropic`/a Claude model for the `coding` role (via
  `model-overrides.json`, CLI config, or the dashboard picker) actually drives file
  edits through Claude's tool-use loop — no more silent DeepSeek fallback, no more
  `"[agent] Anthropic has no coding tool loop"` log line for this case.
- `dotnet build` succeeds; `dotnet test` (existing suite in
  `tests/AutoCoder.Tests`) still passes.
- A dry run (`dotnet run --project src/AutoCoder.Cli -- dry-run --ticket samples/ticket.json`)
  with `coding` role overridden to `anthropic`/`claude-sonnet-5` and
  `ANTHROPIC_API_KEY` set completes, writes at least one product file, and calls
  `finish`.
- Token usage/cost for these calls shows up under provider `"anthropic"` in run logs
  (`llm.call` events) and the dashboard's per-run model usage panel — same as
  existing Anthropic planning calls already do.
- `claude-sonnet-5`/`claude-4.x`/`opus-4`/`haiku-4` models don't get sent
  `temperature` (reuse `AnthropicLlmProvider.AcceptsTemperature`); older models still
  do.

## Non-goals / out of scope

- Streaming responses — none of the existing tool clients stream; don't add it here.
- Prompt caching / extended thinking — not used by the existing Anthropic planning
  client either; skip for parity.
- Changing the `default` (unspecified provider) fallback behavior — that still goes
  to DeepSeek and is unrelated to this task.
