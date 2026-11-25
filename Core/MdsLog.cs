using BepInEx.Logging;

namespace MDSFramework;

internal static class MdsLog
{
    private static ManualLogSource? logger;

    internal static void Initialize(ManualLogSource source)
    {
        logger = source;
    }

    internal static void Info(string message)
    {
        logger?.LogInfo(message);
    }

    internal static void Warn(string message)
    {
        logger?.LogWarning(message);
    }

    internal static void Error(string message)
    {
        logger?.LogError(message);
    }

    internal static void Debug(string message)
    {
        if (MdsConfig.VerboseLogging != null && MdsConfig.VerboseLogging.Value)
        {
            logger?.LogDebug(message);
        }
    }
}
