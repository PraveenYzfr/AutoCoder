using AutoCoder.Abstractions;
using AutoCoder.Core.GitHub;

namespace AutoCoder.Tests;

public sealed class GitHubRepoHostTests
{
    [Theory]
    [InlineData("https://github.com/PraveenYzfr/SimpleApp", "https://github.com/PraveenYzfr/SimpleApp")]
    [InlineData("https://github.com/PraveenYzfr/SimpleApp.git", "https://github.com/PraveenYzfr/SimpleApp")]
    [InlineData("https://github.com/PraveenYzfr/SimpleApp/", "https://github.com/PraveenYzfr/SimpleApp")]
    [InlineData("git@github.com:PraveenYzfr/SimpleApp.git", "https://github.com/PraveenYzfr/SimpleApp")]
    [InlineData("https://x-access-token:ghp_abc123@github.com/PraveenYzfr/SimpleApp.git", "https://github.com/PraveenYzfr/SimpleApp")]
    public void NormalizeRepoUrl_strips_git_suffix_ssh_form_and_embedded_credentials(string input, string expected)
    {
        Assert.Equal(expected, GitHubRepoHost.NormalizeRepoUrl(input));
    }

    [Fact]
    public void ParseOwnerRepo_extracts_owner_and_repo()
    {
        var (owner, repo) = GitHubRepoHost.ParseOwnerRepo("https://github.com/PraveenYzfr/SimpleApp.git");
        Assert.Equal("PraveenYzfr", owner);
        Assert.Equal("SimpleApp", repo);
    }

    [Fact]
    public void ParseOwnerRepo_throws_on_non_github_url()
    {
        Assert.Throws<InvalidOperationException>(() => GitHubRepoHost.ParseOwnerRepo("https://gitlab.com/foo/bar"));
    }

    [Fact]
    public async Task EnsureAllowlisted_passes_when_allowlist_is_empty()
    {
        var host = new GitHubRepoHost([], new FakeCredentials());
        await host.EnsureAllowlistedAsync("https://github.com/Anyone/AnyRepo");
    }

    [Fact]
    public async Task EnsureAllowlisted_passes_for_a_listed_repo_regardless_of_dotgit_or_case()
    {
        var host = new GitHubRepoHost(["https://github.com/PraveenYzfr/SimpleApp"], new FakeCredentials());
        await host.EnsureAllowlistedAsync("https://github.com/praveenyzfr/simpleapp.git");
    }

    [Fact]
    public async Task EnsureAllowlisted_rejects_a_repo_not_on_the_list()
    {
        var host = new GitHubRepoHost(["https://github.com/PraveenYzfr/SimpleApp"], new FakeCredentials());
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => host.EnsureAllowlistedAsync("https://github.com/PraveenYzfr/AutoCoder"));
        Assert.Contains("not on the allowlist", ex.Message);
    }

    private sealed class FakeCredentials : IGitCredentialProvider
    {
        public string Mode => "fake";
        public Task<GitCredentials> GetCredentialsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new GitCredentials { AccessToken = "fake-token" });
    }
}
