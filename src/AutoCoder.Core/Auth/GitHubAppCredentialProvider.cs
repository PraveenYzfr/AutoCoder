using System.IdentityModel.Tokens.Jwt;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AutoCoder.Abstractions;
using Microsoft.IdentityModel.Tokens;

namespace AutoCoder.Core.Auth;

/// <summary>
/// Enterprise-style GitHub App auth: mint a short-lived installation token.
/// Secrets come from env/files on the VM (or later mounted K8s/OCP secrets) — not from pipeline code.
/// </summary>
public sealed class GitHubAppCredentialProvider : IGitCredentialProvider, IDisposable
{
    private readonly string _appId;
    private readonly string _installationId;
    private readonly string _privateKeyPem;
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private GitCredentials? _cached;
    private readonly object _gate = new();

    public string Mode => "github_app";

    public GitHubAppCredentialProvider(HttpClient? httpClient = null)
    {
        _appId = Environment.GetEnvironmentVariable("GITHUB_APP_ID")
            ?? throw new InvalidOperationException("GITHUB_APP_ID is required for github_app auth.");
        _installationId = Environment.GetEnvironmentVariable("GITHUB_APP_INSTALLATION_ID")
            ?? throw new InvalidOperationException("GITHUB_APP_INSTALLATION_ID is required for github_app auth.");

        var keyPath = Environment.GetEnvironmentVariable("GITHUB_APP_PRIVATE_KEY_PATH");
        var keyInline = Environment.GetEnvironmentVariable("GITHUB_APP_PRIVATE_KEY");
        if (!string.IsNullOrWhiteSpace(keyPath) && File.Exists(keyPath))
            _privateKeyPem = File.ReadAllText(keyPath);
        else if (!string.IsNullOrWhiteSpace(keyInline))
            _privateKeyPem = keyInline.Replace("\\n", "\n");
        else
        {
            throw new InvalidOperationException(
                "Provide GITHUB_APP_PRIVATE_KEY_PATH or GITHUB_APP_PRIVATE_KEY for github_app auth.");
        }

        _ownsHttp = httpClient is null;
        _http = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("AutoCoder");
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
    }

    public async Task<GitCredentials> GetCredentialsAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (_cached?.ExpiresAt is { } exp && exp > DateTimeOffset.UtcNow.AddMinutes(2))
                return _cached;
        }

        var jwt = CreateAppJwt();
        using var req = new HttpRequestMessage(
            HttpMethod.Post,
            $"https://api.github.com/app/installations/{_installationId}/access_tokens");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", jwt);
        req.Content = new StringContent("{}", Encoding.UTF8, "application/json");

        using var response = await _http.SendAsync(req, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"GitHub App installation token failed {(int)response.StatusCode}: {Truncate(raw, 400)}");
        }

        using var doc = JsonDocument.Parse(raw);
        var token = doc.RootElement.GetProperty("token").GetString()
            ?? throw new InvalidOperationException("GitHub App token response missing token.");
        DateTimeOffset? expires = null;
        if (doc.RootElement.TryGetProperty("expires_at", out var expEl)
            && DateTimeOffset.TryParse(expEl.GetString(), out var parsed))
        {
            expires = parsed;
        }

        var creds = new GitCredentials
        {
            AccessToken = token,
            ExpiresAt = expires ?? DateTimeOffset.UtcNow.AddMinutes(50)
        };

        lock (_gate)
            _cached = creds;

        Console.WriteLine($"[auth] GitHub App installation token acquired (expires {creds.ExpiresAt:u}).");
        return creds;
    }

    private string CreateAppJwt()
    {
        using var rsa = RSA.Create();
        rsa.ImportFromPem(_privateKeyPem);

        var securityKey = new RsaSecurityKey(rsa);
        var creds = new SigningCredentials(securityKey, SecurityAlgorithms.RsaSha256);
        var now = DateTime.UtcNow;

        var token = new JwtSecurityToken(
            issuer: _appId,
            claims: null,
            notBefore: now.AddSeconds(-60),
            expires: now.AddMinutes(9),
            signingCredentials: creds);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    public void Dispose()
    {
        if (_ownsHttp)
            _http.Dispose();
    }

    private static string Truncate(string text, int max) =>
        text.Length <= max ? text : text[..max] + "…";
}
