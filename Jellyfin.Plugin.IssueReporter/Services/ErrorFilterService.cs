using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.IssueReporter.Configuration;
using Jellyfin.Plugin.IssueReporter.Models;

namespace Jellyfin.Plugin.IssueReporter.Services;

public sealed class ErrorFilterService
{
    private static readonly Regex NetworkRegex = new(
        @"\b(TimeoutException|TaskCanceledException|HttpRequestException|SocketException|NameResolutionFailure|Connection refused|Connection reset|Network is unreachable|No route to host|502 Bad Gateway|503 Service Unavailable|504 Gateway Timeout)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex AuthRegex = new(
        @"\b(401 Unauthorized|403 Forbidden|Invalid credentials|Authentication failed|Invalid API key|Token expired|Access denied)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex RateLimitRegex = new(
        @"\b(429 Too Many Requests|Rate limit exceeded|Retry-After|quota exceeded|secondary rate limit)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex LifecycleRegex = new(
        @"\b(OperationCanceledException|ApplicationStopping|host is shutting down|server is stopping|The operation was canceled)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private List<Regex> _customRegexes = new();

    public void UpdateConfig(PluginConfiguration config)
    {
        var list = new List<Regex>();
        foreach (var line in (config.CustomIgnorePatterns ?? string.Empty).Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0)
            {
                continue;
            }

            try
            {
                list.Add(new Regex(trimmed, RegexOptions.IgnoreCase | RegexOptions.Compiled));
            }
            catch (ArgumentException)
            {
                // Ignore invalid user regex without crashing pipeline
            }
        }

        _customRegexes = list;
    }

    public (bool Ignore, string Reason) ShouldIgnore(LogEvent ev, PluginConfiguration config, TargetOverride? targetOverride = null)
    {
        var blob = $"{ev.ExceptionType} {ev.Message} {ev.ExceptionText}";

        if (config.IgnoreNetworkErrors && NetworkRegex.IsMatch(blob))
        {
            return (true, "network_error");
        }

        if (config.IgnoreAuthErrors && AuthRegex.IsMatch(blob))
        {
            return (true, "auth_error");
        }

        if (config.IgnoreRateLimitErrors && RateLimitRegex.IsMatch(blob))
        {
            return (true, "rate_limit_error");
        }

        if (config.IgnoreLifecycleErrors && LifecycleRegex.IsMatch(blob))
        {
            return (true, "lifecycle_cancellation");
        }

        foreach (var rx in _customRegexes)
        {
            if (rx.IsMatch(blob))
            {
                return (true, "custom_ignore_pattern");
            }
        }

        if (targetOverride?.IgnorePatterns != null)
        {
            foreach (var pat in targetOverride.IgnorePatterns)
            {
                if (string.IsNullOrWhiteSpace(pat))
                {
                    continue;
                }

                try
                {
                    if (Regex.IsMatch(blob, pat, RegexOptions.IgnoreCase))
                    {
                        return (true, "target_ignore_pattern");
                    }
                }
                catch (ArgumentException)
                {
                    // Skip malformed per-target regex
                }
            }
        }

        return (false, string.Empty);
    }
}
