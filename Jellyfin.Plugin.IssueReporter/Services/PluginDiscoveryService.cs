using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.IssueReporter.Configuration;
using Jellyfin.Plugin.IssueReporter.Models;
using MediaBrowser.Common.Plugins;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.IssueReporter.Services;

public sealed class PluginDiscoveryService
{
    private static readonly Regex GitHubUrlRegex = new(
        @"https?://github\.com/([A-Za-z0-9_.-]+)/([A-Za-z0-9_.-]+?)(?:\.git|/issues/?|/.*)?$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Dictionary<string, string> KnownFaserFRepos = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Jellyfin.Plugin.IssueReporter"] = "FaserF/jellyfin-plugin-issue-reporter",
        ["Issue Reporter"] = "FaserF/jellyfin-plugin-issue-reporter",
        ["Jellyfin.Plugin.YouTube"] = "FaserF/youtube-jellyfin",
        ["YouTube"] = "FaserF/youtube-jellyfin",
        ["Jellyfin.Plugin.Emby"] = "FaserF/emby-jellyfin",
        ["Emby"] = "FaserF/emby-jellyfin",
        ["Jellyfin.Plugin.Crunchyroll"] = "FaserF/crunchyroll-jellyfin",
        ["Crunchyroll"] = "FaserF/crunchyroll-jellyfin",
        ["Jellyfin.Plugin.AnimeKai"] = "FaserF/animekai-jellyfin",
        ["AnimeKai"] = "FaserF/animekai-jellyfin",
        ["Jellyfin.Plugin.AiCompanion"] = "FaserF/jellyfin-ai-companion",
        ["AI Companion"] = "FaserF/jellyfin-ai-companion",
        ["Jellyfin.Plugin.MediathekViewDL"] = "FaserF/jellyfin-plugin-MediathekViewDL",
        ["MediathekViewDL"] = "FaserF/jellyfin-plugin-MediathekViewDL",
        ["Jellyfin.Plugin.AiSearch"] = "FaserF/jellyfin-plugin-ai-search",
        ["AI Search"] = "FaserF/jellyfin-plugin-ai-search",
        ["Jellyfin.Plugin.Ratings"] = "FaserF/jellyfin-plugin-ratings",
        ["Ratings"] = "FaserF/jellyfin-plugin-ratings",
    };

    public static (string? Owner, string? Repo) ParseGitHubRepo(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return (null, null);
        }

        var trimmed = raw.Trim();
        if (!trimmed.Contains("://", StringComparison.Ordinal) && trimmed.Contains('/', StringComparison.Ordinal))
        {
            var parts = trimmed.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 2)
            {
                return (parts[0], parts[1].TrimEnd(".git".ToCharArray()));
            }
        }

        var m = GitHubUrlRegex.Match(trimmed);
        return m.Success ? (m.Groups[1].Value, m.Groups[2].Value) : (null, null);
    }

    public List<PluginTarget> DiscoverPlugins(IEnumerable<IPlugin> installedPlugins, PluginConfiguration config)
    {
        var customMappings = ParseCustomMappings(config.CustomRepoMappings);
        var targets = new List<PluginTarget>();

        foreach (var plugin in installedPlugins)
        {
            var asm = plugin.GetType().Assembly;
            var asmName = asm.GetName().Name ?? plugin.Name;
            var version = plugin.Version?.ToString() ?? "0.0.0";
            var (metaOwner, metaRepoUrl) = ReadMetaJson(plugin.AssemblyFilePath);
            var asmRepoUrl = ReadAssemblyRepoUrl(asm);

            var rawRepo = ResolveRepoCandidate(plugin.Name, asmName, customMappings, metaRepoUrl, asmRepoUrl);
            var (owner, repo) = ParseGitHubRepo(rawRepo);
            if (owner is null && !string.IsNullOrWhiteSpace(metaOwner))
            {
                owner = metaOwner;
            }

            var channel = DetectChannel(version);
            targets.Add(new PluginTarget
            {
                TargetKey = $"plugin:{asmName.ToLowerInvariant()}",
                PluginId = plugin.Id.ToString(),
                Name = plugin.Name,
                AssemblyName = asmName,
                Version = version,
                RepoOwner = owner,
                RepoName = repo,
                RepoUrl = owner is not null && repo is not null ? $"https://github.com/{owner}/{repo}" : null,
                Channel = channel,
            });
        }

        return ApplyPolicies(targets, config);
    }

    public static List<PluginTarget> ApplyPolicies(List<PluginTarget> targets, PluginConfiguration config)
    {
        var mode = (config.OwnerFilterMode ?? "self").ToLowerInvariant();
        var allowOwners = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (mode == "self")
        {
            var authOwner = (config.GitHubUser ?? string.Empty).Trim();
            if (!string.IsNullOrEmpty(authOwner))
            {
                allowOwners.Add(authOwner);
            }
            else if (!string.IsNullOrWhiteSpace(config.OwnerAllowlist))
            {
                foreach (var o in config.OwnerAllowlist.Split(',', StringSplitOptions.RemoveEmptyEntries))
                {
                    allowOwners.Add(o.Trim());
                }
            }
        }
        else if (mode == "custom")
        {
            foreach (var o in (config.OwnerAllowlist ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                allowOwners.Add(o.Trim());
            }
        }

        var defaultLevel = ParseLogLevel(config.MinSeverity);
        var defaultMode = config.DryRun ? "dry_run" : "approval";

        foreach (var t in targets)
        {
            var ov = config.TargetOverrides.Find(o => string.Equals(o.TargetKey, t.TargetKey, StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(ov?.RepoOverride))
            {
                var (o, r) = ParseGitHubRepo(ov.RepoOverride);
                if (o is not null && r is not null)
                {
                    t.RepoOwner = o;
                    t.RepoName = r;
                    t.RepoUrl = $"https://github.com/{o}/{r}";
                }
            }

            t.OwnerMatched = t.RepoOwner is not null && allowOwners.Contains(t.RepoOwner);
            if (ov?.Enabled is not null)
            {
                t.Enabled = ov.Enabled.Value;
            }
            else if (mode == "manual")
            {
                t.Enabled = false;
            }
            else
            {
                t.Enabled = t.OwnerMatched && t.FullRepo is not null;
            }

            t.Mode = !string.IsNullOrWhiteSpace(ov?.Mode) ? ov.Mode : defaultMode;
            t.MinSeverity = !string.IsNullOrWhiteSpace(ov?.MinSeverity)
                ? ParseLogLevel(ov.MinSeverity)
                : defaultLevel;
        }

        return targets;
    }

    public static LogLevel ParseLogLevel(string? raw)
    {
        return (raw ?? "Error").Trim().ToLowerInvariant() switch
        {
            "warning" or "warn" => LogLevel.Warning,
            "critical" => LogLevel.Critical,
            _ => LogLevel.Error,
        };
    }

    private static string? ResolveRepoCandidate(
        string pluginName,
        string asmName,
        Dictionary<string, string> customMappings,
        string? metaRepoUrl,
        string? asmRepoUrl)
    {
        if (customMappings.TryGetValue(asmName, out var c1))
        {
            return c1;
        }

        if (customMappings.TryGetValue(pluginName, out var c2))
        {
            return c2;
        }

        if (!string.IsNullOrWhiteSpace(metaRepoUrl))
        {
            return metaRepoUrl;
        }

        if (!string.IsNullOrWhiteSpace(asmRepoUrl))
        {
            return asmRepoUrl;
        }

        if (KnownFaserFRepos.TryGetValue(asmName, out var f1))
        {
            return f1;
        }

        if (KnownFaserFRepos.TryGetValue(pluginName, out var f2))
        {
            return f2;
        }

        return null;
    }

    private static Dictionary<string, string> ParseCustomMappings(string raw)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in (raw ?? string.Empty).Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var idx = line.IndexOf('=', StringComparison.Ordinal);
            if (idx <= 0)
            {
                continue;
            }

            var k = line[..idx].Trim();
            var v = line[(idx + 1)..].Trim();
            if (k.Length > 0 && v.Length > 0)
            {
                map[k] = v;
            }
        }

        return map;
    }

    private static (string? Owner, string? RepoUrl) ReadMetaJson(string? assemblyPath)
    {
        if (string.IsNullOrWhiteSpace(assemblyPath))
        {
            return (null, null);
        }

        var dir = Path.GetDirectoryName(assemblyPath);
        if (string.IsNullOrEmpty(dir))
        {
            return (null, null);
        }

        var metaPath = Path.Combine(dir, "meta.json");
        if (!File.Exists(metaPath))
        {
            return (null, null);
        }

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(metaPath));
            var root = doc.RootElement;
            var owner = root.TryGetProperty("owner", out var o) ? o.GetString() : null;
            var src = root.TryGetProperty("sourceUrl", out var s) ? s.GetString() : null;
            return (owner, src);
        }
        catch (Exception)
        {
            return (null, null);
        }
    }

    private static string? ReadAssemblyRepoUrl(Assembly asm)
    {
        foreach (var attr in asm.GetCustomAttributes<AssemblyMetadataAttribute>())
        {
            if (attr.Key.Equals("RepositoryUrl", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(attr.Value))
            {
                return attr.Value;
            }
        }

        return null;
    }

    private static string DetectChannel(string version)
    {
        var v = version.ToLowerInvariant();
        if (v.Contains("dev", StringComparison.Ordinal) || v.Contains("alpha", StringComparison.Ordinal))
        {
            return "dev";
        }

        if (v.Contains("beta", StringComparison.Ordinal) || v.Contains("rc", StringComparison.Ordinal))
        {
            return "beta";
        }

        return "stable";
    }
}
