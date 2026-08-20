using System.Net;
using System.Net.Sockets;
using AutoCoder.Abstractions.Config;
using AutoCoder.Core.Logging;
using Microsoft.Extensions.Logging;

namespace AutoCoder.Core.Resilience;

public sealed class TransientFailureException : Exception
{
    public string Operation { get; }
    public int? StatusCode { get; }
    public int? RetryAfterMs { get; }

    public TransientFailureException(
        string operation,
        string message,
        int? statusCode = null,
        int? retryAfterMs = null,
        Exception? inner = null)
        : base(message, inner)
    {
        Operation = operation;
        StatusCode = statusCode;
        RetryAfterMs = retryAfterMs;
    }
}

/// <summary>Exponential backoff for LLM/Jira/GitHub/Docker blips. Does not retry 4xx except 408/429.</summary>
public static class TransientRetry
{
    private static readonly string[] GitNeedles =
    [
        "could not resolve",
        "unable to access",
        "connection reset",
        "connection timed out",
        "timed out",
        "tls handshake",
        "ssl certificate",
        "rpc failed",
        "early eof",
        "the remote hung up",
        "could not read from remote",
        "failed to connect",
        "network is unreachable"
    ];

    private static readonly string[] DockerNeedles =
    [
        "cannot connect to the docker daemon",
        "error during connect",
        "is the docker daemon running",
        "failed to connect to the docker",
        "connection refused",
        "connection reset by peer"
    ];

    private static ResilienceOptions _options = new();

    public static void Configure(ResilienceOptions? options) =>
        _options = options ?? new ResilienceOptions();

    public static Task<T> RunAsync<T>(
        string operation,
        Func<CancellationToken, Task<T>> action,
        CancellationToken cancellationToken = default) =>
        RunAsync(operation, action, cancellationToken, _options);

    public static async Task<T> RunAsync<T>(
        string operation,
        Func<CancellationToken, Task<T>> action,
        CancellationToken cancellationToken,
        ResilienceOptions options)
    {
        var max = Math.Max(1, options.MaxAttempts);
        var delay = TimeSpan.FromMilliseconds(Math.Max(0, options.BaseDelayMs));
        Exception? last = null;

        for (var attempt = 1; attempt <= max; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return await action(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (IsTransient(ex) && attempt < max)
            {
                last = ex;
                var wait = DelayFor(delay, attempt, ex);
                RunLog.Event(
                    "retry.attempt",
                    level: LogLevel.Warning,
                    fields:
                    [
                        ("op", operation),
                        ("attempt", attempt),
                        ("max", max),
                        ("waitMs", (int)wait.TotalMilliseconds),
                        ("reason", Truncate(ex.Message, 240))
                    ]);
                await Task.Delay(wait, cancellationToken);
            }
            catch (Exception ex) when (IsTransient(ex))
            {
                last = ex;
            }
        }

        throw new InvalidOperationException(
            $"{operation} failed after {max} attempt(s): {last?.Message}", last);
    }

    public static Task<HttpResponseMessage> SendAsync(
        string operation,
        Func<CancellationToken, Task<HttpResponseMessage>> send,
        CancellationToken cancellationToken = default) =>
        SendAsync(operation, send, cancellationToken, _options);

    public static Task<HttpResponseMessage> SendAsync(
        string operation,
        Func<CancellationToken, Task<HttpResponseMessage>> send,
        CancellationToken cancellationToken,
        ResilienceOptions options)
    {
        return RunAsync(operation, async ct =>
        {
            var response = await send(ct);
            if (response.IsSuccessStatusCode || !IsTransientStatus(response.StatusCode))
                return response;

            var retryAfterMs = ParseRetryAfterMs(response);
            var body = await response.Content.ReadAsStringAsync(ct);
            var code = (int)response.StatusCode;
            response.Dispose();
            throw new TransientFailureException(
                operation,
                $"{operation} HTTP {code}: {Truncate(body, 240)}",
                code,
                retryAfterMs);
        }, cancellationToken, options);
    }

    public static bool IsTransientStatus(HttpStatusCode status) =>
        status is HttpStatusCode.RequestTimeout
            or HttpStatusCode.TooManyRequests
            or HttpStatusCode.InternalServerError
            or HttpStatusCode.BadGateway
            or HttpStatusCode.ServiceUnavailable
            or HttpStatusCode.GatewayTimeout
            or (HttpStatusCode)529;

    public static bool IsTransientGit(string stderr)
    {
        if (string.IsNullOrWhiteSpace(stderr))
            return false;
        foreach (var n in GitNeedles)
        {
            if (stderr.Contains(n, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    public static bool IsTransientDocker(string stderr)
    {
        if (string.IsNullOrWhiteSpace(stderr))
            return false;
        foreach (var n in DockerNeedles)
        {
            if (stderr.Contains(n, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    public static bool IsTransient(Exception ex)
    {
        for (var cur = ex; cur is not null; cur = cur.InnerException)
        {
            switch (cur)
            {
                case TransientFailureException:
                case HttpRequestException:
                case SocketException:
                case IOException:
                case TimeoutException:
                case TaskCanceledException:
                    return true;
            }
        }

        return false;
    }

    private static TimeSpan DelayFor(TimeSpan baseDelay, int attempt, Exception ex)
    {
        if (ex is TransientFailureException { RetryAfterMs: > 0 } tfe)
            return TimeSpan.FromMilliseconds(tfe.RetryAfterMs.Value);

        if (baseDelay <= TimeSpan.Zero)
            return TimeSpan.Zero;

        var exp = Math.Pow(2, attempt - 1);
        var jitterMs = Random.Shared.Next(0, 50);
        return TimeSpan.FromMilliseconds(baseDelay.TotalMilliseconds * exp + jitterMs);
    }

    private static int? ParseRetryAfterMs(HttpResponseMessage response)
    {
        var ra = response.Headers.RetryAfter;
        if (ra is null)
            return null;

        double ms;
        if (ra.Delta is TimeSpan delta)
            ms = delta.TotalMilliseconds;
        else if (ra.Date is DateTimeOffset date)
            ms = (date - DateTimeOffset.UtcNow).TotalMilliseconds;
        else
            return null;

        return (int)Math.Clamp(ms, 0, 30_000);
    }

    private static string Truncate(string text, int max) =>
        text.Length <= max ? text : text[..max] + "…";
}
