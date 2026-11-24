using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.IssueReporter.Configuration;
using Jellyfin.Plugin.IssueReporter.Models;

namespace Jellyfin.Plugin.IssueReporter.Services;

public sealed class DeduplicationService
{
    public const string FingerprintPrefix = "jellyfin-issue-reporter:fingerprint=";

    private static readonly Regex VersionRegex = new(@"\bv?(\d+\.\d+(?:\.\d+)?(?:\.\d+)?)\b", RegexOptions.Compiled);
    private static readonly Regex HexRegex = new(@"\b0x[0-9a-fA-F]+\b", RegexOptions.Compiled);
    private static readonly Regex NumRegex = new(@"\b\d{4,}\b", RegexOptions.Compiled);

    private readonly GitHubApiClient _github;

    public DeduplicationService(GitHubApiClient github)
    {
        _github = github;
    }

    public static string ComputeFingerprint(PluginTarget target, LogEvent ev)
    {
        var norm = ev.Message.Trim();
        norm = HexRegex.Replace(norm, "<HEX>");
        norm = NumRegex.Replace(norm, "<NUM>");
        norm = Regex.Replace(norm, @"\s+", " ").ToLowerInvariant();
        if (norm.Length > 220)
        {
            norm = norm[..220];
        }

        var raw = $"{target.AssemblyName.ToLowerInvariant()}|{ev.ExceptionType.ToLowerInvariant()}|{ev.CategoryName.ToLowerInvariant()}|{norm}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        return Convert.ToHexString(hash)[..16].ToLowerInvariant();
    }

    public static Version? ParseComparableVersion(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var m = VersionRegex.Match(raw);
        return m.Success && Version.TryParse(m.Groups[1].Value, out var v) ? v : null;
    }

    public static bool IsTargetOutdated(PluginTarget target, string? latestReleaseTag, bool allowDevBuilds)
    {
        if (allowDevBuilds && target.Channel is "dev" or "beta")
        {
            return false;
        }

        var installed = ParseComparableVersion(target.Version);
        var latest = ParseComparableVersion(latestReleaseTag);
        return installed is not null && latest is not null && installed < latest;
    }

    public async Task<DuplicateMatch> CheckGitHubDuplicateAsync(
        PluginConfiguration config,
        PluginTarget target,
        LogEvent ev,
        string fingerprint,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(target.RepoOwner) || string.IsNullOrWhiteSpace(target.RepoName))
        {
            return new DuplicateMatch { Action = "none" };
        }

        var owner = target.RepoOwner;
        var repo = target.RepoName;
        var fpMarker = $"{FingerprintPrefix}{fingerprint}";
        var (localIssues, mayHaveMore) = await _github.GetRepoIssuesAsync(config, owner, repo, ct);

        GitHubIssueRecord? bestMatch = null;
        var bestSim = 0.0;

        foreach (var issue in localIssues)
        {
            if (issue.Body.Contains(fpMarker, StringComparison.Ordinal))
            {
                bestMatch = issue;
                bestSim = 1.0;
                break;
            }
        }

        if (bestMatch is null && localIssues.Count > 0)
        {
            foreach (var issue in localIssues)
            {
                var sim = ComputeTokenSimilarity($"{ev.ExceptionType} {ev.Message}", $"{issue.Title} {issue.Body}");
                if (sim > bestSim)
                {
                    bestSim = sim;
                    bestMatch = issue;
                }
            }

            if (bestSim < config.FuzzySimilarityThreshold)
            {
                bestMatch = null;
            }
        }

        if (bestMatch is null && mayHaveMore)
        {
            var exact = await _github.SearchIssuesAsync(config, owner, repo, $"\"{fpMarker}\"", ct);
            if (exact.Count > 0)
            {
                bestMatch = exact[0];
                bestSim = 1.0;
            }
        }

        if (bestMatch is null)
        {
            return new DuplicateMatch { Action = "none" };
        }

        if (bestMatch.State.Equals("open", StringComparison.OrdinalIgnoreCase))
        {
            if (!config.CommentOnNewerVersion && !target.OwnerMatched)
            {
                return new DuplicateMatch { Action = "skip_open", IssueNumber = bestMatch.Number, IssueUrl = bestMatch.HtmlUrl, Reason = "open_duplicate", Similarity = bestSim };
            }

            var comments = await _github.GetIssueCommentsAsync(config, owner, repo, bestMatch.Number, ct);
            var marker = $"still observed on `{target.Version}`";
            if (comments.Any(c => c.Contains(marker, StringComparison.OrdinalIgnoreCase)))
            {
                return new DuplicateMatch { Action = "skip_open", IssueNumber = bestMatch.Number, IssueUrl = bestMatch.HtmlUrl, Reason = "already_commented_version", Similarity = bestSim };
            }

            var installedV = ParseComparableVersion(target.Version);
            var maxExisting = ExtractMaxVersion(comments.Prepend(bestMatch.Body));
            if (installedV is not null && maxExisting is not null && installedV > maxExisting)
            {
                return new DuplicateMatch
                {
                    Action = "comment_open",
                    IssueNumber = bestMatch.Number,
                    IssueUrl = bestMatch.HtmlUrl,
                    Reason = "newer_version_reproduction",
                    Similarity = bestSim,
                    CommentBody = $"Reproduction note: {marker} (plugin `{target.Name}`, severity `{ev.Severity}`).",
                };
            }

            return new DuplicateMatch { Action = "skip_open", IssueNumber = bestMatch.Number, IssueUrl = bestMatch.HtmlUrl, Reason = "open_duplicate", Similarity = bestSim };
        }

        var closedRecent = bestMatch.ClosedAt is null || (DateTimeOffset.UtcNow - bestMatch.ClosedAt.Value) < TimeSpan.FromDays(90);
        var installedVer = ParseComparableVersion(target.Version);
        var maxClosed = ExtractMaxVersion(new[] { bestMatch.Body });
        if (closedRecent && !(installedVer is not null && maxClosed is not null && installedVer > maxClosed))
        {
            return new DuplicateMatch { Action = "skip_closed", IssueNumber = bestMatch.Number, IssueUrl = bestMatch.HtmlUrl, Reason = "closed_duplicate", Similarity = bestSim };
        }

        return new DuplicateMatch { Action = "regression_new", IssueNumber = bestMatch.Number, IssueUrl = bestMatch.HtmlUrl, Reason = "closed_regression_newer_version", Similarity = bestSim };
    }

    private static Version? ExtractMaxVersion(IEnumerable<string> texts)
    {
        Version? best = null;
        foreach (var t in texts)
        {
            foreach (Match m in VersionRegex.Matches(t))
            {
                if (Version.TryParse(m.Groups[1].Value, out var v) && (best is null || v > best))
                {
                    best = v;
                }
            }
        }

        return best;
    }

    private static double ComputeTokenSimilarity(string a, string b)
    {
        var ta = Tokenize(a);
        var tb = Tokenize(b);
        if (ta.Count == 0 || tb.Count == 0)
        {
            return 0.0;
        }

        var overlap = ta.Count(tb.Contains);
        return (double)overlap / Math.Max(ta.Count, tb.Count);
    }

    private static HashSet<string> Tokenize(string text)
    {
        return Regex.Matches(text.ToLowerInvariant(), @"[a-z0-9_]{3,}")
            .Select(m => m.Value)
            .ToHashSet();
    }
}
