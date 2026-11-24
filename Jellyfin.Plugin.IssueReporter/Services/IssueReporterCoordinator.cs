using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.IssueReporter.Configuration;
using Jellyfin.Plugin.IssueReporter.Models;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Controller;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.IssueReporter.Services;

public sealed class IssueReporterCoordinator : BackgroundService
{
    private readonly IPluginManager _pluginManager;
    private readonly IServerApplicationHost _appHost;
    private readonly IApplicationPaths _appPaths;
    private readonly ResourceEfficientLogMonitor _logMonitor;
    private readonly PluginDiscoveryService _discovery;
    private readonly ErrorFilterService _filter;
    private readonly PrivacyRedactorService _redactor;
    private readonly GitHubApiClient _github;
    private readonly DeduplicationService _dedup;
    private readonly IssueTemplateEngine _templateEngine;
    private readonly StateStoreService _store;
    private readonly ILogger<IssueReporterCoordinator> _logger;

    private readonly ConcurrentDictionary<string, byte> _inFlightFps = new();
    private Dictionary<string, PluginTarget> _targets = new();
    private DateTimeOffset _lastDiscovery = DateTimeOffset.MinValue;

    public IssueReporterCoordinator(
        IPluginManager pluginManager,
        IServerApplicationHost appHost,
        IApplicationPaths appPaths,
        ResourceEfficientLogMonitor logMonitor,
        PluginDiscoveryService discovery,
        ErrorFilterService filter,
        PrivacyRedactorService redactor,
        GitHubApiClient github,
        DeduplicationService dedup,
        IssueTemplateEngine templateEngine,
        StateStoreService store,
        ILogger<IssueReporterCoordinator> logger)
    {
        _pluginManager = pluginManager;
        _appHost = appHost;
        _appPaths = appPaths;
        _logMonitor = logMonitor;
        _discovery = discovery;
        _filter = filter;
        _redactor = redactor;
        _github = github;
        _dedup = dedup;
        _templateEngine = templateEngine;
        _store = store;
        _logger = logger;
    }

    public IReadOnlyDictionary<string, PluginTarget> Targets => _targets;

    public PluginConfiguration CurrentConfig => Plugin.Instance?.Configuration ?? new PluginConfiguration();

    public void RefreshConfiguration(PluginConfiguration? overrideConfig = null)
    {
        var cfg = overrideConfig ?? CurrentConfig;
        _filter.UpdateConfig(cfg);
        _redactor.UpdateConfig(cfg);
        _redactor.RefreshSecretsFromDirectory(_appPaths.PluginConfigurationsPath);
        var plugins = _pluginManager.Plugins.Select(lp => lp.Instance).Where(p => p is not null).Cast<IPlugin>();
        var discovered = _discovery.DiscoverPlugins(plugins, cfg);
        _targets = discovered.ToDictionary(t => t.TargetKey, StringComparer.OrdinalIgnoreCase);
        _logMonitor.UpdateTargets(_targets.Values);
        _lastDiscovery = DateTimeOffset.UtcNow;
        _store.Stats.LastScanAt = _lastDiscovery.ToString("O");
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _store.Load();
        RefreshConfiguration();

        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(10));
        var reader = _logMonitor.Reader;

