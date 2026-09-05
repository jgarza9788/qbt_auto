using NLog;
using NLog.Config;
using NLog.Layouts;
using NLog.Targets;
using LogLevel = NLog.LogLevel;

namespace Qbitflow.Web.Logging;

/// <summary>
/// Builds and mutates the NLog configuration at runtime. The log directory and the minimum
/// level are only known once the app has started (the directory comes from an env var, the
/// level from the SQLite AppSettings row), so the config is assembled in code rather than an
/// nlog.config file. The application rule's level is re-applied from the Settings page without
/// a restart via <see cref="ApplyMinLevel"/>.
/// </summary>
public static class NLogSetup
{
    /// <summary>Name given to the "*" rule whose level the Settings page adjusts.</summary>
    private const string AppRuleId = "qbitflow-app";

    private const string TextLayout =
        "${longdate}|${level:uppercase=true:padding=-5}|${logger:shortName=true}|${message}" +
        "${onexception:${newline}${exception:format=tostring}}";

    /// <summary>Maps the string stored in AppSettings.LogLevel (Microsoft naming) to an NLog level.</summary>
    public static LogLevel MapLevel(string? value) => value switch
    {
        "Trace" => LogLevel.Trace,
        "Debug" => LogLevel.Debug,
        "Information" => LogLevel.Info,
        "Warning" => LogLevel.Warn,
        "Error" => LogLevel.Error,
        "Critical" => LogLevel.Fatal,
        _ => LogLevel.Info
    };

    /// <param name="logDir">Directory for the rolling file (ignored when <paramref name="includeFile"/> is false).</param>
    /// <param name="minLevel">Minimum level for application loggers.</param>
    /// <param name="includeFile">False under the "Testing" environment so unit runs don't write files.</param>
    public static LoggingConfiguration Build(string logDir, LogLevel minLevel, bool includeFile)
    {
        var config = new LoggingConfiguration();

        // Console: JSON lines to stdout so `docker compose logs` stays machine-parseable.
        var console = new ConsoleTarget("console")
        {
            Layout = new JsonLayout
            {
                Attributes =
                {
                    new JsonAttribute("time", "${longdate}"),
                    new JsonAttribute("level", "${level:uppercase=true}"),
                    new JsonAttribute("logger", "${logger}"),
                    new JsonAttribute("message", "${message}"),
                    new JsonAttribute("exception", "${exception:format=tostring}")
                }
            }
        };
        config.AddTarget(console);

        var targets = new List<Target> { console };

        if (includeFile)
        {
            Directory.CreateDirectory(logDir);
            var file = new FileTarget("file")
            {
                // One file per day; NLog prunes matching files older than 7 days.
                FileName = Path.Combine(logDir, "qbitflow-${date:format=yyyy-MM-dd}.log"),
                Layout = TextLayout,
                MaxArchiveDays = 7,
                KeepFileOpen = true,
                AutoFlush = true,
                Encoding = System.Text.Encoding.UTF8
            };
            config.AddTarget(file);
            targets.Add(file);
        }

        // Keep the hosting startup banner ("Now listening on...", "Application started")
        // even though the rest of Microsoft.* is about to be quieted. Not final, so it
        // still hits the swallow rule below and isn't double-logged by the catch-all.
        var lifetime = new LoggingRule("Microsoft.Hosting.Lifetime", LogLevel.Info, LogLevel.Fatal, targets[0]);
        AddExtraTargets(lifetime, targets);
        config.LoggingRules.Add(lifetime);

        // Framework noise stays at Warning regardless of the app level: a targetless,
        // final rule swallows Trace..Info for these namespaces so they never reach the
        // catch-all rule below. Warn and above fall through and are logged.
        foreach (var pattern in new[] { "Microsoft.*", "System.Net.Http.*" })
        {
            var quiet = new LoggingRule { LoggerNamePattern = pattern, Final = true };
            quiet.SetLoggingLevels(LogLevel.Trace, LogLevel.Info);
            config.LoggingRules.Add(quiet);
        }

        var appRule = new LoggingRule("*", minLevel, LogLevel.Fatal, targets[0]) { RuleName = AppRuleId };
        AddExtraTargets(appRule, targets);
        config.LoggingRules.Add(appRule);

        return config;
    }

    private static void AddExtraTargets(LoggingRule rule, IReadOnlyList<Target> targets)
    {
        for (var i = 1; i < targets.Count; i++)
        {
            rule.Targets.Add(targets[i]);
        }
    }

    /// <summary>Re-point the application rule at a new minimum level with no restart.</summary>
    public static void ApplyMinLevel(LogLevel minLevel)
    {
        var config = LogManager.Configuration;
        if (config is null)
        {
            return;
        }

        foreach (var rule in config.LoggingRules)
        {
            if (rule.RuleName == AppRuleId)
            {
                rule.SetLoggingLevels(minLevel, LogLevel.Fatal);
            }
        }

        LogManager.ReconfigExistingLoggers();
    }
}
