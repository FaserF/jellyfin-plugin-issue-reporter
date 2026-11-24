using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Jellyfin.Plugin.IssueReporter.Configuration;
using Jellyfin.Plugin.IssueReporter.Models;
using MediaBrowser.Common.Configuration;

namespace Jellyfin.Plugin.IssueReporter.Services;

public sealed class StateStoreService
{
    private const int MaxDrafts = 50;
    private const int MaxRetryQueue = 50;
    private const int MaxRetryAttempts = 6;
    private const int MinSpacingSeconds = 30;
    private const int FloodBreakerUniqueThreshold = 5;
    private const int FloodBreakerWindowMinutes = 10;
    private const int FloodBreakerPauseMinutes = 60;

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    private readonly string _statePath;
    private readonly object _lock = new();
    private PersistedState _state = new();
    private readonly Dictionary<string, List<DateTimeOffset>> _occurrenceWindows = new();
    private readonly Dictionary<string, List<(DateTimeOffset At, string Fp)>> _targetUniqueEvents = new();

    public StateStoreService(IApplicationPaths appPaths)
    {
        var dir = Path.Combine(appPaths.PluginConfigurationsPath, "IssueReporter");
        Directory.CreateDirectory(dir);
        _statePath = Path.Combine(dir, "state.json");
    }

