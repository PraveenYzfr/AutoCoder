using System.Text.Json;
using AutoCoder.Abstractions;
using Microsoft.Extensions.Logging;

namespace AutoCoder.Core.Logging;

/// <summary>
/// Structured run log: JSON console via ILogger, plus JSONL under runs/{id}/run.log.
/// </summary>
public static class RunLog
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static ILogger? Logger { get; private set; }

    public static void Configure(ILogger logger) => Logger = logger;

    public static void Event(
        string name,
        PipelineContext? context = null,
        LogLevel level = LogLevel.Information,
        Exception? exception = null,
        params (string Key, object? Value)[] fields)
    {
        var payload = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["ts"] = DateTime.UtcNow.ToString("O"),
            ["event"] = name,
            ["runId"] = context?.RunId,
            ["ticket"] = context?.Ticket?.Key,
            ["pipeline"] = context?.PipelineName,
            ["dryRun"] = context?.DryRun
        };
        foreach (var (key, value) in fields)
            payload[key] = value;

        if (context is not null)
        {
            payload["tokens"] = context.Spend.TotalTokens;
            payload["usd"] = context.Spend.EstimatedUsd;
            payload["toolCalls"] = context.Spend.ToolCalls;
        }

        var line = JsonSerializer.Serialize(payload, Json);
        WriteFile(context, line);

        if (Logger is not null)
        {
            using (Logger.BeginScope(payload))
            {
                if (exception is not null)
                    Logger.Log(level, exception, "{Event} {Payload}", name, line);
                else
                    Logger.Log(level, "{Event} {Payload}", name, line);
            }
        }
        else if (level >= LogLevel.Warning)
        {
            Console.Error.WriteLine(line);
        }
        else
        {
            Console.WriteLine(line);
        }
    }

    private static void WriteFile(PipelineContext? context, string line)
    {
        if (context is null || string.IsNullOrWhiteSpace(context.ArtifactsDirectory) || string.IsNullOrWhiteSpace(context.RunId))
            return;
        try
        {
            var dir = Path.Combine(context.ArtifactsDirectory, context.RunId);
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "run.log"), line + Environment.NewLine);
        }
        catch
        {
            // Logging must not fail the pipeline.
        }
    }
}
