using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Jellyfin.Plugin.IssueReporter.Configuration;
using Jellyfin.Plugin.IssueReporter.Models;
using Jellyfin.Plugin.IssueReporter.Services;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.IssueReporter.Tests;

public sealed class GitHubDedupAndOutboxTests
{
    private sealed class MockHandler : HttpMessageHandler
    {
        private readonly Queue<(HttpStatusCode Status, string Body, string? ETag)> _responses;

        public MockHandler(params (HttpStatusCode Status, string Body, string? ETag)[] responses)
        {
            _responses = new Queue<(HttpStatusCode, string, string?)>(responses);
        }

        public int CallCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            var (status, body, etag) = _responses.Dequeue();
            var resp = new HttpResponseMessage(status)
            {
                Content = new StringContent(body),
            };
            resp.Headers.Add("X-RateLimit-Remaining", "4990");
            resp.Headers.Add("X-RateLimit-Reset", DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds().ToString());
            if (!string.IsNullOrEmpty(etag))
            {
                resp.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue(etag);
            }

            return Task.FromResult(resp);
        }
    }

    private sealed class SimpleFactory : IHttpClientFactory
    {
        private readonly HttpClient _client;

        public SimpleFactory(HttpMessageHandler handler) => _client = new HttpClient(handler);

        public HttpClient CreateClient(string name) => _client;
    }

    [Fact]
    public async Task GitHubApiClient_UsesETagCache_AndDedupMatchesLocallyWithoutSearch()
    {
        var target = new PluginTarget
        {
            TargetKey = "plugin:jellyfin.plugin.youtube",
            Name = "YouTube",
            AssemblyName = "Jellyfin.Plugin.YouTube",
            Version = "1.1.4.0",
            RepoOwner = "FaserF",
            RepoName = "youtube-jellyfin",
            OwnerMatched = true,
            Enabled = true,
        };
        var ev = new LogEvent
        {
            TargetKey = target.TargetKey,
            CategoryName = "Jellyfin.Plugin.YouTube.Channel",
            Level = LogLevel.Error,
            Severity = "ERROR",
            Message = "Failed parsing playlist response",
            ExceptionType = "JsonException",
        };
        var fp = DeduplicationService.ComputeFingerprint(target, ev);

        var issuesJson = $"[{{\"number\":12,\"html_url\":\"https://github.com/FaserF/youtube-jellyfin/issues/12\",\"state\":\"open\",\"title\":\"[Bug]: Playlist error\",\"body\":\"Version 1.1.3.0 <!-- {DeduplicationService.FingerprintPrefix}{fp} -->\"}}]";
        var handler = new MockHandler(
            (HttpStatusCode.OK, issuesJson, "\"etag-1\""),
            (HttpStatusCode.OK, "[]", null));
        var factory = new SimpleFactory(handler);
        var cfg = new PluginConfiguration { AuthMethod = "pat", GitHubToken = "ghp_test", GitHubUser = "FaserF", CommentOnNewerVersion = true };
        var auth = new GitHubAuthService(factory);
        var client = new GitHubApiClient(factory, auth);
        var dedup = new DeduplicationService(client);

        var match = await dedup.CheckGitHubDuplicateAsync(cfg, target, ev, fp, TestContext.Current.CancellationToken);
        match.Action.Should().Be("comment_open");
        match.IssueNumber.Should().Be(12);
        match.CommentBody.Should().Contain("1.1.4.0");
        handler.CallCount.Should().Be(2); // issues list + comments (NO Search API call!)
    }

    [Fact]
    public async Task TemplateEngine_RendersBugReport_AndStateStoreHandlesRetryOutboxAndFloodBreaker()
    {
        var handler = new MockHandler((HttpStatusCode.NotFound, "{}", null));
        var factory = new SimpleFactory(handler);
        var cfg = new PluginConfiguration { AuthMethod = "pat", GitHubToken = "ghp_test", GitHubUser = "FaserF" };
        var engine = new IssueTemplateEngine(new GitHubApiClient(factory, new GitHubAuthService(factory)));

        var target = new PluginTarget
        {
            TargetKey = "plugin:jellyfin.plugin.youtube",
            Name = "YouTube",
            AssemblyName = "Jellyfin.Plugin.YouTube",
            Version = "1.1.3.0",
            RepoOwner = "FaserF",
            RepoName = "youtube-jellyfin",
        };
        var ev = new LogEvent { CategoryName = "Jellyfin.Plugin.YouTube", Severity = "ERROR", ExceptionType = "InvalidOperationException", Message = "Stream error" };
        var rendered = await engine.RenderIssueAsync(cfg, target, ev, "Stream error", string.Empty, "abcdef1234567890", 2, "12.4.0", ct: TestContext.Current.CancellationToken);
        rendered.Title.Should().Contain("InvalidOperationException");
        rendered.Body.Should().Contain("12.4.0");
        rendered.Body.Should().Contain("abcdef1234567890");

        var stateFile = Path.Combine(Path.GetTempPath(), $"jf_state_{Guid.NewGuid():N}.json");
        try
        {
            var store = new StateStoreService(stateFile);
            store.EnqueueRetry("issue", target.TargetKey, "FaserF/youtube-jellyfin", "fp1", rendered.Title, rendered.Body, rendered.Labels, null, "Network down", DateTimeOffset.UtcNow.AddSeconds(-10));
            store.RetryQueueCount.Should().Be(1);
            store.Save();

            var reloaded = new StateStoreService(stateFile);
            reloaded.Load();
            reloaded.RetryQueueCount.Should().Be(1);

            for (var i = 0; i < 5; i++)
            {
                reloaded.RecordTargetUniqueEvent(target.TargetKey, $"fp_{i}");
            }

            reloaded.IsTargetFloodPaused(target.TargetKey).Should().BeTrue();
        }
        finally
        {
            if (File.Exists(stateFile))
            {
                File.Delete(stateFile);
            }
        }
    }
}
