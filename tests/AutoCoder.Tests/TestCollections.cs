namespace AutoCoder.Tests;

/// <summary>
/// xunit runs different test classes in parallel by default. Several test classes mutate
/// process-wide state (AUTOCODER_CONTAINER_WORKSPACE_ROOT and friends) via Environment.SetEnvironmentVariable.
/// Putting them in one named collection makes xunit run them sequentially with each other,
/// while everything else in the assembly still runs in parallel.
/// </summary>
[CollectionDefinition(Name)]
public sealed class ProcessEnvStateCollection
{
    public const string Name = "Process env state";
}
