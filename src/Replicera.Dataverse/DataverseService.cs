using System.ServiceModel;
using Microsoft.PowerPlatform.Dataverse.Client;
using Microsoft.Xrm.Sdk;
using Replicera.Dataverse.Errors;

namespace Replicera.Dataverse;

public interface IDataverseService
{
    Task<OrganizationResponse> ExecuteAsync(
        OrganizationRequest request,
        CancellationToken cancellationToken);
}

public sealed class DataverseService : IDataverseService, IAsyncDisposable
{
    private const int DefaultMaxRetryCount = 3;
    private static readonly TimeSpan DefaultRetryPause = TimeSpan.FromSeconds(2);
    private readonly ServiceClient? client;
    private readonly Func<OrganizationRequest, CancellationToken, Task<OrganizationResponse>> execute;
    private readonly Func<TimeSpan, CancellationToken, Task> delay;
    private readonly int maxRetryCount;
    private readonly TimeSpan retryPause;

    public DataverseService(ServiceClient client)
        : this(client.ExecuteAsync, Task.Delay, DefaultMaxRetryCount, DefaultRetryPause)
    {
        this.client = client;
    }

    internal DataverseService(
        Func<OrganizationRequest, CancellationToken, Task<OrganizationResponse>> execute,
        Func<TimeSpan, CancellationToken, Task> delay,
        int maxRetryCount = DefaultMaxRetryCount,
        TimeSpan? retryPause = null)
    {
        this.execute = execute;
        this.delay = delay;
        this.maxRetryCount = maxRetryCount;
        this.retryPause = retryPause ?? DefaultRetryPause;
    }

    public async Task<OrganizationResponse> ExecuteAsync(
        OrganizationRequest request,
        CancellationToken cancellationToken)
    {
        for (var retry = 0; ; retry++)
        {
            try
            {
                return await execute(request, cancellationToken).ConfigureAwait(false);
            }
            catch (FaultException<OrganizationServiceFault> exception)
            {
                var classified = DataverseFaultClassifier.Classify(request, exception.Detail);
                if (retry >= maxRetryCount || classified.Category is not (Core.Errors.ErrorCategory.Throttling or Core.Errors.ErrorCategory.SourceConnectivity))
                {
                    throw classified;
                }

                var retryAfter = DataverseFaultClassifier.TryGetRetryAfter(exception.Detail)
                    ?? TimeSpan.FromTicks(retryPause.Ticks * (1L << retry));
                await delay(retryAfter, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    public ValueTask DisposeAsync()
    {
        client?.Dispose();
        return ValueTask.CompletedTask;
    }
}
