namespace AutoCoder.Core.Agent;

/// <summary>Single source of truth for coding-loop tools. Each client formats these for its API.</summary>
internal static class CodingToolCatalog
{
    public const string NudgeNoProductChange =
        "You have not changed product source yet. Use write_file, then finish.";

    public static readonly CodingToolDefinition[] Tools =
    [
        new("list_files", "List files and folders relative to the repo root.",
            new CodingToolParam("path", "Relative directory. Use empty or '.' for root.", Required: false)),
        new("read_file", "Read a text file relative to the repo root.",
            new CodingToolParam("path", "Relative file path", Required: true)),
        new("write_file", "Create or overwrite a text file. Use this to implement the fix or feature.",
            new CodingToolParam("path", "Relative file path", Required: true),
            new CodingToolParam("content", "Full file contents", Required: true)),
        new("grep", "Search file contents for a string (case-insensitive).",
            new CodingToolParam("pattern", "Text to find", Required: true),
            new CodingToolParam("path", "Relative file or directory to search", Required: false)),
        new("finish", "Call when the code change is complete. Do not call until files are written.",
            new CodingToolParam("summary", "What you changed and why", Required: true))
    ];
}

internal sealed record CodingToolDefinition(string Name, string Description, params CodingToolParam[] Parameters);

internal sealed record CodingToolParam(string Name, string Description, bool Required);
