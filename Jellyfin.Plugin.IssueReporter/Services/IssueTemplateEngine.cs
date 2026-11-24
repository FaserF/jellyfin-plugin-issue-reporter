using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.IssueReporter.Configuration;
using Jellyfin.Plugin.IssueReporter.Models;

namespace Jellyfin.Plugin.IssueReporter.Services;

public sealed class IssueTemplateEngine
{
    private readonly GitHubApiClient _github;

    public IssueTemplateEngine(GitHubApiClient github)
    {
        _github = github;
    }

    public async Task<RenderedIssue> RenderIssueAsync(
        PluginConfiguration config,
        PluginTarget target,
        LogEvent ev,
        string sanitizedMessage,
        string sanitizedException,
        string fingerprint,
        int occurrences,
        string jellyfinVersion,
        int? regressionOf = null,
        CancellationToken ct = default)
    {
        var repo = target.FullRepo ?? string.Empty;
        var summaryLine = sanitizedMessage.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries) is { Length: > 0 } lines
            ? lines[0].Trim()
            : ev.CategoryName;
        var headline = string.IsNullOrEmpty(ev.ExceptionType) ? summaryLine : $"{ev.ExceptionType}: {summaryLine}";
        if (headline.Length > 86)
        {
            headline = headline[..86];
        }

        var title = regressionOf is not null ? $"[Regression #{regressionOf}]: {headline}" : $"[Bug]: {headline}";
        var logBlock = $"[{ev.Severity}] [{ev.CategoryName}] {sanitizedMessage}";
        if (!string.IsNullOrWhiteSpace(sanitizedException))
        {
            logBlock += $"\n{sanitizedException}";
        }

        var templates = (!string.IsNullOrWhiteSpace(target.RepoOwner) && !string.IsNullOrWhiteSpace(target.RepoName))
            ? await _github.FetchBugTemplatesAsync(config, target.RepoOwner, target.RepoName, ct)
            : new List<(string Name, string Content)>();

        var footer = $"\n\n---\n*Reported automatically by [jellyfin-plugin-issue-reporter](https://github.com/FaserF/jellyfin-plugin-issue-reporter). All credentials, tokens, hosts, and personal identifiers were redacted prior to submission.*\n<!-- {DeduplicationService.FingerprintPrefix}{fingerprint} version={target.Version} target={target.TargetKey} -->";

        if (templates.Count > 0 && templates[0].Name.EndsWith(".yml", StringComparison.OrdinalIgnoreCase))
        {
            var body = RenderSimpleYamlTemplate(templates[0].Content, target, ev, summaryLine, logBlock, occurrences, jellyfinVersion, regressionOf) + footer;
            return new RenderedIssue
            {
                Repo = repo,
                Title = title,
                Body = body,
                Labels = new List<string> { "bug" },
                TemplateUsed = $".github/ISSUE_TEMPLATE/{templates[0].Name}",
                Fingerprint = fingerprint,
            };
        }

        var sb = new StringBuilder();
        sb.AppendLine("### Describe the bug");
        sb.AppendLine($"Automated error detection for Jellyfin plugin **{target.Name}** (`{target.AssemblyName}`).");
        sb.AppendLine();
        if (regressionOf is not null)
        {
            sb.AppendLine($"- **Regression of:** #{regressionOf}");
        }

        sb.AppendLine($"- **Severity:** `{ev.Severity}`");
        sb.AppendLine($"- **Plugin Version:** `{target.Version}` (`{target.Channel}`)");
        sb.AppendLine($"- **Jellyfin Version:** `{jellyfinVersion}`");
        sb.AppendLine($"- **Logger:** `{ev.CategoryName}`");
        sb.AppendLine($"- **Occurrences:** `{occurrences}`");
        sb.AppendLine();
        sb.AppendLine("### Logs");
        sb.AppendLine("```txt");
        sb.AppendLine(logBlock);
        sb.AppendLine("```");
        sb.AppendLine();
        sb.AppendLine("### Environment");
        sb.AppendLine($"- Jellyfin Server: `{jellyfinVersion}`");
        sb.AppendLine($"- OS / Runtime: `{RuntimeInformation.OSDescription}` / `{RuntimeInformation.FrameworkDescription}`");

        return new RenderedIssue
        {
            Repo = repo,
            Title = title,
            Body = sb.ToString().TrimEnd() + footer,
            Labels = new List<string> { "bug" },
            TemplateUsed = "default",
            Fingerprint = fingerprint,
        };
    }

    private static string RenderSimpleYamlTemplate(
        string yamlContent,
        PluginTarget target,
        LogEvent ev,
        string summaryLine,
        string logBlock,
        int occurrences,
        string jellyfinVersion,
        int? regressionOf)
    {
        var sb = new StringBuilder();
        var sections = Regex.Split(yamlContent, @"(?m)^\s*-\s+type:\s+");
        foreach (var sec in sections)
        {
            var labelMatch = Regex.Match(sec, @"(?m)^\s*label:\s*[""']?(.+?)[""']?\s*$");
            if (!labelMatch.Success)
            {
                continue;
            }

            var label = labelMatch.Groups[1].Value.Trim();
            var low = label.ToLowerInvariant();
            string answer;
            if (low.Contains("jellyfin", StringComparison.Ordinal) && low.Contains("version", StringComparison.Ordinal))
            {
                answer = jellyfinVersion;
            }
            else if (low.Contains("plugin", StringComparison.Ordinal) && low.Contains("version", StringComparison.Ordinal))
            {
                answer = target.Version;
            }
            else if (low.Contains("log", StringComparison.Ordinal) || low.Contains("trace", StringComparison.Ordinal))
            {
                answer = $"```txt\n{logBlock}\n```";
            }
            else if (low.Contains("os", StringComparison.Ordinal) || low.Contains("environment", StringComparison.Ordinal))
            {
                answer = $"{RuntimeInformation.OSDescription} ({RuntimeInformation.FrameworkDescription})";
            }
            else
            {
                var regNote = regressionOf is not null ? $"\n- **Regression of:** #{regressionOf}" : string.Empty;
                answer = $"Automated error detection for **{target.Name}** (`{target.Version}`, `{ev.Severity}`, `{occurrences}x`){regNote}\n- **Summary:** {summaryLine}";
            }

            sb.AppendLine($"### {label}");
            sb.AppendLine();
            sb.AppendLine(answer);
            sb.AppendLine();
        }

        return sb.Length > 0
            ? sb.ToString().TrimEnd()
            : $"### Describe the bug\n\n{summaryLine}\n\n```txt\n{logBlock}\n```";
    }
}
