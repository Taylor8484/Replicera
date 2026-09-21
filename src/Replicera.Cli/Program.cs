using Replicera.Cli;

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    cancellation.Cancel();
};

return await CliApplication.RunAsync(
    args,
    Console.Out,
    Console.Error,
    cancellation.Token).ConfigureAwait(false);
