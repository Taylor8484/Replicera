using System.Runtime.InteropServices;

namespace Replicera.Cli;

/// <summary>
/// Turns the first SIGINT, SIGTERM, or SIGQUIT into a cancellation request so an active
/// synchronization rolls back through its normal path and the worker stops cleanly. A later
/// signal is left to the runtime's default handling, which ends the process immediately.
/// </summary>
internal sealed class ShutdownSignals : IDisposable
{
    private readonly CancellationTokenSource cancellation;
    private readonly PosixSignalRegistration[] registrations;
    private int received;

    public ShutdownSignals(CancellationTokenSource cancellation)
    {
        this.cancellation = cancellation;
        registrations =
        [
            PosixSignalRegistration.Create(PosixSignal.SIGINT, Handle),
            PosixSignalRegistration.Create(PosixSignal.SIGTERM, Handle),
            PosixSignalRegistration.Create(PosixSignal.SIGQUIT, Handle)
        ];
    }

    public void Dispose()
    {
        foreach (var registration in registrations)
        {
            registration.Dispose();
        }
    }

    /// <summary>Returns whether the default termination should be suppressed.</summary>
    internal bool OnSignal()
    {
        if (Interlocked.Increment(ref received) != 1)
        {
            return false;
        }

        cancellation.Cancel();
        return true;
    }

    private void Handle(PosixSignalContext context) => context.Cancel = OnSignal();
}
