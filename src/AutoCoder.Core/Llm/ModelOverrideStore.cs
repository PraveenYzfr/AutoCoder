using System.Text.Json;
using AutoCoder.Abstractions.Config;
using AutoCoder.Core.Logging;
using AutoCoder.Core.Runs;

namespace AutoCoder.Core.Llm;

public sealed class RoleModelOverride
{
    public string Provider { get; set; } = "";
    public string Model { get; set; } = "";
}

public sealed class ModelOverrideFile
{
    public DateTimeOffset UpdatedAt { get; set; }
    public Dictionary<string, RoleModelOverride> Roles { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public static class ModelOverrideStore
{
    public static readonly string[] Roles =
    [
        "scout", "summarize", "comment", "coding",
        "planning", "thinking", "decision", "primary"
    ];

    public static readonly string[] Providers = ["deepseek", "groq", "openai", "anthropic", "gemini"];

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    public static string FilePath(AutoCoderOptions? options = null) =>
        Path.Combine(RunWorkspace.AppRoot(options), "model-overrides.json");

    public static ModelOverrideFile Load(AutoCoderOptions? options = null)
    {
        var path = FilePath(options);
        if (!File.Exists(path))
            return new ModelOverrideFile();
        try
        {
            var file = JsonSerializer.Deserialize<ModelOverrideFile>(File.ReadAllText(path), Json)
                       ?? new ModelOverrideFile();
            file.Roles ??= new Dictionary<string, RoleModelOverride>(StringComparer.OrdinalIgnoreCase);
            return file;
        }
        catch
        {
            return new ModelOverrideFile();
        }
    }

    public static bool TryGet(string role, out RoleModelOverride? slot, AutoCoderOptions? options = null)
    {
        var file = Load(options);
        if (file.Roles.TryGetValue(role, out var found)
            && !string.IsNullOrWhiteSpace(found.Provider)
            && !string.IsNullOrWhiteSpace(found.Model))
        {
            slot = found;
            return true;
        }

        slot = null;
        return false;
    }

    public static ModelOverrideFile Set(string role, string provider, string model, string? who, AutoCoderOptions? options = null)
    {
        role = role.Trim().ToLowerInvariant();
        provider = provider.Trim().ToLowerInvariant();
        if (!Roles.Contains(role, StringComparer.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Unknown role '{role}'.");
        if (!Providers.Contains(provider, StringComparer.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Unknown provider '{provider}'.");
        if (string.IsNullOrWhiteSpace(model))
            throw new InvalidOperationException("Model is required.");

        var file = Load(options);
        file.Roles.TryGetValue(role, out var previous);
        file.Roles[role] = new RoleModelOverride { Provider = provider, Model = model.Trim() };
        file.UpdatedAt = DateTimeOffset.UtcNow;
        Save(file, options);
        RunLog.Event(
            "ui.model.override",
            fields:
            [
                ("who", who ?? "unknown"),
                ("role", role),
                ("from", previous is null ? "config" : $"{previous.Provider}/{previous.Model}"),
                ("to", $"{provider}/{model.Trim()}")
            ]);
        return file;
    }

    public static ModelOverrideFile Reset(string? role, string? who, AutoCoderOptions? options = null)
    {
        var file = Load(options);
        if (string.IsNullOrWhiteSpace(role))
        {
            file.Roles.Clear();
        }
        else
        {
            file.Roles.Remove(role.Trim());
        }

        file.UpdatedAt = DateTimeOffset.UtcNow;
        Save(file, options);
        RunLog.Event(
            "ui.model.reset",
            fields: [("who", who ?? "unknown"), ("role", role ?? "*")]);
        return file;
    }

    private static void Save(ModelOverrideFile file, AutoCoderOptions? options)
    {
        var path = FilePath(options);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(file, Json));
    }
}