        while (!stoppingToken.IsCancellationRequested)
        {
            var waitRead = reader.WaitToReadAsync(stoppingToken).AsTask();
            var waitTick = timer.WaitForNextTickAsync(stoppingToken).AsTask();
            var completed = await Task.WhenAny(waitRead, waitTick);

            if (completed == waitRead && await waitRead)
            {
                // Coalesce burst log entries briefly
                await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
                while (reader.TryRead(out var ev))
                {
                    await ProcessEventAsync(ev, CurrentConfig, stoppingToken);
                }

                _store.Save();
            }
            else
            {
                if (DateTimeOffset.UtcNow - _lastDiscovery > TimeSpan.FromMinutes(30))
                {
                    RefreshConfiguration();
                }

                await FlushRetryQueueAsync(CurrentConfig, stoppingToken);
                _store.Stats.ApiCircuitOpen = _github.IsCircuitOpen;
                _store.Stats.RateLimitRemaining = _github.RateLimitRemaining;
                _store.Save();
            }
        }
    }

    public async Task<string> ProcessEventAsync(LogEvent ev, PluginConfiguration config, CancellationToken ct = default)
    {
        ResourceEfficientLogMonitor.InReporterPipeline.Value = true;
        try
        {
            return await ProcessEventInnerAsync(ev, config, ct);
        }
        finally
        {
            ResourceEfficientLogMonitor.InReporterPipeline.Value = false;
        }
    }

    private async Task<string> ProcessEventInnerAsync(LogEvent ev, PluginConfiguration config, CancellationToken ct)
    {
        if (!_targets.TryGetValue(ev.TargetKey, out var target) || !target.Enabled || string.IsNullOrWhiteSpace(target.FullRepo))
        {
            return "target_disabled";
        }

        if (ev.Level < target.MinSeverity)
        {
            return "below_severity";
        }

        if (_store.IsTargetFloodPaused(target.TargetKey))
        {
            _store.Stats.RateLimitedSuppressed++;
            return "flood_paused";
        }

        config.TargetOverrides.TryGetValue(target.TargetKey, out var ov);
        var (ignored, _) = _filter.ShouldIgnore(ev, config, ov);
        if (ignored)
        {
            _store.Stats.FilteredLocal++;
            return "filtered";
        }

        var msgRes = _redactor.Redact(ev.Message);
        var excRes = _redactor.Redact(ev.ExceptionText);
        var sanitized = new LogEvent
        {
            TargetKey = ev.TargetKey,
            CategoryName = ev.CategoryName,
            Level = ev.Level,
            Severity = ev.Severity,
            Message = msgRes.Text,
            ExceptionText = excRes.Text,
            ExceptionType = ev.ExceptionType,
            TimestampUtc = ev.TimestampUtc,
        };

        var fp = DeduplicationService.ComputeFingerprint(target, sanitized);
        if (!_inFlightFps.TryAdd(fp, 0))
        {
            _store.Stats.DuplicatesSuppressed++;
            return "cooldown";
        }

        try
        {
            if (_store.IsDismissedOrCooldown(fp, config.CooldownHours) || _store.IsQueuedForRetry(fp))
            {
                _store.Stats.DuplicatesSuppressed++;
                return "cooldown";
            }

            if (_store.RecordTargetUniqueEvent(target.TargetKey, fp))
            {
                _logger.LogWarning("Anti-flood breaker paused {TargetKey} for 60m after burst of unique errors.", target.TargetKey);
                return "flood_paused";
            }

            var occ = _store.RecordOccurrence(fp, config.OccurrenceWindowMinutes);
            var threshold = ev.Level < LogLevel.Error ? Math.Max(1, config.MinOccurrencesWarning) : Math.Max(1, config.MinOccurrencesError);
            if (occ < threshold)
            {
                return "below_threshold";
            }

            if (!_store.WithinRateLimits(target.FullRepo, config))
            {
                _store.Stats.RateLimitedSuppressed++;
                return "rate_limited";
            }

            if (config.SkipOutdated && target.RepoOwner is not null && target.RepoName is not null)
            {
                target.LatestVersion ??= await _github.GetLatestReleaseAsync(config, target.RepoOwner, target.RepoName, ct);
                if (DeduplicationService.IsTargetOutdated(target, target.LatestVersion, config.AllowDevBuilds))
                {
                    _store.Stats.OutdatedSuppressed++;
                    _store.SetCooldown(fp);
                    return "outdated";
                }
            }

            DuplicateMatch dup;
            try
            {
                dup = await _dedup.CheckGitHubDuplicateAsync(config, target, sanitized, fp, ct);
            }
            catch (GitHubApiException ex) when (ex is GitHubRateLimitException or GitHubNetworkException)
            {
                _store.Stats.LastError = ex.Message;
                return "github_deferred";
            }

            var effectiveMode = config.DryRun ? "dry_run" : target.Mode;
            if (dup.Action is "skip_open" or "skip_closed")
            {
                _store.Stats.DuplicatesSuppressed++;
                _store.SetCooldown(fp);
                return dup.Action;
            }

            if (dup.Action == "comment_open" && dup.IssueNumber is not null)
            {
                return await HandleOpenCommentAsync(config, target, fp, dup.IssueNumber.Value, dup.CommentBody, effectiveMode, ct);
            }

            var rendered = await _templateEngine.RenderIssueAsync(
                config,
                target,
                sanitized,
                msgRes.Text,
                excRes.Text,
                fp,
                occ,
                _appHost.ApplicationVersionString,
                dup.Action == "regression_new" ? dup.IssueNumber : null,
                ct);

            var (passed, reasons) = _redactor.VerifyNoLeaks(rendered.Title, rendered.Body);
            if (!passed)
            {
                _store.Stats.LeakGuardBlocks++;
                _logger.LogWarning("Pre-flight Leak Guard aborted {TargetKey} ({Reasons}).", target.TargetKey, string.Join(", ", reasons));
                _store.SetCooldown(fp);
                return "leak_guard_blocked";
            }

            if (effectiveMode is "dry_run" or "approval")
            {
                _store.StoreDraft(new PendingDraft
                {
                    DraftId = Guid.NewGuid().ToString("N")[..10],
                    TargetKey = target.TargetKey,
                    TargetName = target.Name,
                    Repo = rendered.Repo,
                    Title = rendered.Title,
                    Body = rendered.Body,
                    Labels = rendered.Labels,
                    Fingerprint = fp,
                    Severity = ev.Severity,
                    Version = target.Version,
                    CreatedAt = DateTimeOffset.UtcNow.ToString("O"),
                    Occurrences = occ,
                    TemplateUsed = rendered.TemplateUsed,
                    DryRunOnly = effectiveMode == "dry_run",
                });
                _store.SetCooldown(fp);
                return effectiveMode == "dry_run" ? "dry_run_draft" : "approval_queued";
            }

            return await SubmitIssueAsync(config, target, rendered, fp, ct);
        }
        finally
        {
            _inFlightFps.TryRemove(fp, out _);
        }
    }

    private async Task<string> HandleOpenCommentAsync(PluginConfiguration config, PluginTarget target, string fp, int issueNum, string rawComment, string mode, CancellationToken ct)
    {
        if (mode != "auto" || target.RepoOwner is null || target.RepoName is null || target.FullRepo is null)
        {
            _store.Stats.DuplicatesSuppressed++;
            _store.SetCooldown(fp);
            return "comment_skipped_dry_run";
        }

        if (!_store.WithinRateLimits(target.FullRepo, config))
        {
            _store.Stats.RateLimitedSuppressed++;
            return "rate_limited";
        }

        var comment = _redactor.Redact(rawComment).Text;
        var (safe, _) = _redactor.VerifyNoLeaks(string.Empty, comment);
        if (!safe)
        {
            _store.Stats.LeakGuardBlocks++;
            return "leak_guard_blocked";
        }

        if (!_store.IsSubmissionSpaced())
        {
            _store.EnqueueRetry("comment", target.TargetKey, target.FullRepo, fp, string.Empty, comment, null, issueNum, "submission_spacing");
            return "queued_retry";
        }

        try
        {
            await _github.CreateCommentAsync(config, target.RepoOwner, target.RepoName, issueNum, comment, ct);
            _store.Stats.CommentsPosted++;
            _store.SetCooldown(fp);
            _store.RecordPostRate(target.FullRepo);
            return "commented";
        }
        catch (GitHubApiException ex) when (ex is GitHubRateLimitException or GitHubNetworkException)
        {
            _store.EnqueueRetry("comment", target.TargetKey, target.FullRepo, fp, string.Empty, comment, null, issueNum, ex.Message, (ex as GitHubRateLimitException)?.ResetAt);
            return "queued_retry";
        }
    }

    private async Task<string> SubmitIssueAsync(PluginConfiguration config, PluginTarget target, RenderedIssue rendered, string fp, CancellationToken ct)
    {
        if (target.RepoOwner is null || target.RepoName is null)
        {
            return "missing_repo";
        }

        if (!_store.IsSubmissionSpaced())
        {
            _store.EnqueueRetry("issue", target.TargetKey, rendered.Repo, fp, rendered.Title, rendered.Body, rendered.Labels, null, "submission_spacing");
            return "queued_retry";
        }

        try
        {
            var (_, url) = await _github.CreateIssueAsync(config, target.RepoOwner, target.RepoName, rendered.Title, rendered.Body, rendered.Labels, ct);
            _store.Stats.IssuesCreated++;
            _store.Stats.LastIssueUrl = url;
            _store.SetCooldown(fp);
            _store.RecordPostRate(rendered.Repo);
            return "created";
        }
        catch (GitHubApiException ex) when (ex is GitHubRateLimitException or GitHubNetworkException)
        {
            _store.Stats.LastError = ex.Message;
            _store.EnqueueRetry("issue", target.TargetKey, rendered.Repo, fp, rendered.Title, rendered.Body, rendered.Labels, null, ex.Message, (ex as GitHubRateLimitException)?.ResetAt);
            return "queued_retry";
        }
        catch (GitHubApiException ex)
        {
            _store.Stats.LastError = ex.Message;
            return "github_error";
        }
    }

    public async Task FlushRetryQueueAsync(PluginConfiguration config, CancellationToken ct = default)
    {
        foreach (var item in _store.GetDueRetries(3))
        {
            if (_store.IsDismissedOrCooldown(item.Fingerprint, config.CooldownHours))
            {
                _store.RemoveRetry(item.Fingerprint);
                continue;
            }

            if (!_store.WithinRateLimits(item.Repo, config) || !_store.IsSubmissionSpaced())
            {
                break;
            }

            var (safe, _) = _redactor.VerifyNoLeaks(item.Title, item.Body);
            if (!safe)
            {
                _store.Stats.LeakGuardBlocks++;
                _store.RemoveRetry(item.Fingerprint);
                continue;
            }

            var parts = item.Repo.Split('/', 2);
            if (parts.Length != 2)
            {
                _store.RemoveRetry(item.Fingerprint);
                continue;
            }

            try
            {
                if (item.Kind == "comment" && item.IssueNumber is not null)
                {
                    await _github.CreateCommentAsync(config, parts[0], parts[1], item.IssueNumber.Value, item.Body, ct);
                    _store.Stats.CommentsPosted++;
                }
                else
                {
                    var (_, url) = await _github.CreateIssueAsync(config, parts[0], parts[1], item.Title, item.Body, item.Labels, ct);
                    _store.Stats.IssuesCreated++;
                    _store.Stats.LastIssueUrl = url;
                }

                _store.SetCooldown(item.Fingerprint);
                _store.RecordPostRate(item.Repo);
                _store.RemoveRetry(item.Fingerprint);
            }
            catch (GitHubApiException ex) when (ex is GitHubRateLimitException or GitHubNetworkException)
            {
                _store.EnqueueRetry(item.Kind, item.TargetKey, item.Repo, item.Fingerprint, item.Title, item.Body, item.Labels, item.IssueNumber, ex.Message, (ex as GitHubRateLimitException)?.ResetAt);
                break;
            }
            catch (GitHubApiException)
            {
                _store.RemoveRetry(item.Fingerprint);
            }
        }
    }

    public async Task<string> ApproveDraftAsync(string draftId, CancellationToken ct = default)
    {
        var draft = _store.GetDraft(draftId) ?? throw new InvalidOperationException($"Unknown draft {draftId}");
        var (safe, reasons) = _redactor.VerifyNoLeaks(draft.Title, draft.Body);
        if (!safe)
        {
            _store.Stats.LeakGuardBlocks++;
            throw new InvalidOperationException($"Leak Guard aborted draft {draftId}: {string.Join(", ", reasons)}");
        }

        var parts = draft.Repo.Split('/', 2);
        var (_, url) = await _github.CreateIssueAsync(CurrentConfig, parts[0], parts[1], draft.Title, draft.Body, draft.Labels, ct);
        _store.PopDraft(draftId);
        _store.Stats.IssuesCreated++;
        _store.Stats.LastIssueUrl = url;
        _store.SetCooldown(draft.Fingerprint);
        _store.RecordPostRate(draft.Repo);
        _store.Save();
        return url;
    }

    public async Task<object> PreviewDraftAsync(string targetKey, string sampleMessage, string severity, CancellationToken ct = default)
    {
        if (!_targets.TryGetValue(targetKey, out var target))
        {
            throw new InvalidOperationException($"Unknown target {targetKey}");
        }

        var red = _redactor.Redact(sampleMessage);
        var lvl = PluginDiscoveryService.ParseLogLevel(severity);
        var ev = new LogEvent { TargetKey = targetKey, CategoryName = target.AssemblyName, Level = lvl, Severity = severity.ToUpperInvariant(), Message = red.Text };
        var fp = DeduplicationService.ComputeFingerprint(target, ev);
        var rendered = await _templateEngine.RenderIssueAsync(CurrentConfig, target, ev, red.Text, string.Empty, fp, 1, _appHost.ApplicationVersionString, null, ct);
        var (passed, reasons) = _redactor.VerifyNoLeaks(rendered.Title, rendered.Body);
        return new
        {
            repo = rendered.Repo,
            title = rendered.Title,
            body = rendered.Body,
            template_used = rendered.TemplateUsed,
            leak_guard_passed = passed,
            leak_guard_reasons = reasons,
        };
    }
}
