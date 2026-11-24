using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.IssueReporter.Configuration;

namespace Jellyfin.Plugin.IssueReporter.Services;

public class GitHubApiException : Exception
{
    public GitHubApiException(string message, int statusCode = 0) : base(message)
    {
        StatusCode = statusCode;
    }

    public int StatusCode { get; }
}

public sealed class GitHubNetworkException : GitHubApiException
{
    public GitHubNetworkException(string message) : base(message, 503)
    {
    }
}

public sealed class GitHubRateLimitException : GitHubApiException
{
    public GitHubRateLimitException(string message, DateTimeOffset? resetAt = null) : base(message, 429)
    {
        ResetAt = resetAt;
    }

    public DateTimeOffset? ResetAt { get; }
}

public sealed class GitHubIssueRecord
{
    public int Number { get; set; }

    public string HtmlUrl { get; set; } = string.Empty;

    public string State { get; set; } = "open";

    public string Title { get; set; } = string.Empty;

    public string Body { get; set; } = string.Empty;

    public DateTimeOffset? ClosedAt { get; set; }
}

public sealed class GitHubApiClient
{
    private sealed record CachedValue<T>(DateTimeOffset FetchedAt, T Data, string? ETag);

    private const int CoreReserve = 20;
    private const int SearchReserve = 2;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly GitHubAuthService _auth;

    private readonly ConcurrentDictionary<string, CachedValue<string?>> _releaseCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, CachedValue<List<(string Name, string Content)>>> _templateCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, CachedValue<(List<GitHubIssueRecord> Issues, bool MayHaveMore)>> _issuesCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, CachedValue<List<string>>> _commentsCache = new(StringComparer.OrdinalIgnoreCase);

    private int? _coreRemaining;
    private long? _coreResetEpoch;
    private int? _searchRemaining;
    private long? _searchResetEpoch;
    private int _consecutiveFailures;
    private DateTimeOffset? _circuitOpenUntil;

    public GitHubApiClient(IHttpClientFactory httpClientFactory, GitHubAuthService auth)
    {
        _httpClientFactory = httpClientFactory;
        _auth = auth;
    }

    public int? RateLimitRemaining => _coreRemaining;

    public bool IsCircuitOpen
    {
        get
        {
            if (_circuitOpenUntil is null)
            {
                return false;
            }

            if (DateTimeOffset.UtcNow >= _circuitOpenUntil.Value)
            {
                _circuitOpenUntil = null;
                _consecutiveFailures = 0;
                return false;
            }

            return true;
        }
    }

    public async Task<string?> GetLatestReleaseAsync(PluginConfiguration config, string owner, string repo, CancellationToken ct = default)
    {
        var key = $"{owner}/{repo}";
        if (_releaseCache.TryGetValue(key, out var cached) && DateTimeOffset.UtcNow - cached.FetchedAt < TimeSpan.FromHours(6))
        {
            return cached.Data;
        }

        try
        {
            var token = await _auth.GetAnyReadTokenAsync(config, ct);
            var (status, body, etag) = await SendGetAsync($"https://api.github.com/repos/{owner}/{repo}/releases/latest", token, cached?.ETag, "core", ct);
            if (status == HttpStatusCode.NotModified && cached is not null)
            {
                _releaseCache[key] = cached with { FetchedAt = DateTimeOffset.UtcNow };
                return cached.Data;
            }

            string? tag = null;
            if (status == HttpStatusCode.OK && !string.IsNullOrEmpty(body))
            {
                using var doc = JsonDocument.Parse(body);
                tag = doc.RootElement.TryGetProperty("tag_name", out var t) ? t.GetString() : null;
            }

            _releaseCache[key] = new CachedValue<string?>(DateTimeOffset.UtcNow, tag, etag);
            return tag;
        }
        catch (GitHubApiException) when (cached is not null)
        {
            return cached.Data;
        }
    }

