using System;
using System.IO;
using FluentAssertions;
using Jellyfin.Plugin.IssueReporter.Configuration;
using Jellyfin.Plugin.IssueReporter.Models;
using Jellyfin.Plugin.IssueReporter.Services;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Jellyfin.Plugin.IssueReporter.Tests;

public sealed class PrivacyAndFilterTests
{
    [Fact]
    public void Redactor_MasksAllFourLayers_AndLeakGuardCatchesSurvivingSecrets()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"jf_ir_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            File.WriteAllText(
                Path.Combine(tempDir, "OtherPlugin.xml"),
                "<PluginConfig><ApiSecret>SuperSecretPluginValue99</ApiSecret><GitHubUser>FaserF</GitHubUser></PluginConfig>");

            var cfg = new PluginConfiguration
            {
                GitHubUser = "FaserF",
                CustomRedactions = "myhome.example",
                CustomRedactionRegexes = @"ACCT-\d{4}",
                StrictEntropyGuard = true,
            };

            var redactor = new PrivacyRedactorService();
            redactor.UpdateConfig(cfg);
            var count = redactor.RefreshSecretsFromDirectory(tempDir);
            count.Should().Be(1);

            var raw = "Error calling https://user:pass@myhome.example/api with ghp_1234567890abcdefghij123456, SuperSecretPluginValue99, ACCT-4242, 192.168.1.50, admin@myhome.example, and FaserF/youtube-jellyfin";
            var res = redactor.Redact(raw);

            res.Text.Should().NotContain("SuperSecretPluginValue99");
            res.Text.Should().NotContain("myhome.example");
            res.Text.Should().NotContain("ghp_1234567890abcdefghij123456");
            res.Text.Should().NotContain("ACCT-4242");
            res.Text.Should().NotContain("192.168.1.50");
            res.Text.Should().Contain("FaserF/youtube-jellyfin");

            var (passed, _) = redactor.VerifyNoLeaks("Title", res.Text);
            passed.Should().BeTrue();

            var (failPassed, reasons) = redactor.VerifyNoLeaks("Title", "Leaked SuperSecretPluginValue99");
            failPassed.Should().BeFalse();
            reasons.Should().Contain("harvested_secret_survived");
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void ErrorFilter_IgnoresLocalTransientErrors_AndRespectsCustomPatterns()
    {
        var cfg = new PluginConfiguration
        {
            IgnoreNetworkErrors = true,
            IgnoreAuthErrors = true,
            IgnoreRateLimitErrors = true,
            IgnoreLifecycleErrors = true,
            CustomIgnorePatterns = @"expected_local_noise_\d+",
        };

        var filter = new ErrorFilterService();
        filter.UpdateConfig(cfg);

        filter.ShouldIgnore(new LogEvent { ExceptionType = "SocketException", Message = "Connection refused" }, cfg).Ignore.Should().BeTrue();
        filter.ShouldIgnore(new LogEvent { Message = "401 Unauthorized from upstream" }, cfg).Ignore.Should().BeTrue();
        filter.ShouldIgnore(new LogEvent { Message = "429 Too Many Requests" }, cfg).Ignore.Should().BeTrue();
        filter.ShouldIgnore(new LogEvent { ExceptionType = "OperationCanceledException", Message = "host is shutting down" }, cfg).Ignore.Should().BeTrue();
        filter.ShouldIgnore(new LogEvent { Message = "Hit expected_local_noise_12" }, cfg).Ignore.Should().BeTrue();

        var genuine = filter.ShouldIgnore(new LogEvent { ExceptionType = "NullReferenceException", Message = "Object reference not set" }, cfg);
        genuine.Ignore.Should().BeFalse();
    }
}
