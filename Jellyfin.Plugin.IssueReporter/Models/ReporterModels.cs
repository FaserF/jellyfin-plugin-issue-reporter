using System;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.IssueReporter.Models;

public sealed class TargetOverride
{
    public string TargetKey { get; set; } = string.Empty;

    public bool? Enabled { get; set; }

    public string? Mode { get; set; }

    public string? MinSeverity { get; set; }

    public string? RepoOverride { get; set; }

    public List<string> IgnorePatterns { get; set; } = new();
}

public sealed class PluginTarget
{
    public string TargetKey { get; set; } = string.Empty;

    public string PluginId { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string AssemblyName { get; set; } = string.Empty;

    public string Version { get; set; } = "0.0.0";

    public string? RepoOwner { get; set; }

    public string? RepoName { get; set; }

    public string? RepoUrl { get; set; }

    public string Channel { get; set; } = "stable";

    public bool OwnerMatched { get; set; }

    public bool Enabled { get; set; }

    public string Mode { get; set; } = "approval";

    public LogLevel MinSeverity { get; set; } = LogLevel.Error;

    public string? LatestVersion { get; set; }

    public string? FullRepo =>
        !string.IsNullOrWhiteSpace(RepoOwner) && !string.IsNullOrWhiteSpace(RepoName)
            ? $"{RepoOwner}/{RepoName}"
            : null;
}

public sealed class LogEvent
{
    public string TargetKey { get; set; } = string.Empty;

    public string CategoryName { get; set; } = string.Empty;

    public LogLevel Level { get; set; } = LogLevel.Error;

    public string Severity { get; set; } = "ERROR";

    public string Message { get; set; } = string.Empty;

    public string ExceptionText { get; set; } = string.Empty;

    public string ExceptionType { get; set; } = string.Empty;

    public string TimestampUtc { get; set; } = string.Empty;
}

public sealed class RedactionResult
{
    public string Text { get; set; } = string.Empty;

    public int ReplacementsCount { get; set; }

    public List<string> MatchedCategories { get; set; } = new();
}

public sealed class DuplicateMatch
{
    public string Action { get; set; } = "none";

    public int? IssueNumber { get; set; }

    public string? IssueUrl { get; set; }

    public string Reason { get; set; } = string.Empty;

    public double Similarity { get; set; }

    public string CommentBody { get; set; } = string.Empty;
}

public sealed class RenderedIssue
{
    public string Repo { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    public string Body { get; set; } = string.Empty;

    public List<string> Labels { get; set; } = new();

    public string TemplateUsed { get; set; } = "default";

    public string Fingerprint { get; set; } = string.Empty;
}

public sealed class PendingDraft
{
    public string DraftId { get; set; } = string.Empty;

    public string TargetKey { get; set; } = string.Empty;

    public string TargetName { get; set; } = string.Empty;

    public string Repo { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    public string Body { get; set; } = string.Empty;

    public List<string> Labels { get; set; } = new();

    public string Fingerprint { get; set; } = string.Empty;

    public string Severity { get; set; } = "ERROR";

    public string Version { get; set; } = string.Empty;

    public string CreatedAt { get; set; } = string.Empty;

    public int Occurrences { get; set; } = 1;

    public string TemplateUsed { get; set; } = "default";

    public bool DryRunOnly { get; set; }
}

public sealed class QueuedSubmission
{
    public string QueueId { get; set; } = string.Empty;

    public string Kind { get; set; } = "issue";

    public string TargetKey { get; set; } = string.Empty;

    public string Repo { get; set; } = string.Empty;

    public string Fingerprint { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    public string Body { get; set; } = string.Empty;

    public List<string> Labels { get; set; } = new();

    public int? IssueNumber { get; set; }

    public string CreatedAt { get; set; } = string.Empty;

    public string NextRetryAt { get; set; } = string.Empty;

    public int Attempts { get; set; }

    public string LastError { get; set; } = string.Empty;
}

public sealed class ReporterStats
{
    public int IssuesCreated { get; set; }

    public int CommentsPosted { get; set; }

    public int DuplicatesSuppressed { get; set; }

    public int OutdatedSuppressed { get; set; }

    public int FilteredLocal { get; set; }

    public int RateLimitedSuppressed { get; set; }

    public int LeakGuardBlocks { get; set; }

    public int RetryQueueSize { get; set; }

    public bool ApiCircuitOpen { get; set; }

    public int? RateLimitRemaining { get; set; }

    public string? LastIssueUrl { get; set; }

    public string? LastScanAt { get; set; }

    public string? LastError { get; set; }
}
