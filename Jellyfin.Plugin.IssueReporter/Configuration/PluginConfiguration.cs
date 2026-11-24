using System.Collections.Generic;
using Jellyfin.Plugin.IssueReporter.Models;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.IssueReporter.Configuration;

public sealed class PluginConfiguration : BasePluginConfiguration
{
    // Authentication: "github_app" | "device_flow" | "pat" | "none"
    public string AuthMethod { get; set; } = "none";

    public string GitHubToken { get; set; } = string.Empty;

    public string GitHubUser { get; set; } = string.Empty;

    public string OAuthClientId { get; set; } = string.Empty;

    public string AppId { get; set; } = string.Empty;

    public string InstallationId { get; set; } = string.Empty;

    public string PrivateKeyPem { get; set; } = string.Empty;

    public string FallbackAuthMethod { get; set; } = "none";

    public string FallbackToken { get; set; } = string.Empty;

    public string FallbackUser { get; set; } = string.Empty;

    // Owner filtering: "self" | "custom" | "manual"
    public string OwnerFilterMode { get; set; } = "self";

    public string OwnerAllowlist { get; set; } = "FaserF";

    public bool DryRun { get; set; } = true;

    // "Warning" | "Error" | "Critical"
    public string MinSeverity { get; set; } = "Error";

    // Universal custom mappings: lines of "PluginNameOrAssembly=owner/repo"
    public string CustomRepoMappings { get; set; } = string.Empty;

    // Anti-spam & deduplication
    public int MinOccurrencesError { get; set; } = 1;

    public int MinOccurrencesWarning { get; set; } = 3;

    public int OccurrenceWindowMinutes { get; set; } = 15;

    public int CooldownHours { get; set; } = 24;

    public int MaxIssuesPerHour { get; set; } = 5;

    public int MaxIssuesPerDay { get; set; } = 15;

    public int MaxIssuesPerRepoDay { get; set; } = 2;

    public bool SkipOutdated { get; set; } = true;

    public bool AllowDevBuilds { get; set; } = true;

    public bool CommentOnNewerVersion { get; set; } = true;

    public double FuzzySimilarityThreshold { get; set; } = 0.65;

    // Local / transient error filters
    public bool IgnoreNetworkErrors { get; set; } = true;

    public bool IgnoreAuthErrors { get; set; } = true;

    public bool IgnoreRateLimitErrors { get; set; } = true;

    public bool IgnoreLifecycleErrors { get; set; } = true;

    public string CustomIgnorePatterns { get; set; } = string.Empty;

    // 4-Layer Privacy & Redaction
    public string CustomRedactions { get; set; } = string.Empty;

    public string CustomRedactionRegexes { get; set; } = string.Empty;

    public bool StrictEntropyGuard { get; set; } = true;

    // Per-plugin overrides (List<TargetOverride> for XmlSerializer compatibility)
    public List<TargetOverride> TargetOverrides { get; set; } = new();
}
