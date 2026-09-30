using System.Net.Sockets;
using System.ServiceModel;
using Microsoft.PowerPlatform.Dataverse.Client;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Replicera.Core.Errors;
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
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested
                && ClassifyFailure(request, exception) is { } failure)
            {
                if (retry >= maxRetryCount || !failure.CanRetry)
                {
                    throw failure.Exception;
                }

                var retryAfter = failure.RetryAfter ?? TimeSpan.FromTicks(retryPause.Ticks * (1L << retry));
                await delay(retryAfter, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static Failure? ClassifyFailure(OrganizationRequest request, Exception exception)
    {
        var chain = ExceptionChain(exception).ToArray();
        var fault = chain.OfType<FaultException<OrganizationServiceFault>>().FirstOrDefault();
        if (fault is not null)
        {
            return new Failure(
                DataverseFaultClassifier.Classify(request, fault.Detail),
                CanRetry(request, fault.Detail),
                DataverseFaultClassifier.TryGetRetryAfter(fault.Detail));
        }

        return chain.Any(IsTransportFailure)
            ? new Failure(
                new RepliceraException(
                    ErrorCategory.SourceConnectivity,
                    "Dataverse could not be reached after the configured retries.",
                    exception),
                IsIdempotent(request),
                null)
            : null;
    }

    // The caller's token is checked before classification, so a cancellation seen here is a
    // client-side timeout rather than a requested stop.
    private static bool IsTransportFailure(Exception exception) => exception
        is HttpRequestException
        or TimeoutException
        or IOException
        or SocketException
        or OperationCanceledException
        || (exception is CommunicationException && exception is not FaultException);

    private static IEnumerable<Exception> ExceptionChain(Exception exception)
    {
        var pending = new Stack<Exception>([exception]);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            yield return current;
            if (current is AggregateException aggregate)
            {
                foreach (var inner in aggregate.InnerExceptions)
                {
                    pending.Push(inner);
                }
            }
            else if (current.InnerException is not null)
            {
                pending.Push(current.InnerException);
            }
        }
    }

    private sealed record Failure(RepliceraException Exception, bool CanRetry, TimeSpan? RetryAfter);

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
