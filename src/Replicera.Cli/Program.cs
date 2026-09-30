using Replicera.Cli;

using var cancellation = new CancellationTokenSource();
using var signals = new ShutdownSignals(cancellation);

return await CliApplication.RunAsync(
    args,
    Console.Out,
    Console.Error,
    cancellation.Token).ConfigureAwait(false);
