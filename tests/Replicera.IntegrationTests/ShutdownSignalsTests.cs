using Replicera.Cli;

namespace Replicera.IntegrationTests;

public sealed class ShutdownSignalsTests
{
    [Fact]
    public void FirstSignalRequestsCancellationAndLaterSignalsTerminate()
    {
        using var cancellation = new CancellationTokenSource();
        using var signals = new ShutdownSignals(cancellation);

        Assert.True(signals.OnSignal());
        Assert.True(cancellation.IsCancellationRequested);
        Assert.False(signals.OnSignal());
        Assert.False(signals.OnSignal());
    }
}
