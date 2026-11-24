using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Jellyfin.Plugin.IssueReporter.Configuration;
using Jellyfin.Plugin.IssueReporter.Models;

namespace Jellyfin.Plugin.IssueReporter.Services;

public sealed class PrivacyRedactorService
{
    private static readonly string[] SensitiveKeywords =
    {
        "token", "secret", "password", "passwd", "pwd", "apikey", "api_key",
        "key", "cookie", "auth", "credential", "bearer", "webhook", "clientid",
        "client_id", "private", "pin", "passphrase", "session", "email", "mail",
    };

    private static readonly HashSet<string> SafeStopwords = new(StringComparer.OrdinalIgnoreCase)
    {
        "true", "false", "none", "null", "localhost", "127.0.0.1", "0.0.0.0",
        "jellyfin", "default", "stable", "warning", "error", "critical", "debug",
        "info", "https", "http", "github.com", "api.github.com", "devices", "device",
        "problem", "version", "assistant", "running", "causing", "information",
        "installation", "summary", "severity", "channel", "logger", "automated",
        "integration", "plugin", "plugins", "admin", "system",
    };

    private static readonly (string Name, Regex Pattern, string Replacement)[] Scrubbers =
    {
        ("private_key", new Regex(@"-----BEGIN [A-Z0-9 ]*PRIVATE KEY-----[\s\S]*?-----END [A-Z0-9 ]*PRIVATE KEY-----", RegexOptions.Compiled), "[REDACTED_PRIVATE_KEY]"),
        ("github_token", new Regex(@"\b(?:ghp|gho|ghu|ghs|ghr)_[A-Za-z0-9_]{20,255}\b|\bgithub_pat_[A-Za-z0-9_]{20,255}\b", RegexOptions.Compiled), "[REDACTED_TOKEN]"),
        ("jwt", new Regex(@"\beyJ[A-Za-z0-9_-]{8,}\.eyJ[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}\b", RegexOptions.Compiled), "[REDACTED_TOKEN]"),
        ("known_api_token", new Regex(@"\b(?:sk-[A-Za-z0-9_-]{20,}|xox[baprs]-[A-Za-z0-9-]{10,}|AKIA[0-9A-Z]{16}|\d{8,10}:[A-Za-z0-9_-]{35})\b", RegexOptions.Compiled), "[REDACTED_TOKEN]"),
        ("auth_header", new Regex(@"((?:Authorization|X-Api-Key|X-Auth-Token|Cookie|Set-Cookie)\s*[:=]\s*(?:Bearer|Basic|Token)?\s*)([^\r\n;""']+)", RegexOptions.IgnoreCase | RegexOptions.Compiled), "$1[REDACTED_TOKEN]"),
        ("bearer_inline", new Regex(@"(\bBearer\s+)([A-Za-z0-9._~+/=-]{8,})", RegexOptions.IgnoreCase | RegexOptions.Compiled), "$1[REDACTED_TOKEN]"),
        ("url_credentials", new Regex(@"\b([a-zA-Z][a-zA-Z0-9+.-]*://)([^:@/\s]+):([^@/\s]+)@", RegexOptions.Compiled), "$1[REDACTED_CRED]@"),
        ("kv_secret", new Regex(@"((?:password|passwd|pwd|secret|token|api[_-]?key|access_token|refresh_token|client_secret|pin|passphrase|session_id|session_token|cookie)\s*[:=]\s*['""]?)([^\s'"",;}\]]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled), "$1[REDACTED_SECRET]"),
        ("private_domain", new Regex(@"\b[a-zA-Z0-9](?:[a-zA-Z0-9-]*[a-zA-Z0-9])?(?:\.[a-zA-Z0-9-]+)*\.(?:tailnet\.[a-zA-Z0-9.-]+|ts\.net|local|lan|home\.arpa|internal|fritz\.box)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled), "[REDACTED_HOST]"),
        ("email", new Regex(@"\b[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}\b", RegexOptions.Compiled), "[REDACTED_EMAIL]"),
        ("ipv4", new Regex(@"\b(?:(?:25[0-5]|2[0-4]\d|1?\d?\d)\.){3}(?:25[0-5]|2[0-4]\d|1?\d?\d)\b", RegexOptions.Compiled), "[REDACTED_IP]"),
        ("mac", new Regex(@"\b(?:[0-9A-Fa-f]{2}[:-]){5}[0-9A-Fa-f]{2}\b", RegexOptions.Compiled), "[REDACTED_MAC]"),
        ("uuid", new Regex(@"\b[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\b", RegexOptions.Compiled), "[REDACTED_UUID]"),
        ("unix_user_path", new Regex(@"(?:/home|/Users)/[^/\s]+", RegexOptions.Compiled), "/home/[REDACTED_USER]"),
        ("win_user_path", new Regex(@"C:\\Users\\[^\\\s]+", RegexOptions.IgnoreCase | RegexOptions.Compiled), @"C:\Users\[REDACTED_USER]"),
    };

    private static readonly Regex HighEntropyCandidateRegex = new(@"\b[A-Za-z0-9+/=_-]{24,}\b", RegexOptions.Compiled);
    private static readonly Regex FingerprintLineRegex = new(@"<!--\s*jellyfin-issue-reporter:fingerprint=[^\n]*-->", RegexOptions.Compiled);

    private readonly object _lock = new();
    private HashSet<string> _harvestedLiterals = new(StringComparer.Ordinal);
    private Regex? _harvestedRegex;
    private List<string> _customLiterals = new();
    private List<Regex> _customLiteralRegexes = new();
    private List<Regex> _customRegexes = new();
    private bool _strictEntropy = true;
    private string _publicGitHubUser = string.Empty;

    public void UpdateConfig(PluginConfiguration config)
    {
        var literals = (config.CustomRedactions ?? string.Empty)
            .Split(new[] { ',', ';', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim())
            .Where(s => s.Length >= 3)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var litRegexes = literals
            .Select(w => new Regex(Regex.Escape(w), RegexOptions.IgnoreCase | RegexOptions.Compiled))
            .ToList();

        var customRx = new List<Regex>();
        foreach (var line in (config.CustomRedactionRegexes ?? string.Empty).Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0)
            {
                continue;
            }

            try
            {
                customRx.Add(new Regex(trimmed, RegexOptions.Compiled));
            }
            catch (ArgumentException)
            {
                // Ignore invalid user regex
            }
        }

        lock (_lock)
        {
            _customLiterals = literals;
            _customLiteralRegexes = litRegexes;
            _customRegexes = customRx;
            _strictEntropy = config.StrictEntropyGuard;
            _publicGitHubUser = (config.GitHubUser ?? string.Empty).Trim();
            if (!string.IsNullOrEmpty(_publicGitHubUser))
            {
                _harvestedLiterals.Remove(_publicGitHubUser);
                RebuildHarvestedRegexLocked();
            }
        }
    }

    public int RefreshSecretsFromDirectory(string pluginConfigsDir)
    {
        var found = new HashSet<string>(StringComparer.Ordinal);
        if (Directory.Exists(pluginConfigsDir))
        {
            foreach (var xmlPath in Directory.EnumerateFiles(pluginConfigsDir, "*.xml", SearchOption.TopDirectoryOnly))
            {
                HarvestFromXml(xmlPath, found);
            }
        }

        lock (_lock)
        {
            if (!string.IsNullOrEmpty(_publicGitHubUser))
            {
                found.Remove(_publicGitHubUser);
            }

            _harvestedLiterals = found;
            RebuildHarvestedRegexLocked();
            return _harvestedLiterals.Count;
        }
    }

    public void AddHarvestedSecretsForTest(IEnumerable<string> secrets)
    {
        lock (_lock)
        {
            foreach (var s in secrets)
            {
                if (!string.IsNullOrWhiteSpace(s) && s.Length >= 6 && !SafeStopwords.Contains(s))
                {
                    _harvestedLiterals.Add(s.Trim());
                }
            }

            RebuildHarvestedRegexLocked();
        }
    }

    public RedactionResult Redact(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return new RedactionResult { Text = string.Empty };
        }

        var current = text;
        var count = 0;
        var categories = new List<string>();

        Regex? harvested;
        List<Regex> customLit;
        List<Regex> customRx;
        lock (_lock)
        {
            harvested = _harvestedRegex;
            customLit = _customLiteralRegexes;
            customRx = _customRegexes;
        }

        if (harvested is not null)
        {
            var n = 0;
            current = harvested.Replace(current, _ =>
            {
                n++;
                return "[REDACTED_SECRET]";
            });
            if (n > 0)
            {
                count += n;
                categories.Add("harvested_secret");
            }
        }

        foreach (var (name, pattern, repl) in Scrubbers)
        {
            var n = 0;
            var updated = pattern.Replace(current, m =>
            {
                n++;
                return m.Result(repl);
            });
            if (n > 0)
            {
                current = updated;
                count += n;
                if (!categories.Contains(name))
                {
                    categories.Add(name);
                }
            }
        }

        foreach (var rx in customLit)
        {
            var n = 0;
            current = rx.Replace(current, _ =>
            {
                n++;
                return "[REDACTED_CUSTOM]";
            });
            if (n > 0)
            {
                count += n;
                if (!categories.Contains("custom_literal"))
                {
                    categories.Add("custom_literal");
                }
            }
        }

        foreach (var rx in customRx)
        {
            var n = 0;
            current = rx.Replace(current, _ =>
            {
                n++;
                return "[REDACTED_CUSTOM]";
            });
            if (n > 0)
            {
                count += n;
                if (!categories.Contains("custom_regex"))
                {
                    categories.Add("custom_regex");
                }
            }
        }

        return new RedactionResult
        {
            Text = current,
            ReplacementsCount = count,
            MatchedCategories = categories,
        };
    }

    public (bool Passed, List<string> Reasons) VerifyNoLeaks(string title, string body)
    {
        var combined = $"{title}\n{body}";
        var withoutMarker = FingerprintLineRegex.Replace(combined, string.Empty);
        var reasons = new List<string>();

        HashSet<string> harvested;
        List<string> customLit;
        bool strictEntropy;
        lock (_lock)
        {
            harvested = new HashSet<string>(_harvestedLiterals, StringComparer.Ordinal);
            customLit = new List<string>(_customLiterals);
            strictEntropy = _strictEntropy;
        }

        if (harvested.Any(lit => !string.IsNullOrEmpty(lit) && withoutMarker.Contains(lit, StringComparison.Ordinal)))
        {
            reasons.Add("harvested_secret_survived");
        }

        if (customLit.Any(w => !string.IsNullOrEmpty(w) && withoutMarker.Contains(w, StringComparison.OrdinalIgnoreCase)))
        {
            reasons.Add("custom_redaction_survived");
        }

        foreach (var (name, pattern, _) in Scrubbers)
        {
            if (name is "uuid" or "unix_user_path" or "win_user_path" or "ipv4" or "mac")
            {
                if (name == "ipv4" && pattern.IsMatch(withoutMarker))
                {
                    reasons.Add("pattern_survived:ipv4");
                }

                continue;
            }

            if (pattern.IsMatch(withoutMarker))
            {
                reasons.Add($"pattern_survived:{name}");
            }
        }

        if (strictEntropy)
        {
            foreach (Match m in HighEntropyCandidateRegex.Matches(withoutMarker))
            {
                var tok = m.Value;
                if (tok.Contains('_', StringComparison.Ordinal) && tok == tok.ToLowerInvariant())
                {
                    continue;
                }

                if (ShannonEntropy(tok) > 4.55)
                {
                    reasons.Add("high_entropy_token_suspected");
                    break;
                }
            }
        }

        return (reasons.Count == 0, reasons);
    }

    private void RebuildHarvestedRegexLocked()
    {
        if (_harvestedLiterals.Count == 0)
        {
            _harvestedRegex = null;
            return;
        }

        var ordered = _harvestedLiterals.OrderByDescending(s => s.Length).Select(Regex.Escape);
        _harvestedRegex = new Regex(string.Join("|", ordered), RegexOptions.Compiled);
    }

    private static void HarvestFromXml(string path, HashSet<string> found)
    {
        try
        {
            var doc = XDocument.Load(path);
            foreach (var el in doc.Descendants())
            {
                if (el.HasElements)
                {
                    continue;
                }

                var key = el.Name.LocalName.ToLowerInvariant();
                if (key is "githubuser" or "fallbackuser" or "owner" or "version" or "name")
                {
                    continue;
                }

                if (!SensitiveKeywords.Any(kw => key.Contains(kw, StringComparison.Ordinal)))
                {
                    continue;
                }

                var val = el.Value.Trim();
                if (val.Length >= 6 && !SafeStopwords.Contains(val) && !Regex.IsMatch(val, @"^\d{1,5}$"))
                {
                    found.Add(val);
                }
            }
        }
        catch (Exception)
        {
            // Ignore malformed plugin XML config files
        }
    }

    private static double ShannonEntropy(string value)
    {
        if (value.Length == 0)
        {
            return 0.0;
        }

        var counts = new Dictionary<char, int>();
        foreach (var ch in value)
        {
            counts[ch] = counts.TryGetValue(ch, out var c) ? c + 1 : 1;
        }

        var len = (double)value.Length;
        var entropy = 0.0;
        foreach (var count in counts.Values)
        {
            var p = count / len;
            entropy -= p * Math.Log2(p);
        }

        return entropy;
    }
}
