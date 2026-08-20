using System.Net;
using AutoCoder.Abstractions.Config;
using AutoCoder.Core.Resilience;

namespace AutoCoder.Tests;

public sealed class TransientRetryTests
{
    private static readonly ResilienceOptions Fast = new()
    {
        MaxAttempts = 3,
        BaseDelayMs = 0
    };

    [Fact]
    public async Task Succeeds_on_third_attempt()
    {
        var n = 0;
        var value = await TransientRetry.RunAsync("test.ok", _ =>
        {
            n++;
            if (n < 3)
                throw new HttpRequestException("blip");
            return Task.FromResult(42);
        }, CancellationToken.None, Fast);

        Assert.Equal(42, value);
        Assert.Equal(3, n);
    }

    [Fact]
    public async Task Exhausted_retries_wrap_the_last_error()
    {
        var n = 0;
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            TransientRetry.RunAsync<int>("test.fail", _ =>
            {
                n++;
                throw new HttpRequestException("still down");
            }, CancellationToken.None, Fast));

        Assert.Equal(3, n);
        Assert.Contains("failed after 3", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.IsType<HttpRequestException>(ex.InnerException);
    }

    [Fact]
    public async Task Does_not_retry_http_400()
    {
        var n = 0;
        using var response = await TransientRetry.SendAsync("test.400", _ =>
        {
            n++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent("nope")
            });
        }, CancellationToken.None, Fast);

        Assert.Equal(1, n);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Retries_http_429_then_succeeds()
    {
        var n = 0;
        using var response = await TransientRetry.SendAsync("test.429", _ =>
        {
            n++;
            if (n < 3)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.TooManyRequests)
                {
                    Content = new StringContent("slow down")
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("ok")
            });
        }, CancellationToken.None, Fast);

        Assert.Equal(3, n);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Cancellation_is_not_retried()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var n = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            TransientRetry.RunAsync("test.cancel", _ =>
            {
                n++;
                return Task.FromResult(1);
            }, cts.Token, Fast));
        Assert.Equal(0, n);
    }

    [Fact]
    public async Task Does_not_retry_invalid_operation()
    {
        var n = 0;
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            TransientRetry.RunAsync<int>("test.logic", _ =>
            {
                n++;
                throw new InvalidOperationException("empty content");
            }, CancellationToken.None, Fast));
        Assert.Equal(1, n);
    }

    [Theory]
    [InlineData("fatal: unable to access 'https://github.com/x': Could not resolve host", true)]
    [InlineData("RPC failed; curl 56 Recv failure: Connection reset by peer", true)]
    [InlineData("error: pathspec 'main' did not match any file(s) known to git", false)]
    [InlineData("", false)]
    public void Git_transient_detector(string stderr, bool expected) =>
        Assert.Equal(expected, TransientRetry.IsTransientGit(stderr));

    [Theory]
    [InlineData("Cannot connect to the Docker daemon at unix:///var/run/docker.sock", true)]
    [InlineData("error during connect: Get http://docker: failed to connect", true)]
    [InlineData("error CS1001: Identifier expected", false)]
    [InlineData("FAIL src/app.test.js", false)]
    public void Docker_transient_detector(string stderr, bool expected) =>
        Assert.Equal(expected, TransientRetry.IsTransientDocker(stderr));
}