    public async Task<List<(string Name, string Content)>> FetchBugTemplatesAsync(PluginConfiguration config, string owner, string repo, CancellationToken ct = default)
    {
        var key = $"{owner}/{repo}";
        if (_templateCache.TryGetValue(key, out var cached) && DateTimeOffset.UtcNow - cached.FetchedAt < TimeSpan.FromHours(24))
        {
            return cached.Data;
        }

        var result = new List<(string Name, string Content)>();
        try
        {
            var token = await _auth.GetAnyReadTokenAsync(config, ct);
            var (status, body, etag) = await SendGetAsync($"https://api.github.com/repos/{owner}/{repo}/contents/.github/ISSUE_TEMPLATE", token, cached?.ETag, "core", ct);
            if (status == HttpStatusCode.NotModified && cached is not null)
            {
                _templateCache[key] = cached with { FetchedAt = DateTimeOffset.UtcNow };
                return cached.Data;
            }

            if (status == HttpStatusCode.OK && !string.IsNullOrEmpty(body))
            {
                using var doc = JsonDocument.Parse(body);
                var candidates = doc.RootElement.EnumerateArray()
                    .Where(i => i.TryGetProperty("type", out var t) && t.GetString() == "file")
                    .Select(i => (Name: i.GetProperty("name").GetString() ?? string.Empty, Path: i.GetProperty("path").GetString() ?? string.Empty))
                    .Where(i => (i.Name.EndsWith(".yml", StringComparison.OrdinalIgnoreCase) || i.Name.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase) || i.Name.EndsWith(".md", StringComparison.OrdinalIgnoreCase)) && !i.Name.Equals("config.yml", StringComparison.OrdinalIgnoreCase))
                    .OrderBy(i => i.Name.Contains("bug", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                    .ThenBy(i => i.Name.EndsWith(".md", StringComparison.OrdinalIgnoreCase) ? 1 : 0)
                    .Take(3);

                foreach (var item in candidates)
                {
                    var (fStatus, fBody, _) = await SendGetAsync($"https://api.github.com/repos/{owner}/{repo}/contents/{item.Path}", token, null, "core", ct);
                    if (fStatus != HttpStatusCode.OK || string.IsNullOrEmpty(fBody))
                    {
                        continue;
                    }

                    using var fDoc = JsonDocument.Parse(fBody);
                    if (fDoc.RootElement.TryGetProperty("content", out var c))
                    {
                        var b64 = (c.GetString() ?? string.Empty).Replace("\n", string.Empty, StringComparison.Ordinal);
                        var raw = Encoding.UTF8.GetString(Convert.FromBase64String(b64));
                        result.Add((item.Name, raw));
                    }
                }
            }

            _templateCache[key] = new CachedValue<List<(string Name, string Content)>>(DateTimeOffset.UtcNow, result, etag);
            return result;
        }
        catch (GitHubApiException) when (cached is not null)
        {
            return cached.Data;
        }
    }

    public async Task<(List<GitHubIssueRecord> Issues, bool MayHaveMore)> GetRepoIssuesAsync(PluginConfiguration config, string owner, string repo, CancellationToken ct = default)
    {
        var key = $"{owner}/{repo}";
        if (_issuesCache.TryGetValue(key, out var cached) && DateTimeOffset.UtcNow - cached.FetchedAt < TimeSpan.FromMinutes(15))
        {
            return cached.Data;
        }

        try
        {
            var token = await _auth.GetAnyReadTokenAsync(config, ct);
            var (status, body, etag) = await SendGetAsync(
                $"https://api.github.com/repos/{owner}/{repo}/issues?state=all&sort=updated&direction=desc&per_page=100",
                token,
                cached?.ETag,
                "core",
                ct);

            if (status == HttpStatusCode.NotModified && cached is not null)
            {
                _issuesCache[key] = cached with { FetchedAt = DateTimeOffset.UtcNow };
                return cached.Data;
            }

            var list = new List<GitHubIssueRecord>();
            var rawCount = 0;
            if (status == HttpStatusCode.OK && !string.IsNullOrEmpty(body))
            {
                using var doc = JsonDocument.Parse(body);
                foreach (var item in doc.RootElement.EnumerateArray())
                {
                    rawCount++;
                    if (item.TryGetProperty("pull_request", out _))
                    {
                        continue;
                    }

                    list.Add(ParseIssue(item));
                }
            }

            var pair = (list, rawCount >= 100);
            _issuesCache[key] = new CachedValue<(List<GitHubIssueRecord>, bool)>(DateTimeOffset.UtcNow, pair, etag);
            return pair;
        }
        catch (GitHubApiException) when (cached is not null)
        {
            return cached.Data;
        }
    }

    public async Task<List<GitHubIssueRecord>> SearchIssuesAsync(PluginConfiguration config, string owner, string repo, string query, CancellationToken ct = default)
    {
        var token = await _auth.GetAnyReadTokenAsync(config, ct);
        var q = Uri.EscapeDataString($"repo:{owner}/{repo} is:issue {query}".Trim());
        var (status, body, _) = await SendGetAsync($"https://api.github.com/search/issues?q={q}&per_page=15&sort=updated&order=desc", token, null, "search", ct);
        var list = new List<GitHubIssueRecord>();
        if (status == HttpStatusCode.OK && !string.IsNullOrEmpty(body))
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("items", out var items))
            {
                foreach (var item in items.EnumerateArray())
                {
                    list.Add(ParseIssue(item));
                }
            }
        }

