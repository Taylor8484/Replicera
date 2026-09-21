using System.ServiceModel;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Xrm.Sdk;
using Replicera.Core.Errors;

namespace Replicera.Dataverse.Tests;

public sealed class DataverseServiceRetryTests
{
    [Fact]
    public async Task ExecuteAsync_HonorsRetryAfterAndRecovers()
    {
        var attempts = 0;
        var delays = new List<TimeSpan>();
        var service = new DataverseService(
            (_, _) =>
            {
                attempts++;
                if (attempts <= 2)
                {
                    var fault = Fault(429);
                    fault.ErrorDetails["Retry-After"] = TimeSpan.FromSeconds(3);
                    throw new FaultException<OrganizationServiceFault>(fault);
                }

                return Task.FromResult<OrganizationResponse>(new WhoAmIResponse());
            },
            (duration, _) =>
            {
                delays.Add(duration);
                return Task.CompletedTask;
            });

        _ = await service.ExecuteAsync(new WhoAmIRequest(), CancellationToken.None);

        Assert.Equal(3, attempts);
        Assert.Equal([TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(3)], delays);
    }

    [Fact]
    public async Task ExecuteAsync_UsesBoundedExponentialDelayAndClassifiesExhaustion()
    {
        var attempts = 0;
        var delays = new List<TimeSpan>();
        var service = new DataverseService(
            (_, _) =>
            {
                attempts++;
                throw new FaultException<OrganizationServiceFault>(Fault(503));
            },
            (duration, _) =>
            {
                delays.Add(duration);
                return Task.CompletedTask;
            },
            maxRetryCount: 2,
            retryPause: TimeSpan.FromSeconds(2));

        var exception = await Assert.ThrowsAsync<RepliceraException>(
            () => service.ExecuteAsync(new WhoAmIRequest(), CancellationToken.None));

        Assert.Equal(ErrorCategory.SourceConnectivity, exception.Category);
        Assert.Equal(3, attempts);
        Assert.Equal([TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4)], delays);
    }

    [Fact]
    public async Task ExecuteAsync_DoesNotRetryAuthorizationFailure()
    {
        var attempts = 0;
        var service = new DataverseService(
            (_, _) =>
            {
                attempts++;
                throw new FaultException<OrganizationServiceFault>(Fault(403));
            },
            (_, _) => throw new InvalidOperationException("Delay should not be called."));

        var exception = await Assert.ThrowsAsync<RepliceraException>(
            () => service.ExecuteAsync(new WhoAmIRequest(), CancellationToken.None));

        Assert.Equal(ErrorCategory.Authorization, exception.Category);
        Assert.Equal(1, attempts);
    }

    private static OrganizationServiceFault Fault(int statusCode)
    {
        var fault = new OrganizationServiceFault { ErrorCode = -1 };
        fault.ErrorDetails["ApiExceptionHttpStatusCode"] = statusCode;
        return fault;
    }
}
