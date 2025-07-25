﻿using System.Reflection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.UserSecrets;
using Microsoft.Extensions.Logging;
using Microsoft.IO;
using NLog;
using NLog.Extensions.Logging;
using NLog.Filters;
using NLog.Targets;
using NLog.Targets.Wrappers;
using ILogger = NLog.ILogger;
using LogLevel = NLog.LogLevel;

namespace MeduzaRepost;

public static class Config
{
    private const string Version = "1.0.9";
    
    private static readonly IConfigurationRoot config;
    private static readonly string secretsPath;

    internal static string CurrentLogPath => Path.GetFullPath("./logs/bot.log");
    internal static string ConfigFolderName => config.GetValue(nameof(ConfigFolderName), "meduza-bot")!;
    internal static int MaxDescriptionLength => config.GetValue(nameof(MaxDescriptionLength), 1500);
    internal static TimeSpan PublicLimiterTimeSpan => config.GetValue(nameof(PublicLimiterTimeSpan), TimeSpan.FromHours(1));
    internal static TimeSpan WatchdogThreshold => config.GetValue(nameof(WatchdogThreshold), TimeSpan.FromHours(6));
    internal static TimeSpan UpdateFetchThreshold => config.GetValue(nameof(WatchdogThreshold), TimeSpan.FromMinutes(5));
    internal static int PublicLimiterItemCount => config.GetValue(nameof(PublicLimiterItemCount), 3);
    internal static bool WatchdogEnabled => config.GetValue(nameof(WatchdogEnabled), true);

    internal static readonly CancellationTokenSource Cts = new();
    internal static readonly ILogger Log, SpamLog;
    internal static readonly ILoggerFactory LoggerFactory;
    internal static readonly RecyclableMemoryStreamManager MemoryStreamManager = new();

    static Config()
    {
        Log = GetLog();
        SpamLog = LogManager.GetLogger("spam");
        LoggerFactory = new NLogLoggerFactory();
        Log.Info("Log path: " + CurrentLogPath);
        config = new ConfigurationBuilder()
            .AddEnvironmentVariables()
            .AddUserSecrets(Assembly.GetExecutingAssembly())
            .Build();
        if (Assembly.GetExecutingAssembly().GetCustomAttribute<UserSecretsIdAttribute>() is not UserSecretsIdAttribute attribute)
            throw new InvalidOperationException("Failed to find UserSecretsId attribute");

        secretsPath = Path.GetDirectoryName(PathHelper.GetSecretsPathFromSecretsId(attribute.UserSecretsId))!;
    }

    private static string? InteractiveGet(string param)
    {
        Console.Write($"Enter {param}: ");
        return Console.ReadLine() is { Length: > 0 } result ? result : null;
    }

    public static string? Get(string param) => param switch
    {
        "session_pathname" => Path.Combine(secretsPath, "WTSession.bin"),
        "app_version" => Version,
        "session_key" => null,
        "user_id" => null,
        "server_address" => null,
        "device_model" => null,
        "system_version" => null,
        "system_lang_code" => null,
        "lang_pack" => null,
        "lang_code" => null,
        "init_params" => null,
        _ => config.GetValue<string?>(param) ?? InteractiveGet(param),
    };

    private static ILogger GetLog()
    {
        var loggingConfig = new NLog.Config.LoggingConfiguration();
        var fileTarget = new FileTarget("logfile")
        {
            FileName = CurrentLogPath,
            ArchiveFileName = CurrentLogPath,
            ArchiveEvery = FileArchivePeriod.Day,
            ArchiveSuffixFormat = ".{1:yyyyMMdd}.{0:0}",
            MaxArchiveDays = 7,
            KeepFileOpen = true,
            AutoFlush = false,
            OpenFileFlushTimeout = 1,
            Layout = "${longdate} ${sequenceid:padding=6} ${level:uppercase=true:padding=-5}" +
                     "${when:when=logger!='default':inner= [${logger}]} ${message} " +
                     "${onexception:${newline}${exception:format=ToString}:when=not contains('${exception:format=ShortType}','TaskCanceledException')}",
        };
        var asyncFileTarget = new AsyncTargetWrapper(fileTarget)
        {
            TimeToSleepBetweenBatches = 0,
            OverflowAction = AsyncTargetWrapperOverflowAction.Block,
            BatchSize = 500,
        };
        var consoleTarget = new ColoredConsoleTarget("logconsole")
        {
            Layout = "${longdate} ${level:uppercase=true:padding=-5}" +
                     "${when:when=logger!='default':inner= [${logger}]} ${message} " +
                     "${onexception:${newline}${exception:format=Message}:when=not contains('${exception:format=ShortType}','TaskCanceledException')}",
        };
#if DEBUG
        loggingConfig.AddRule(LogLevel.Warn, LogLevel.Fatal, consoleTarget, "spam");
        loggingConfig.AddRule(LogLevel.Trace, LogLevel.Fatal, consoleTarget, "mastodon"); // only echo messages from default logger to the console
        loggingConfig.AddRule(LogLevel.Trace, LogLevel.Fatal, consoleTarget, "telegram"); // only echo messages from default logger to the console
        loggingConfig.AddRule(LogLevel.Trace, LogLevel.Fatal, consoleTarget, "default"); // only echo messages from default logger to the console
#else
        loggingConfig.AddRule(LogLevel.Error, LogLevel.Fatal, consoleTarget, "spam");
        loggingConfig.AddRule(LogLevel.Info, LogLevel.Fatal, consoleTarget, "telegram");
        loggingConfig.AddRule(LogLevel.Info, LogLevel.Fatal, consoleTarget, "mastodon");
        loggingConfig.AddRule(LogLevel.Info, LogLevel.Fatal, consoleTarget, "default");
#endif
        loggingConfig.AddRule(LogLevel.Debug, LogLevel.Fatal, asyncFileTarget);

        var ignoreFilter1 = new ConditionBasedFilter { Condition = "contains('${message}','TaskCanceledException')"!, Action = FilterResult.Ignore, };
        foreach (var rule in loggingConfig.LoggingRules)
        {
            rule.Filters.Add(ignoreFilter1);
            rule.FilterDefaultAction = FilterResult.Log;
        }
        LogManager.Configuration = loggingConfig;
        return LogManager.GetLogger("default");
    }
}