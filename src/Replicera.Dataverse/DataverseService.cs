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
    internal static readonly TimeSpan MaximumBackoff = TimeSpan.FromMinutes(1);
    internal static readonly TimeSpan MaximumRetryAfter = TimeSpan.FromMinutes(5);
    private readonly ServiceClient? client;
    private readonly Func<OrganizationRequest, CancellationToken, Task<OrganizationResponse>> execute;
    private readonly Func<TimeSpan, CancellationToken, Task> delay;
    private readonly int maxRetryCount;
    private readonly TimeSpan retryPause;
    private readonly Func<double> jitter;
    private readonly IDisposable? ownedResource;

    /// <summary>
    /// Wraps a Dataverse client. <paramref name="ownedResource"/>, such as the certificate the client
    /// signs in with, must outlive the client and is disposed with it.
    /// </summary>
    public DataverseService(ServiceClient client, IDisposable? ownedResource = null)
        : this(client.ExecuteAsync, Task.Delay, DefaultMaxRetryCount, DefaultRetryPause, ownedResource: ownedResource)
    {
        this.client = client;
    }

    internal DataverseService(
        Func<OrganizationRequest, CancellationToken, Task<OrganizationResponse>> execute,
        Func<TimeSpan, CancellationToken, Task> delay,
        int maxRetryCount = DefaultMaxRetryCount,
        TimeSpan? retryPause = null,
        Func<double>? jitter = null,
        IDisposable? ownedResource = null)
    {
        this.execute = execute;
        this.ownedResource = ownedResource;
        this.delay = delay;
        this.maxRetryCount = maxRetryCount;
        this.retryPause = retryPause ?? DefaultRetryPause;
        this.jitter = jitter ?? Random.Shared.NextDouble;
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

                await delay(RetryDelay(failure.RetryAfter, retry), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    // Service-protection limits specify their own wait, which is honored up to a ceiling so that
    // a malformed value cannot stall a run. Other retries back off exponentially with jitter so
    // that concurrent jobs do not retry in lockstep.
    private TimeSpan RetryDelay(TimeSpan? retryAfter, int retry)
    {
        if (retryAfter is { } requested)
        {
            return requested < MaximumRetryAfter ? requested : MaximumRetryAfter;
        }

        var exponential = retryPause.Ticks * Math.Pow(2, retry) * (0.8 + (0.4 * jitter()));
        return exponential < MaximumBackoff.Ticks ? TimeSpan.FromTicks((long)exponential) : MaximumBackoff;
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
        try
        {
            client?.Dispose();
        }
        finally
        {
            ownedResource?.Dispose();
        }

        return ValueTask.CompletedTask;
    }
}
