using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Channels;
using Jellyfin.Plugin.IssueReporter.Models;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.IssueReporter.Services;

public sealed class ResourceEfficientLogMonitor : ILoggerProvider
{
    public static readonly AsyncLocal<bool> InReporterPipeline = new();

    private const int MaxQueueSize = 250;
    private readonly Channel<LogEvent> _channel = Channel.CreateBounded<LogEvent>(new BoundedChannelOptions(MaxQueueSize)
    {
        FullMode = BoundedChannelFullMode.DropOldest,
        SingleReader = true,
        SingleWriter = false,
    });

    private volatile Dictionary<string, (string TargetKey, LogLevel MinLevel)> _assemblyRoutes = new(StringComparer.OrdinalIgnoreCase);

    public ChannelReader<LogEvent> Reader => _channel.Reader;

    public void UpdateTargets(IEnumerable<PluginTarget> targets)
    {
        var next = new Dictionary<string, (string TargetKey, LogLevel MinLevel)>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in targets)
        {
            if (!t.Enabled || string.IsNullOrWhiteSpace(t.FullRepo))
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(t.AssemblyName))
            {
                next[t.AssemblyName] = (t.TargetKey, t.MinSeverity);
            }

            if (!string.IsNullOrWhiteSpace(t.Name))
            {
                next[t.Name] = (t.TargetKey, t.MinSeverity);
            }
        }

        _assemblyRoutes = next;
    }

    public ILogger CreateLogger(string categoryName) => new FastPathPluginLogger(this, categoryName);

    public void Dispose()
    {
        _channel.Writer.TryComplete();
    }

    internal bool TryResolveTarget(string categoryName, LogLevel level, out string targetKey)
    {
        targetKey = string.Empty;
        if (level < LogLevel.Warning || InReporterPipeline.Value)
        {
            return false;
        }

        var routes = _assemblyRoutes;
        if (routes.Count == 0)
        {
            return false;
        }

        foreach (var kvp in routes)
        {
            if (categoryName.StartsWith(kvp.Key, StringComparison.OrdinalIgnoreCase))
            {
                if (level < kvp.Value.MinLevel)
                {
                    return false;
                }

                targetKey = kvp.Value.TargetKey;
                return true;
            }
        }

        return false;
    }

    internal void Enqueue(string targetKey, string categoryName, LogLevel level, string message, Exception? exception)
    {
        // Loop-breaker for self-monitoring: never re-report Leak Guard or internal pipeline notices
        if (categoryName.StartsWith("Jellyfin.Plugin.IssueReporter", StringComparison.OrdinalIgnoreCase) &&
            (message.Contains("Leak Guard", StringComparison.OrdinalIgnoreCase) ||
             message.Contains("Anti-flood breaker", StringComparison.OrdinalIgnoreCase) ||
             message.Contains("GitHub API", StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        var ev = new LogEvent
        {
            TargetKey = targetKey,
            CategoryName = categoryName,
            Level = level,
            Severity = level >= LogLevel.Critical ? "CRITICAL" : level >= LogLevel.Error ? "ERROR" : "WARNING",
            Message = message.Length > 2000 ? message[..2000] : message,
            ExceptionText = exception?.ToString() is { Length: > 4000 } s ? s[..4000] : (exception?.ToString() ?? string.Empty),
            ExceptionType = exception?.GetType().Name ?? string.Empty,
            TimestampUtc = DateTimeOffset.UtcNow.ToString("O"),
        };

        _channel.Writer.TryWrite(ev);
    }

    private sealed class FastPathPluginLogger : ILogger
    {
        private readonly ResourceEfficientLogMonitor _owner;
        private readonly string _category;

        public FastPathPluginLogger(ResourceEfficientLogMonitor owner, string category)
        {
            _owner = owner;
            _category = category;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!_owner.TryResolveTarget(_category, logLevel, out var targetKey))
            {
                return;
            }

            var msg = formatter(state, exception);
            if (string.IsNullOrWhiteSpace(msg) && exception is null)
            {
                return;
            }

            _owner.Enqueue(targetKey, _category, logLevel, msg, exception);
        }
    }
}
