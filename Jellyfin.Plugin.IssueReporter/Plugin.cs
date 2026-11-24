using System;
using System.Collections.Generic;
using Jellyfin.Plugin.IssueReporter.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.IssueReporter;

public sealed class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    public static readonly Guid PluginGuid = new("8c4e7f2a-1b9d-4e3a-9c5f-6a2b8d1e4f90");

    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    public static Plugin? Instance { get; private set; }

    public override string Name => "Issue Reporter";

    public override Guid Id => PluginGuid;

    public override string Description =>
        "Resource-efficient plugin log monitor with 4-layer secret redaction, GitHub deduplication, anti-spam protection, and template auto-fill.";

    public IEnumerable<PluginPageInfo> GetPages()
    {
        return new[]
        {
            new PluginPageInfo
            {
                Name = "IssueReporterConfig",
                EmbeddedResourcePath = $"{GetType().Namespace}.Configuration.configPage.html",
            },
        };
    }
}
