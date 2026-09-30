namespace Replicera.Core.Configuration;

public static class CommandTimeouts
{
    /// <summary>Converts a timeout to whole seconds for database drivers, where zero means no limit.</summary>
    public static int ToSeconds(TimeSpan timeout)
    {
        if (timeout < TimeSpan.Zero || timeout > DestinationConfiguration.MaximumCommandTimeout)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), timeout, "Command timeouts must be between zero and 24 hours.");
        }

        return (int)Math.Ceiling(timeout.TotalSeconds);
    }
}
