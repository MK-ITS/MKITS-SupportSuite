// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) MK-ITSolutions and contributors

using System.Globalization;
using Microsoft.Extensions.Logging;

namespace MKITS.SupportSuite.Agent.Core.Logging;

/// <summary>
/// The agents' log: one line per entry – <c>[timestamp] LEVEL Category: message (Exception: message)</c> – handed to
/// a platform sink (Windows: rotating file in the protected data folder; Linux: standard error, i.e. the journal,
/// which adds its own timestamps). Exceptions are logged by type and message only; stack traces from <c>Debug</c> on.
/// Callers never log secrets (tokens, codes, keys, payloads). Also the logger factory the agents pass to the core
/// (a single provider; <see cref="AddProvider"/> is not supported).
/// </summary>
public sealed class LineLoggerProvider(Action<string> write, LogLevel minimum = LogLevel.Information, bool timestamps = true, TimeProvider? timeProvider = null)
    : ILoggerProvider, ILoggerFactory
{
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    private LogLevel Minimum => minimum;

    /// <summary>Parses a configured level (<c>"debug"</c>, <c>"Warning"</c>, …); anything else gives <paramref name="fallback"/>.</summary>
    public static LogLevel ParseLevel(string? value, LogLevel fallback = LogLevel.Information) =>
        Enum.TryParse<LogLevel>(value, ignoreCase: true, out var level) && Enum.IsDefined(level) && !int.TryParse(value, out _) ? level : fallback;

    public ILogger CreateLogger(string categoryName) => new LineLogger(this, ShortName(categoryName));

    public void AddProvider(ILoggerProvider provider) => throw new NotSupportedException("The agent log has exactly one sink.");

    public void Dispose()
    {
    }

    /// <summary><c>MKITS.SupportSuite.Agent.Core.Devices.DeviceAgent</c> → <c>DeviceAgent</c>.</summary>
    private static string ShortName(string category)
    {
        var dot = category.LastIndexOf('.');
        return dot >= 0 && dot < category.Length - 1 ? category[(dot + 1)..] : category;
    }

    private void Write(string category, LogLevel level, string message, Exception? exception)
    {
        var line = new System.Text.StringBuilder();
        if (timestamps)
        {
            line.Append(_time.GetUtcNow().ToString("yyyy-MM-dd HH:mm:ss'Z' ", CultureInfo.InvariantCulture));
        }

        line.Append(Level(level)).Append(' ').Append(category).Append(": ").Append(message);
        if (exception is not null)
        {
            line.Append(" (").Append(exception.GetType().Name).Append(": ").Append(exception.Message).Append(')');
            if (minimum <= LogLevel.Debug && exception.StackTrace is { } stack)
            {
                line.AppendLine().Append(stack);
            }
        }

        try
        {
            write(line.ToString());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ObjectDisposedException)
        {
            // logging must never break the agent
        }
    }

    private static string Level(LogLevel level) => level switch
    {
        LogLevel.Trace => "TRACE",
        LogLevel.Debug => "DEBUG",
        LogLevel.Information => "INFO",
        LogLevel.Warning => "WARN",
        LogLevel.Error => "ERROR",
        _ => "CRIT",
    };

    private sealed class LineLogger(LineLoggerProvider provider, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None && logLevel >= provider.Minimum;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);
            if (IsEnabled(logLevel))
            {
                provider.Write(category, logLevel, formatter(state, exception), exception);
            }
        }
    }
}
