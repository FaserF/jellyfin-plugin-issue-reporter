using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.IssueReporter.Configuration;

namespace Jellyfin.Plugin.IssueReporter.Services;

public sealed class GitHubAuthException : Exception
{
    public GitHubAuthException(string message) : base(message)
    {
    }
}

public sealed class GitHubAuthService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly SemaphoreSlim _appLock = new(1, 1);
    private string? _cachedAppToken;
    private DateTimeOffset _appTokenExpiresAt = DateTimeOffset.MinValue;
    private string? _appOwner;
    private readonly HashSet<string> _installedRepos = new(StringComparer.OrdinalIgnoreCase);

    public GitHubAuthService(IHttpClientFactory httpClientFactory)
    {
        _httpClientFactory = httpClientFactory;
    }

    public string? GetAuthenticatedOwner(PluginConfiguration config)
    {
        if (!string.IsNullOrWhiteSpace(_appOwner))
        {
            return _appOwner;
        }

        if (!string.IsNullOrWhiteSpace(config.GitHubUser))
        {
            return config.GitHubUser.Trim();
        }

        return string.IsNullOrWhiteSpace(config.FallbackUser) ? null : config.FallbackUser.Trim();
    }

    public string GetActorDescription(PluginConfiguration config)
    {
        return config.AuthMethod switch
        {
            "github_app" => $"github-app ({GetAuthenticatedOwner(config) ?? config.AppId})",
            "device_flow" => $"oauth ({GetAuthenticatedOwner(config) ?? "user"})",
            "pat" => $"pat ({GetAuthenticatedOwner(config) ?? "user"})",
            _ => "unconfigured",
        };
    }

    public void InvalidateAppToken()
    {
        _cachedAppToken = null;
        _appTokenExpiresAt = DateTimeOffset.MinValue;
    }

    public async Task<(string Token, string Actor)> GetTokenForRepoAsync(
        PluginConfiguration config,
        string owner,
        string repo,
        CancellationToken ct = default)
    {
        var fullRepo = $"{owner}/{repo}";
        if (config.AuthMethod == "github_app")
        {
            var appToken = await GetAppInstallationTokenAsync(config, ct);
            if (_installedRepos.Count == 0 || _installedRepos.Contains(fullRepo))
            {
                return (appToken, "github_app");
            }

            if (!string.IsNullOrWhiteSpace(config.FallbackToken))
            {
                return (config.FallbackToken.Trim(), "fallback_user");
            }

            throw new GitHubAuthException($"GitHub App not installed on {fullRepo} and no fallback token configured.");
        }

        if (!string.IsNullOrWhiteSpace(config.GitHubToken))
        {
            return (config.GitHubToken.Trim(), config.AuthMethod);
        }

        throw new GitHubAuthException("No GitHub credentials configured.");
    }

    public async Task<string?> GetAnyReadTokenAsync(PluginConfiguration config, CancellationToken ct = default)
    {
        if (config.AuthMethod == "github_app" && !string.IsNullOrWhiteSpace(config.PrivateKeyPem))
        {
            try
            {
                return await GetAppInstallationTokenAsync(config, ct);
            }
            catch (GitHubAuthException)
            {
                // Fall back to user token or anonymous read
            }
        }

        if (!string.IsNullOrWhiteSpace(config.GitHubToken))
        {
            return config.GitHubToken.Trim();
        }

        return string.IsNullOrWhiteSpace(config.FallbackToken) ? null : config.FallbackToken.Trim();
    }

    public async Task<string> ValidatePatAsync(string token, CancellationToken ct = default)
    {
        var client = _httpClientFactory.CreateClient();
        using var req = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/user");
        req.Headers.UserAgent.ParseAdd("jellyfin-plugin-issue-reporter");
        req.Headers.Accept.ParseAdd("application/vnd.github+json");
        req.Headers.Authorization = new AuthenticationHeaderValue("token", token.Trim());
        using var resp = await client.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode)
        {
            throw new GitHubAuthException($"PAT validation failed ({(int)resp.StatusCode}).");
        }

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        return doc.RootElement.TryGetProperty("login", out var l) ? (l.GetString() ?? string.Empty) : string.Empty;
    }

    public async Task<(string DeviceCode, string UserCode, string VerificationUri, int Interval)> StartDeviceFlowAsync(
        string clientId,
        CancellationToken ct = default)
    {
        var client = _httpClientFactory.CreateClient();
        using var req = new HttpRequestMessage(HttpMethod.Post, "https://github.com/login/device/code");
        req.Headers.Accept.ParseAdd("application/json");
        req.Content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = clientId.Trim(),
            ["scope"] = "public_repo read:user",
        });
        using var resp = await client.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode)
        {
            throw new GitHubAuthException($"Device flow start failed ({(int)resp.StatusCode}).");
        }

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        var root = doc.RootElement;
        return (
            root.GetProperty("device_code").GetString() ?? string.Empty,
            root.GetProperty("user_code").GetString() ?? string.Empty,
            root.GetProperty("verification_uri").GetString() ?? "https://github.com/login/device",
            root.TryGetProperty("interval", out var i) ? i.GetInt32() : 5
        );
    }

    public async Task<(string Status, string? AccessToken)> PollDeviceFlowOnceAsync(
        string clientId,
        string deviceCode,
        CancellationToken ct = default)
    {
        var client = _httpClientFactory.CreateClient();
        using var req = new HttpRequestMessage(HttpMethod.Post, "https://github.com/login/oauth/access_token");
        req.Headers.Accept.ParseAdd("application/json");
        req.Content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = clientId.Trim(),
            ["device_code"] = deviceCode.Trim(),
            ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code",
        });
        using var resp = await client.SendAsync(req, ct);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        var root = doc.RootElement;
        if (root.TryGetProperty("access_token", out var tok) && !string.IsNullOrEmpty(tok.GetString()))
        {
            return ("authorized", tok.GetString());
        }

        var err = root.TryGetProperty("error", out var e) ? (e.GetString() ?? "pending") : "pending";
        return (err, null);
    }

    public static string CreateAppJwt(string appId, string pem)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var headerJson = JsonSerializer.Serialize(new { alg = "RS256", typ = "JWT" });
        var payloadJson = JsonSerializer.Serialize(new { iat = now - 60, exp = now + 540, iss = appId.Trim() });
        var unsigned = $"{Base64UrlEncode(Encoding.UTF8.GetBytes(headerJson))}.{Base64UrlEncode(Encoding.UTF8.GetBytes(payloadJson))}";

        using var rsa = RSA.Create();
        rsa.ImportFromPem(pem.AsSpan());
        var sig = rsa.SignData(Encoding.ASCII.GetBytes(unsigned), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return $"{unsigned}.{Base64UrlEncode(sig)}";
    }

    private async Task<string> GetAppInstallationTokenAsync(PluginConfiguration config, CancellationToken ct)
    {
        await _appLock.WaitAsync(ct);
        try
        {
            if (!string.IsNullOrEmpty(_cachedAppToken) && DateTimeOffset.UtcNow < _appTokenExpiresAt.AddMinutes(-2))
            {
                return _cachedAppToken;
            }

            if (string.IsNullOrWhiteSpace(config.AppId) || string.IsNullOrWhiteSpace(config.PrivateKeyPem))
            {
                throw new GitHubAuthException("GitHub App ID or Private Key PEM missing.");
            }

            var jwt = CreateAppJwt(config.AppId, config.PrivateKeyPem);
            var client = _httpClientFactory.CreateClient();
            var instId = config.InstallationId.Trim();

            if (string.IsNullOrEmpty(instId))
            {
                using var instReq = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/app/installations");
                instReq.Headers.UserAgent.ParseAdd("jellyfin-plugin-issue-reporter");
                instReq.Headers.Accept.ParseAdd("application/vnd.github+json");
                instReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", jwt);
                using var instResp = await client.SendAsync(instReq, ct);
                if (!instResp.IsSuccessStatusCode)
                {
                    throw new GitHubAuthException($"Failed listing GitHub App installations ({(int)instResp.StatusCode}).");
                }

                using var instDoc = JsonDocument.Parse(await instResp.Content.ReadAsStringAsync(ct));
                if (instDoc.RootElement.GetArrayLength() == 0)
                {
                    throw new GitHubAuthException("No GitHub App installations found.");
                }

                var first = instDoc.RootElement[0];
                instId = first.GetProperty("id").GetInt64().ToString();
                if (first.TryGetProperty("account", out var acc) && acc.TryGetProperty("login", out var login))
                {
                    _appOwner = login.GetString();
                }
            }

            using var tokReq = new HttpRequestMessage(HttpMethod.Post, $"https://api.github.com/app/installations/{instId}/access_tokens");
            tokReq.Headers.UserAgent.ParseAdd("jellyfin-plugin-issue-reporter");
            tokReq.Headers.Accept.ParseAdd("application/vnd.github+json");
            tokReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", jwt);
            using var tokResp = await client.SendAsync(tokReq, ct);
            if (!tokResp.IsSuccessStatusCode)
            {
                throw new GitHubAuthException($"Failed creating installation token ({(int)tokResp.StatusCode}).");
            }

            using var tokDoc = JsonDocument.Parse(await tokResp.Content.ReadAsStringAsync(ct));
            _cachedAppToken = tokDoc.RootElement.GetProperty("token").GetString()
                ?? throw new GitHubAuthException("Empty installation token.");
            _appTokenExpiresAt = tokDoc.RootElement.TryGetProperty("expires_at", out var exp) &&
                                 DateTimeOffset.TryParse(exp.GetString(), out var parsedExp)
                ? parsedExp
                : DateTimeOffset.UtcNow.AddMinutes(50);

            await RefreshInstalledReposAsync(_cachedAppToken, ct);
            return _cachedAppToken;
        }
        finally
        {
            _appLock.Release();
        }
    }

    private async Task RefreshInstalledReposAsync(string token, CancellationToken ct)
    {
        try
        {
            var client = _httpClientFactory.CreateClient();
            using var req = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/installation/repositories?per_page=100");
            req.Headers.UserAgent.ParseAdd("jellyfin-plugin-issue-reporter");
            req.Headers.Accept.ParseAdd("application/vnd.github+json");
            req.Headers.Authorization = new AuthenticationHeaderValue("token", token);
            using var resp = await client.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode)
            {
                return;
            }

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            if (!doc.RootElement.TryGetProperty("repositories", out var repos))
            {
                return;
            }

            _installedRepos.Clear();
            foreach (var r in repos.EnumerateArray())
            {
                var full = r.TryGetProperty("full_name", out var fn) ? fn.GetString() : null;
                if (!string.IsNullOrEmpty(full))
                {
                    _installedRepos.Add(full);
                }

                if (string.IsNullOrEmpty(_appOwner) && r.TryGetProperty("owner", out var ow) && ow.TryGetProperty("login", out var ol))
                {
                    _appOwner = ol.GetString();
                }
            }
        }
        catch (Exception)
        {
            // Non-fatal; fallback routing handles missing list
        }
    }

    private static string Base64UrlEncode(byte[] input)
    {
        return Convert.ToBase64String(input).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }
}
