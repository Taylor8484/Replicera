using System.ServiceModel;
using Microsoft.PowerPlatform.Dataverse.Client;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
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
                if (retry >= maxRetryCount || !CanRetry(request, exception.Detail))
                {
                    throw classified;
                }

                var retryAfter = DataverseFaultClassifier.TryGetRetryAfter(exception.Detail)
                    ?? TimeSpan.FromTicks(retryPause.Ticks * (1L << retry));
                await delay(retryAfter, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    // A throttled request is rejected before Dataverse processes it, so it is always safe to
    // repeat. Other transient failures may occur after the server applied the change, so only
    // requests that can be repeated without a second effect are retried in that case.
    private static bool CanRetry(OrganizationRequest request, OrganizationServiceFault fault) =>
        DataverseFaultClassifier.IsThrottled(fault)
        || (DataverseFaultClassifier.IsTransient(fault) && IsIdempotent(request));

    private static bool IsIdempotent(OrganizationRequest request) =>
        request is not (CreateRequest or AssociateRequest);

    public ValueTask DisposeAsync()
    {
        client?.Dispose();
        return ValueTask.CompletedTask;
    }
}
