using NLog;

namespace MeduzaRepost;

public static class LogExtensions
{
    public static ILogger WithPrefix(this ILogger logger, string prefix) => LogManager.GetLogger(prefix);
}