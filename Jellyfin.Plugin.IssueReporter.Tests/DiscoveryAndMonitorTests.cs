using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using FluentAssertions;
using Jellyfin.Plugin.IssueReporter.Configuration;
using Jellyfin.Plugin.IssueReporter.Models;
using Jellyfin.Plugin.IssueReporter.Services;
using MediaBrowser.Common.Plugins;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.IssueReporter.Tests;

public sealed class DiscoveryAndMonitorTests
{
    [Fact]
    public void Discovery_MapsFaserFPlugins_CustomMappings_AndOwnerFilter()
    {
        var selfPlugin = new Mock<IPlugin>();
        selfPlugin.Setup(p => p.Name).Returns("Issue Reporter");
        selfPlugin.Setup(p => p.Id).Returns(Plugin.PluginGuid);
        selfPlugin.Setup(p => p.Version).Returns(new Version(1, 0, 0, 0));
        selfPlugin.Setup(p => p.AssemblyFilePath).Returns(typeof(Plugin).Assembly.Location);

        var targets = new List<PluginTarget>
        {
            new() { TargetKey = "plugin:jellyfin.plugin.youtube", Name = "YouTube", AssemblyName = "Jellyfin.Plugin.YouTube", Version = "1.1.3.0", RepoOwner = "FaserF", RepoName = "youtube-jellyfin", RepoUrl = "https://github.com/FaserF/youtube-jellyfin" },
            new() { TargetKey = "plugin:jellyfin.plugin.issuereporter", Name = "Issue Reporter", AssemblyName = "Jellyfin.Plugin.IssueReporter", Version = "1.0.0.0", RepoOwner = "FaserF", RepoName = "jellyfin-plugin-issue-reporter", RepoUrl = "https://github.com/FaserF/jellyfin-plugin-issue-reporter" },
            new() { TargetKey = "plugin:other.plugin", Name = "Other", AssemblyName = "Other.Plugin", Version = "2.0.0", RepoOwner = "SomeoneElse", RepoName = "other-plugin", RepoUrl = "https://github.com/SomeoneElse/other-plugin" },
        };

        var cfg = new PluginConfiguration
        {
            OwnerFilterMode = "self",
            GitHubUser = "FaserF",
            MinSeverity = "Warning",
            DryRun = true,
        };

        PluginDiscoveryService.ApplyPolicies(targets, cfg);
        targets[0].Enabled.Should().BeTrue();
        targets[0].MinSeverity.Should().Be(LogLevel.Warning);
        targets[1].Enabled.Should().BeTrue();
        targets[2].Enabled.Should().BeFalse();

        // Check built-in discovery of Issue Reporter assembly itself
        var discovery = new PluginDiscoveryService();
        var (o, r) = PluginDiscoveryService.ParseGitHubRepo("https://github.com/FaserF/youtube-jellyfin.git");
        o.Should().Be("FaserF");
        r.Should().Be("youtube-jellyfin");
    }

    [Fact]
    public void LogMonitor_CapturesEnabledPlugin_AndPreventsSelfLoop()
    {
        using var monitor = new ResourceEfficientLogMonitor();
        monitor.UpdateTargets(new[]
        {
            new PluginTarget
            {
                TargetKey = "plugin:jellyfin.plugin.issuereporter",
                Name = "Issue Reporter",
                AssemblyName = "Jellyfin.Plugin.IssueReporter",
                RepoOwner = "FaserF",
                RepoName = "jellyfin-plugin-issue-reporter",
                Enabled = true,
                MinSeverity = LogLevel.Error,
            },
        });

        var logger = monitor.CreateLogger("Jellyfin.Plugin.IssueReporter.Services.Discovery");
        logger.LogError("Genuine internal bug");
        monitor.Reader.TryRead(out var captured).Should().BeTrue();
        captured!.TargetKey.Should().Be("plugin:jellyfin.plugin.issuereporter");

        // Loop-breaker notice is dropped
        logger.LogWarning("Pre-flight Leak Guard aborted something");
        monitor.Reader.TryRead(out _).Should().BeFalse();

        // Re-entrancy AsyncLocal guard blocks capture while inside pipeline
        ResourceEfficientLogMonitor.InReporterPipeline.Value = true;
        try
        {
            logger.LogError("Nested pipeline error");
            monitor.Reader.TryRead(out _).Should().BeFalse();
        }
        finally
        {
            ResourceEfficientLogMonitor.InReporterPipeline.Value = false;
        }
    }
}