        return list;
    }

    public async Task<List<string>> GetIssueCommentsAsync(PluginConfiguration config, string owner, string repo, int issueNumber, CancellationToken ct = default)
    {
        var key = $"{owner}/{repo}#{issueNumber}";
        if (_commentsCache.TryGetValue(key, out var cached) && DateTimeOffset.UtcNow - cached.FetchedAt < TimeSpan.FromMinutes(10))
        {
            return cached.Data;
        }

        try
        {
            var token = await _auth.GetAnyReadTokenAsync(config, ct);
            var (status, body, etag) = await SendGetAsync($"https://api.github.com/repos/{owner}/{repo}/issues/{issueNumber}/comments?per_page=50", token, cached?.ETag, "core", ct);
            if (status == HttpStatusCode.NotModified && cached is not null)
            {
                _commentsCache[key] = cached with { FetchedAt = DateTimeOffset.UtcNow };
                return cached.Data;
            }

            var comments = new List<string>();
            if (status == HttpStatusCode.OK && !string.IsNullOrEmpty(body))
            {
                using var doc = JsonDocument.Parse(body);
                foreach (var c in doc.RootElement.EnumerateArray())
                {
                    if (c.TryGetProperty("body", out var b) && b.GetString() is { } text)
                    {
                        comments.Add(text);
                    }
                }
            }

            _commentsCache[key] = new CachedValue<List<string>>(DateTimeOffset.UtcNow, comments, etag);
            return comments;
        }
        catch (GitHubApiException) when (cached is not null)
        {
            return cached.Data;
        }
    }

    public async Task<(int Number, string HtmlUrl)> CreateIssueAsync(
        PluginConfiguration config,
        string owner,
        string repo,
        string title,
        string body,
        IReadOnlyList<string>? labels = null,
        CancellationToken ct = default)
    {
        var (token, _) = await _auth.GetTokenForRepoAsync(config, owner, repo, ct);
        var payload = new Dictionary<string, object> { ["title"] = title, ["body"] = body };
        if (labels is { Count: > 0 })
        {
            payload["labels"] = labels;
        }

        try
        {
            return await PostIssueInternalAsync($"https://api.github.com/repos/{owner}/{repo}/issues", token, payload, ct);
        }
        catch (GitHubApiException ex) when (labels is { Count: > 0 } && ex.StatusCode is 403 or 422)
        {
            payload.Remove("labels");
            return await PostIssueInternalAsync($"https://api.github.com/repos/{owner}/{repo}/issues", token, payload, ct);
        }
    }

    public async Task CreateCommentAsync(PluginConfiguration config, string owner, string repo, int issueNumber, string body, CancellationToken ct = default)
    {
        var (token, _) = await _auth.GetTokenForRepoAsync(config, owner, repo, ct);
        await PostIssueInternalAsync($"https://api.github.com/repos/{owner}/{repo}/issues/{issueNumber}/comments", token, new Dictionary<string, object> { ["body"] = body }, ct);
    }

    private async Task<(int Number, string HtmlUrl)> PostIssueInternalAsync(string url, string token, Dictionary<string, object> payload, CancellationToken ct)
    {
        CheckProactiveLimits("core");
        var client = _httpClientFactory.CreateClient();
        using var req = new HttpRequestMessage(HttpMethod.Post, url);
        req.Headers.UserAgent.ParseAdd("jellyfin-plugin-issue-reporter");
        req.Headers.Accept.ParseAdd("application/vnd.github+json");
        req.Headers.Authorization = new AuthenticationHeaderValue("token", token);
        req.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

        try
        {
            using var resp = await client.SendAsync(req, ct);
            UpdateRateLimits(resp.Headers);
            var text = await resp.Content.ReadAsStringAsync(ct);
            ThrowIfErrorStatus(resp, text, "core");
            RecordSuccess();
            using var doc = JsonDocument.Parse(text);
            var num = doc.RootElement.TryGetProperty("number", out var n) ? n.GetInt32() : 0;
            var html = doc.RootElement.TryGetProperty("html_url", out var h) ? (h.GetString() ?? string.Empty) : string.Empty;
            return (num, html);
        }
        catch (HttpRequestException ex)
        {
            RecordFailure();
            throw new GitHubNetworkException($"Network error posting to GitHub: {ex.Message}");
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            RecordFailure();
            throw new GitHubNetworkException($"Timeout posting to GitHub: {ex.Message}");
        }
    }

    private async Task<(HttpStatusCode Status, string Body, string? ETag)> SendGetAsync(
        string url,
        string? token,
        string? ifNoneMatch,
        string resource,
        CancellationToken ct)
    {
        CheckProactiveLimits(resource);
        var client = _httpClientFactory.CreateClient();
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.UserAgent.ParseAdd("jellyfin-plugin-issue-reporter");
        req.Headers.Accept.ParseAdd("application/vnd.github+json");
        if (!string.IsNullOrEmpty(token))
        {
            req.Headers.Authorization = new AuthenticationHeaderValue("token", token);
        }

        if (!string.IsNullOrEmpty(ifNoneMatch))
        {
            req.Headers.TryAddWithoutValidation("If-None-Match", ifNoneMatch);
        }

        try
        {
            using var resp = await client.SendAsync(req, ct);
            UpdateRateLimits(resp.Headers);
            if (resp.StatusCode is HttpStatusCode.NotModified or HttpStatusCode.NotFound)
            {
                RecordSuccess();
                return (resp.StatusCode, string.Empty, resp.Headers.ETag?.Tag);
            }

            var text = await resp.Content.ReadAsStringAsync(ct);
            ThrowIfErrorStatus(resp, text, resource);
            RecordSuccess();
            return (resp.StatusCode, text, resp.Headers.ETag?.Tag);
        }
        catch (HttpRequestException ex)
        {
            RecordFailure();
            throw new GitHubNetworkException($"Network error calling GitHub: {ex.Message}");
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            RecordFailure();
            throw new GitHubNetworkException($"Timeout calling GitHub: {ex.Message}");
        }
    }

    private void CheckProactiveLimits(string resource)
    {
        if (IsCircuitOpen)
        {
            throw new GitHubNetworkException("GitHub API circuit breaker is open.");
        }

        var now = DateTimeOffset.UtcNow;
        if (resource == "search" && _searchRemaining < SearchReserve && _searchResetEpoch is not null)
        {
            var rst = DateTimeOffset.FromUnixTimeSeconds(_searchResetEpoch.Value);
            if (now < rst)
            {
                throw new GitHubRateLimitException("GitHub Search API quota reserve reached.", rst);
            }
        }
        else if (resource != "search" && _coreRemaining < CoreReserve && _coreResetEpoch is not null)
        {
            var rst = DateTimeOffset.FromUnixTimeSeconds(_coreResetEpoch.Value);
            if (now < rst)
            {
                throw new GitHubRateLimitException("GitHub Core API quota reserve reached.", rst);
            }
        }
    }

    private void UpdateRateLimits(HttpResponseHeaders headers)
    {
        var resource = headers.TryGetValues("X-RateLimit-Resource", out var resVals)
            ? (resVals.FirstOrDefault() ?? "core").ToLowerInvariant()
            : "core";
        if (headers.TryGetValues("X-RateLimit-Remaining", out var remVals) && int.TryParse(remVals.FirstOrDefault(), out var rem))
        {
            if (resource == "search")
            {
                _searchRemaining = rem;
            }
            else
            {
                _coreRemaining = rem;
            }
        }

        if (headers.TryGetValues("X-RateLimit-Reset", out var rstVals) && long.TryParse(rstVals.FirstOrDefault(), out var rst))
        {
            if (resource == "search")
            {
                _searchResetEpoch = rst;
            }
            else
            {
                _coreResetEpoch = rst;
            }
        }
    }

    private void ThrowIfErrorStatus(HttpResponseMessage resp, string text, string resource)
    {
        var code = (int)resp.StatusCode;
        if (code < 400)
        {
            return;
        }

        if (code == 429 || (code == 403 && ((resource == "search" && _searchRemaining == 0) || (resource == "core" && _coreRemaining == 0) || resp.Headers.Contains("Retry-After"))))
        {
            var rstEpoch = resource == "search" ? _searchResetEpoch : _coreResetEpoch;
            var resetAt = rstEpoch is not null ? DateTimeOffset.FromUnixTimeSeconds(rstEpoch.Value) : DateTimeOffset.UtcNow.AddMinutes(5);
            throw new GitHubRateLimitException($"GitHub rate limit exhausted ({code}).", resetAt);
        }

        if (code >= 500)
        {
            RecordFailure();
            throw new GitHubNetworkException($"GitHub server error ({code}): {text[..Math.Min(200, text.Length)]}");
        }

        throw new GitHubApiException($"GitHub API error ({code}): {text[..Math.Min(200, text.Length)]}", code);
    }

    private void RecordSuccess()
    {
        _consecutiveFailures = 0;
        _circuitOpenUntil = null;
    }

    private void RecordFailure()
    {
        _consecutiveFailures++;
        if (_consecutiveFailures >= 3)
        {
            _circuitOpenUntil = DateTimeOffset.UtcNow.AddMinutes(5);
        }
    }

    private static GitHubIssueRecord ParseIssue(JsonElement item)
    {
        DateTimeOffset? closedAt = null;
        if (item.TryGetProperty("closed_at", out var c) && DateTimeOffset.TryParse(c.GetString(), out var dt))
        {
            closedAt = dt;
        }

        return new GitHubIssueRecord
        {
            Number = item.GetProperty("number").GetInt32(),
            HtmlUrl = item.TryGetProperty("html_url", out var u) ? (u.GetString() ?? string.Empty) : string.Empty,
            State = item.TryGetProperty("state", out var s) ? (s.GetString() ?? "open") : "open",
            Title = item.TryGetProperty("title", out var t) ? (t.GetString() ?? string.Empty) : string.Empty,
            Body = item.TryGetProperty("body", out var b) ? (b.GetString() ?? string.Empty) : string.Empty,
            ClosedAt = closedAt,
        };
    }
}
