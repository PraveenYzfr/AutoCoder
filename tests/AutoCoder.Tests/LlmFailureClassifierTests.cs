using System.Net.Http;
using AutoCoder.Core.Llm;

namespace AutoCoder.Tests;

public sealed class LlmFailureClassifierTests
{
    [Theory]
    [InlineData(429, true)]
    [InlineData(500, true)]
    [InlineData(502, true)]
    [InlineData(503, true)]
    [InlineData(504, true)]
    [InlineData(408, true)]
    [InlineData(529, true)]
    [InlineData(400, false)]
    [InlineData(401, false)]
    [InlineData(403, false)]
    [InlineData(404, false)]
    public void Classifies_by_status_code(int status, bool expectedTransient)
    {
        var ex = new LlmProviderException("deepseek", "boom", status);
        Assert.Equal(expectedTransient, LlmFailureClassifier.IsTransient(ex));
    }

    [Fact]
    public void Empty_content_is_permanent_even_without_a_status_code()
    {
        var ex = new LlmProviderException("deepseek", "empty", statusCode: null, isEmptyContent: true);
        Assert.False(LlmFailureClassifier.IsTransient(ex));
    }

    [Fact]
    public void Generic_network_blips_are_transient()
    {
        Assert.True(LlmFailureClassifier.IsTransient(new TaskCanceledException()));
        Assert.True(LlmFailureClassifier.IsTransient(new HttpRequestException("blip")));
    }

    [Fact]
    public void Unrelated_business_errors_default_to_permanent()
    {
        Assert.False(LlmFailureClassifier.IsTransient(
            new InvalidOperationException("No product code changes — refusing PR.")));
    }

    [Fact]
    public void Tier_exhausted_passes_through_its_own_flag_without_re_examining_the_inner_exception()
    {
        var transient = new LlmTierExhaustedException(
            "cheap", true, "all failed", new LlmProviderException("groq", "x", 401));
        var permanent = new LlmTierExhaustedException(
            "cheap", false, "all failed", new LlmProviderException("groq", "x", 503));

        Assert.True(LlmFailureClassifier.IsTransient(transient));
        Assert.False(LlmFailureClassifier.IsTransient(permanent));
    }

    [Fact]
    public void Walks_the_inner_exception_chain()
    {
        var inner = new LlmProviderException("deepseek", "rate limited", 429);
        var wrapped = new InvalidOperationException("wrapper", inner);
        Assert.True(LlmFailureClassifier.IsTransient(wrapped));
    }
}
