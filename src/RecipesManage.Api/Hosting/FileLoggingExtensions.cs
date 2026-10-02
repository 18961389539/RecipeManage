using RecipesManage.Infrastructure.Diagnostics;

namespace RecipesManage.Api;

/// <summary>
/// 文件日志的读取口径与 Backup / Maintenance 一致：每一项都有默认值，配置文件里不写也能跑。
/// 现场常常没人改 appsettings，而"没日志"这件事只会在要查事故的时候才暴露。
/// </summary>
public static class FileLoggingExtensions
{
    public static ILoggingBuilder AddBrmesFileLogging(this ILoggingBuilder logging, IConfiguration config)
    {
        var settings = SettingsFrom(config);
        return settings.Enabled ? logging.AddProvider(new RollingFileLoggerProvider(settings)) : logging;
    }

    /// <summary>
    /// 写歪的配置项（比如 <c>MinimumLevel</c> 拼错）退回默认值而不是让进程起不来：
    /// 日志这道防线本身不该成为停机原因。
    /// </summary>
    internal static FileLogSettings SettingsFrom(IConfiguration config)
    {
        var directory = config["Logging:File:Directory"];
        return new FileLogSettings
        {
            Enabled = !string.Equals(config["Logging:File:Enabled"], "false", StringComparison.OrdinalIgnoreCase),
            Directory = string.IsNullOrWhiteSpace(directory) ? "App_Data/logs" : directory.Trim(),
            MinimumLevel = Enum.TryParse<LogLevel>(config["Logging:File:MinimumLevel"], ignoreCase: true, out var level)
                && level != LogLevel.None
                ? level
                : LogLevel.Information,
            // 缺省不删旧日志；配了正数才按天数裁（见 FileLogSettings.RetainedDays）。
            RetainedDays = int.TryParse(config["Logging:File:RetainedDays"], out var days) ? Math.Max(0, days) : 0,
            QueueCapacity = int.TryParse(config["Logging:File:QueueCapacity"], out var capacity)
                ? Math.Max(128, capacity)
                : 20_000,
        };
    }
}