    public StateStoreService(string customStatePath)
    {
        var dir = Path.GetDirectoryName(customStatePath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        _statePath = customStatePath;
    }

    public ReporterStats Stats
    {
        get
        {
            lock (_lock)
            {
                return _state.Stats;
            }
        }
    }

    public List<PendingDraft> GetDrafts()
    {
        lock (_lock)
        {
            return _state.PendingDrafts.Values.ToList();
        }
    }

    public int RetryQueueCount
    {
        get
        {
            lock (_lock)
            {
                return _state.RetryQueue.Count;
            }
        }
    }

    public void Load()
    {
        lock (_lock)
        {
            if (!File.Exists(_statePath))
            {
                return;
            }

            try
            {
                var loaded = JsonSerializer.Deserialize<PersistedState>(File.ReadAllText(_statePath));
                if (loaded is not null)
                {
                    _state = loaded;
                    _state.Stats.RetryQueueSize = _state.RetryQueue.Count;
                }
            }
            catch (Exception)
            {
                _state = new PersistedState();
            }
        }
    }

    public void Save()
    {
        lock (_lock)
        {
            _state.Stats.RetryQueueSize = _state.RetryQueue.Count;
            File.WriteAllText(_statePath, JsonSerializer.Serialize(_state, JsonOpts));
        }
    }

    public bool IsDismissedOrCooldown(string fp, int cooldownHours)
    {
        lock (_lock)
        {
            if (_state.DismissedFps.Contains(fp))
            {
                return true;
            }

            if (_state.Cooldowns.TryGetValue(fp, out var raw) && DateTimeOffset.TryParse(raw, out var ts))
            {
                if (DateTimeOffset.UtcNow - ts < TimeSpan.FromHours(Math.Max(1, cooldownHours)))
                {
                    return true;
                }

                _state.Cooldowns.Remove(fp);
            }

            return false;
        }
    }

    public void SetCooldown(string fp)
    {
        lock (_lock)
        {
            _state.Cooldowns[fp] = DateTimeOffset.UtcNow.ToString("O");
        }
    }

    public int RecordOccurrence(string fp, int windowMinutes)
    {
        lock (_lock)
        {
            var now = DateTimeOffset.UtcNow;
            var cutoff = now.AddMinutes(-Math.Max(1, windowMinutes));
            var list = _occurrenceWindows.TryGetValue(fp, out var existing)
                ? existing.Where(t => t >= cutoff).ToList()
                : new List<DateTimeOffset>();
            list.Add(now);
            _occurrenceWindows[fp] = list;
            return list.Count;
        }
    }

    public bool IsTargetFloodPaused(string targetKey)
    {
        lock (_lock)
        {
            if (_state.TargetPausedUntil.TryGetValue(targetKey, out var raw) && DateTimeOffset.TryParse(raw, out var until))
            {
                if (DateTimeOffset.UtcNow < until)
                {
                    return true;
                }

                _state.TargetPausedUntil.Remove(targetKey);
            }

            return false;
        }
    }

    public bool RecordTargetUniqueEvent(string targetKey, string fp)
    {
        lock (_lock)
        {
            var now = DateTimeOffset.UtcNow;
            var cutoff = now.AddMinutes(-FloodBreakerWindowMinutes);
            var list = _targetUniqueEvents.TryGetValue(targetKey, out var existing)
                ? existing.Where(e => e.At >= cutoff).ToList()
                : new List<(DateTimeOffset At, string Fp)>();
            if (!list.Any(e => e.Fp == fp))
            {
                list.Add((now, fp));
            }

            _targetUniqueEvents[targetKey] = list;
            if (list.Count >= FloodBreakerUniqueThreshold)
            {
                _state.TargetPausedUntil[targetKey] = now.AddMinutes(FloodBreakerPauseMinutes).ToString("O");
                return true;
            }

            return false;
        }
    }

    public bool WithinRateLimits(string fullRepo, PluginConfiguration config)
    {
        lock (_lock)
        {
            var now = DateTimeOffset.UtcNow;
            var hourAgo = now.AddHours(-1);
            var dayAgo = now.AddDays(-1);

            _state.HourlyPosts = _state.HourlyPosts.Where(t => DateTimeOffset.TryParse(t, out var dt) && dt >= hourAgo).ToList();
            if (_state.HourlyPosts.Count >= Math.Max(1, config.MaxIssuesPerHour))
            {
                return false;
            }

            _state.DailyPosts = _state.DailyPosts.Where(t => DateTimeOffset.TryParse(t, out var dt) && dt >= dayAgo).ToList();
            if (_state.DailyPosts.Count >= Math.Max(1, config.MaxIssuesPerDay))
            {
                return false;
            }

            var repoKey = fullRepo.ToLowerInvariant();
            var repoList = _state.RepoDailyPosts.TryGetValue(repoKey, out var rl)
                ? rl.Where(t => DateTimeOffset.TryParse(t, out var dt) && dt >= dayAgo).ToList()
                : new List<string>();
            _state.RepoDailyPosts[repoKey] = repoList;
            return repoList.Count < Math.Max(1, config.MaxIssuesPerRepoDay);
        }
    }

    public bool IsSubmissionSpaced()
    {
        lock (_lock)
        {
            if (_state.HourlyPosts.Count == 0)
            {
                return true;
            }

            return DateTimeOffset.TryParse(_state.HourlyPosts[^1], out var last)
                && (DateTimeOffset.UtcNow - last) >= TimeSpan.FromSeconds(MinSpacingSeconds);
        }
    }

    public void RecordPostRate(string fullRepo)
    {
        lock (_lock)
        {
            var now = DateTimeOffset.UtcNow.ToString("O");
            _state.HourlyPosts.Add(now);
            _state.DailyPosts.Add(now);
            var repoKey = fullRepo.ToLowerInvariant();
            if (!_state.RepoDailyPosts.TryGetValue(repoKey, out var list))
            {
                list = new List<string>();
                _state.RepoDailyPosts[repoKey] = list;
            }

            list.Add(now);
        }
    }

    public void StoreDraft(PendingDraft draft)
    {
        lock (_lock)
        {
            _state.PendingDrafts[draft.DraftId] = draft;
            while (_state.PendingDrafts.Count > MaxDrafts)
            {
                var oldest = _state.PendingDrafts.Keys.First();
                _state.PendingDrafts.Remove(oldest);
            }
        }
    }

    public PendingDraft? PopDraft(string draftId)
    {
        lock (_lock)
        {
            if (_state.PendingDrafts.Remove(draftId, out var d))
            {
                return d;
            }

            return null;
        }
    }

    public PendingDraft? GetDraft(string draftId)
    {
        lock (_lock)
        {
            return _state.PendingDrafts.TryGetValue(draftId, out var d) ? d : null;
        }
    }

    public void DismissDraft(string draftId)
    {
        lock (_lock)
        {
            if (_state.PendingDrafts.Remove(draftId, out var d))
            {
                _state.DismissedFps.Add(d.Fingerprint);
                _state.Cooldowns[d.Fingerprint] = DateTimeOffset.UtcNow.ToString("O");
            }
        }
    }

    public void ClearCooldowns()
    {
        lock (_lock)
        {
            _state.Cooldowns.Clear();
            _state.DismissedFps.Clear();
            _state.TargetPausedUntil.Clear();
            _occurrenceWindows.Clear();
            _targetUniqueEvents.Clear();
        }
    }

    public bool IsQueuedForRetry(string fp)
    {
        lock (_lock)
        {
            return _state.RetryQueue.ContainsKey(fp);
        }
    }

    public void EnqueueRetry(
        string kind,
        string targetKey,
        string repo,
        string fp,
        string title,
        string body,
        List<string>? labels,
        int? issueNumber,
        string error,
        DateTimeOffset? resetAt = null)
    {
        lock (_lock)
        {
            var now = DateTimeOffset.UtcNow;
            var existing = _state.RetryQueue.TryGetValue(fp, out var prev) ? prev : null;
            var attempts = existing is not null ? existing.Attempts + 1 : 1;
            var backoffSecs = new[] { 120, 600, 1800, 7200, 21600 }[Math.Min(attempts - 1, 4)];
            var nextAt = now.AddSeconds(backoffSecs);
            if (resetAt is not null && resetAt.Value.AddSeconds(5) > nextAt)
            {
                nextAt = resetAt.Value.AddSeconds(5);
            }

            _state.RetryQueue[fp] = new QueuedSubmission
            {
                QueueId = existing?.QueueId ?? Guid.NewGuid().ToString("N")[..10],
                Kind = kind,
                TargetKey = targetKey,
                Repo = repo,
                Fingerprint = fp,
                Title = title,
                Body = body,
                Labels = labels ?? new List<string>(),
                IssueNumber = issueNumber,
                CreatedAt = existing?.CreatedAt ?? now.ToString("O"),
                NextRetryAt = nextAt.ToString("O"),
                Attempts = attempts,
                LastError = error.Length > 200 ? error[..200] : error,
            };

            while (_state.RetryQueue.Count > MaxRetryQueue)
            {
                var oldest = _state.RetryQueue.Keys.First();
                _state.RetryQueue.Remove(oldest);
            }

            _state.Stats.RetryQueueSize = _state.RetryQueue.Count;
        }
    }

    public List<QueuedSubmission> GetDueRetries(int limit = 3)
    {
        lock (_lock)
        {
            var now = DateTimeOffset.UtcNow;
            var due = new List<QueuedSubmission>();
            foreach (var kvp in _state.RetryQueue.ToList())
            {
                if (kvp.Value.Attempts >= MaxRetryAttempts)
                {
                    _state.RetryQueue.Remove(kvp.Key);
                    continue;
                }

                if (DateTimeOffset.TryParse(kvp.Value.NextRetryAt, out var nextAt) && nextAt <= now)
                {
                    due.Add(kvp.Value);
                    if (due.Count >= limit)
                    {
                        break;
                    }
                }
            }

            _state.Stats.RetryQueueSize = _state.RetryQueue.Count;
            return due;
        }
    }

    public void RemoveRetry(string fp)
    {
        lock (_lock)
        {
            _state.RetryQueue.Remove(fp);
            _state.Stats.RetryQueueSize = _state.RetryQueue.Count;
        }
    }

    private sealed class PersistedState
    {
        public Dictionary<string, string> Cooldowns { get; set; } = new();

        public HashSet<string> DismissedFps { get; set; } = new();

        public List<string> HourlyPosts { get; set; } = new();

        public List<string> DailyPosts { get; set; } = new();

        public Dictionary<string, List<string>> RepoDailyPosts { get; set; } = new();

        public Dictionary<string, string> TargetPausedUntil { get; set; } = new();

        public Dictionary<string, PendingDraft> PendingDrafts { get; set; } = new();

        public Dictionary<string, QueuedSubmission> RetryQueue { get; set; } = new();

        public ReporterStats Stats { get; set; } = new();
    }
}
