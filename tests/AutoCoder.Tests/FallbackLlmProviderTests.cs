using AutoCoder.Abstractions;
using AutoCoder.Core.Llm;

namespace AutoCoder.Tests;

public sealed class FallbackLlmProviderTests
{
    [Fact]
    public async Task Succeeds_on_primary_without_touching_the_fallback()
    {
        var primary = new StubProvider(_ => Task.FromResult(Response("primary")));
        var fallback = new StubProvider(_ => throw new InvalidOperationException("should not be called"));
        var sut = new FallbackLlmProvider("cheap", [("deepseek", primary), ("groq", fallback)]);

        var result = await sut.CompleteAsync(Request());

        Assert.Equal("primary", result.Content);
        Assert.Equal(1, primary.Calls);
        Assert.Equal(0, fallback.Calls);
    }

    [Fact]
    public async Task Falls_back_to_the_next_provider_on_a_business_error()
    {
        // Same shape as the SCRUM-7 Anthropic `temperature` 400 — no point retrying it, switch providers.
        var primary = new StubProvider(_ => throw new LlmProviderException("deepseek", "temperature deprecated", 400));
        var fallback = new StubProvider(_ => Task.FromResult(Response("fallback")));
        var sut = new FallbackLlmProvider("costly", [("deepseek", primary), ("groq", fallback)]);

        var result = await sut.CompleteAsync(Request());

        Assert.Equal("fallback", result.Content);
        Assert.Equal(1, primary.Calls);
        Assert.Equal(1, fallback.Calls);
    }

    [Fact]
    public async Task Falls_back_on_empty_content_too()
    {
        var primary = new StubProvider(_ => throw new LlmProviderException("deepseek", "empty", isEmptyContent: true));
        var fallback = new StubProvider(_ => Task.FromResult(Response("fallback")));
        var sut = new FallbackLlmProvider("cheap", [("deepseek", primary), ("groq", fallback)]);

        var result = await sut.CompleteAsync(Request());

        Assert.Equal("fallback", result.Content);
    }

    [Fact]
    public async Task All_providers_failing_transiently_throws_a_transient_tier_exhausted_exception()
    {
        var primary = new StubProvider(_ => throw new LlmProviderException("deepseek", "down", 503));
        var fallback = new StubProvider(_ => throw new LlmProviderException("groq", "also down", 503));
        var sut = new FallbackLlmProvider("cheap", [("deepseek", primary), ("groq", fallback)]);

        var ex = await Assert.ThrowsAsync<LlmTierExhaustedException>(() => sut.CompleteAsync(Request()));

        Assert.Equal("cheap", ex.Tier);
        Assert.True(ex.Transient);
        Assert.Equal(1, primary.Calls);
        Assert.Equal(1, fallback.Calls);
    }

    [Fact]
    public async Task Ends_on_a_permanent_error_marks_the_tier_exhaustion_as_permanent()
    {
        var primary = new StubProvider(_ => throw new LlmProviderException("deepseek", "rate limited", 429));
        var fallback = new StubProvider(_ => throw new LlmProviderException("groq", "bad key", 401));
        var sut = new FallbackLlmProvider("cheap", [("deepseek", primary), ("groq", fallback)]);

        var ex = await Assert.ThrowsAsync<LlmTierExhaustedException>(() => sut.CompleteAsync(Request()));

        Assert.False(ex.Transient);
    }

    [Fact]
    public async Task Single_provider_chain_still_works_and_surfaces_its_failure()
    {
        var only = new StubProvider(_ => throw new LlmProviderException("deepseek", "down", 500));
        var sut = new FallbackLlmProvider("cheap", [("deepseek", only)]);

        var ex = await Assert.ThrowsAsync<LlmTierExhaustedException>(() => sut.CompleteAsync(Request()));

        Assert.True(ex.Transient);
        Assert.Equal(1, only.Calls);
    }

    [Fact]
    public void Requires_at_least_one_provider()
    {
        Assert.Throws<ArgumentException>(() => new FallbackLlmProvider("cheap", []));
    }

    private static LlmRequest Request() => new()
    {
        ModelRole = "coding",
        Messages = [new LlmMessage { Role = "user", Content = "hi" }]
    };

    private static LlmResponse Response(string content) => new() { Content = content };

    private sealed class StubProvider(Func<LlmRequest, Task<LlmResponse>> handler) : ILlmProvider
    {
        public int Calls { get; private set; }

        public Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken cancellationToken = default)
        {
            Calls++;
            return handler(request);
        }
    }
}
