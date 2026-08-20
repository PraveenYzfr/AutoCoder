using AutoCoder.Abstractions;

namespace AutoCoder.Tests;

internal static class TestContext
{
    public static PipelineContext New(string? work = null, string? artifacts = null)
    {
        var artifactsDir = artifacts ?? Path.Combine(Path.GetTempPath(), "autocoder-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(artifactsDir);
        return new PipelineContext
        {
            RunId = "test-run",
            PipelineName = "fix-bug",
            ArtifactsDirectory = artifactsDir,
            WorkDirectory = work,
            DryRun = false
        };
    }

    public static Ticket Ticket(string status = "AssignedToAgent", params string[] labels) =>
        new()
        {
            Key = "AC-101",
            Summary = "Test ticket",
            Description = "Body",
            Status = status,
            Labels = labels.ToList(),
            IssueType = "Bug"
        };
}
