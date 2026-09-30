using System.ServiceModel;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.PowerPlatform.Dataverse.Client.Utils;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Replicera.Core.Errors;

namespace Replicera.Dataverse.Tests;

public sealed class DataverseServiceRetryTests
{
    [Fact]
    public async Task DisposeAsync_DisposesOwnedResource()
    {
        var resource = new TrackingDisposable();
        var service = new DataverseService(
            (_, _) => Task.FromResult<OrganizationResponse>(new WhoAmIResponse()),
            (_, _) => Task.CompletedTask,
            ownedResource: resource);

        await service.DisposeAsync();

        Assert.True(resource.Disposed);
    }

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
            retryPause: TimeSpan.FromSeconds(2),
            jitter: () => 0.5);

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

    [Fact]
    public async Task ExecuteAsync_DoesNotRetryFaultWithoutTransientEvidence()
    {
        var attempts = 0;
        var service = new DataverseService(
            (_, _) =>
            {
                attempts++;
                throw new FaultException<OrganizationServiceFault>(new OrganizationServiceFault { ErrorCode = unchecked((int)0x80040216) });
            },
            (_, _) => throw new InvalidOperationException("Delay should not be called."));

        var exception = await Assert.ThrowsAsync<RepliceraException>(
            () => service.ExecuteAsync(new WhoAmIRequest(), CancellationToken.None));

        Assert.Equal(ErrorCategory.SourceConnectivity, exception.Category);
        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task ExecuteAsync_ReportsPrivilegeDeniedAsAuthorizationWithoutRetry()
    {
        var attempts = 0;
        var service = new DataverseService(
            (_, _) =>
            {
                attempts++;
                throw new FaultException<OrganizationServiceFault>(new OrganizationServiceFault { ErrorCode = unchecked((int)0x80040220) });
            },
            (_, _) => throw new InvalidOperationException("Delay should not be called."));

        var exception = await Assert.ThrowsAsync<RepliceraException>(
            () => service.ExecuteAsync(new WhoAmIRequest(), CancellationToken.None));

        Assert.Equal(ErrorCategory.Authorization, exception.Category);
        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task ExecuteAsync_DoesNotRepeatNonIdempotentRequestAfterServerFailure()
    {
        var attempts = 0;
        var service = new DataverseService(
            (_, _) =>
            {
                attempts++;
                throw new FaultException<OrganizationServiceFault>(Fault(503));
            },
            (_, _) => throw new InvalidOperationException("Delay should not be called."));

        var exception = await Assert.ThrowsAsync<RepliceraException>(
            () => service.ExecuteAsync(new CreateRequest { Target = new Entity("role") }, CancellationToken.None));

        Assert.Equal(ErrorCategory.SourceConnectivity, exception.Category);
        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task ExecuteAsync_RepeatsNonIdempotentRequestWhenThrottled()
    {
        var attempts = 0;
        var service = new DataverseService(
            (_, _) =>
            {
                attempts++;
                if (attempts == 1)
                {
                    throw new FaultException<OrganizationServiceFault>(Fault(429));
                }

                return Task.FromResult<OrganizationResponse>(new AssociateResponse());
            },
            (_, _) => Task.CompletedTask);

        _ = await service.ExecuteAsync(new AssociateRequest(), CancellationToken.None);

        Assert.Equal(2, attempts);
    }

    [Fact]
    public async Task ExecuteAsync_RetriesTransportFailuresWrappedBySdk()
    {
        var attempts = 0;
        var service = new DataverseService(
            (_, _) =>
            {
                attempts++;
                return attempts switch
                {
                    1 => throw new HttpRequestException("connection reset"),
                    2 => throw new DataverseOperationException("wrapped", new TimeoutException()),
                    _ => Task.FromResult<OrganizationResponse>(new WhoAmIResponse())
                };
            },
            (_, _) => Task.CompletedTask);

        _ = await service.ExecuteAsync(new WhoAmIRequest(), CancellationToken.None);

        Assert.Equal(3, attempts);
    }

    [Fact]
    public async Task ExecuteAsync_ReportsExhaustedTransportFailureAsSourceConnectivity()
    {
        var attempts = 0;
        var service = new DataverseService(
            (_, _) =>
            {
                attempts++;
                throw new TaskCanceledException("client timeout");
            },
            (_, _) => Task.CompletedTask,
            maxRetryCount: 2);

        var exception = await Assert.ThrowsAsync<RepliceraException>(
            () => service.ExecuteAsync(new WhoAmIRequest(), CancellationToken.None));

        Assert.Equal(ErrorCategory.SourceConnectivity, exception.Category);
        Assert.Equal(3, attempts);
    }

    [Fact]
    public async Task ExecuteAsync_ClassifiesFaultWrappedBySdk()
    {
        var attempts = 0;
        var service = new DataverseService(
            (_, _) =>
            {
                attempts++;
                throw new DataverseOperationException(
                    "wrapped",
                    new FaultException<OrganizationServiceFault>(new OrganizationServiceFault { ErrorCode = unchecked((int)0x80040220) }));
            },
            (_, _) => throw new InvalidOperationException("Delay should not be called."));

        var exception = await Assert.ThrowsAsync<RepliceraException>(
            () => service.ExecuteAsync(new WhoAmIRequest(), CancellationToken.None));

        Assert.Equal(ErrorCategory.Authorization, exception.Category);
        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task ExecuteAsync_DoesNotRetryWhenCallerCancels()
    {
        using var cancellation = new CancellationTokenSource();
        var attempts = 0;
        var service = new DataverseService(
            (_, token) =>
            {
                attempts++;
                cancellation.Cancel();
                token.ThrowIfCancellationRequested();
                return Task.FromResult<OrganizationResponse>(new WhoAmIResponse());
            },
            (_, _) => throw new InvalidOperationException("Delay should not be called."));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.ExecuteAsync(new WhoAmIRequest(), cancellation.Token));

        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task ExecuteAsync_DoesNotRepeatNonIdempotentRequestAfterTransportFailure()
    {
        var attempts = 0;
        var service = new DataverseService(
            (_, _) =>
            {
                attempts++;
                throw new HttpRequestException("connection reset");
            },
            (_, _) => throw new InvalidOperationException("Delay should not be called."));

        var exception = await Assert.ThrowsAsync<RepliceraException>(
            () => service.ExecuteAsync(new CreateRequest { Target = new Entity("role") }, CancellationToken.None));

        Assert.Equal(ErrorCategory.SourceConnectivity, exception.Category);
        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task ExecuteAsync_RethrowsUnrecognizedExceptionsUnchanged()
    {
        var service = new DataverseService(
            (_, _) => throw new InvalidOperationException("not a Dataverse failure"),
            (_, _) => throw new InvalidOperationException("Delay should not be called."));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.ExecuteAsync(new WhoAmIRequest(), CancellationToken.None));

        Assert.Equal("not a Dataverse failure", exception.Message);
    }

    [Fact]
    public async Task ExecuteAsync_CapsRetryAfterAndBackoff()
    {
        var delays = new List<TimeSpan>();
        var attempts = 0;
        var service = new DataverseService(
            (_, _) =>
            {
                attempts++;
                if (attempts == 1)
                {
                    var fault = Fault(429);
                    fault.ErrorDetails["Retry-After"] = TimeSpan.FromHours(6);
                    throw new FaultException<OrganizationServiceFault>(fault);
                }

                throw new FaultException<OrganizationServiceFault>(Fault(503));
            },
            (duration, _) =>
            {
                delays.Add(duration);
                return Task.CompletedTask;
            },
            maxRetryCount: 2,
            retryPause: TimeSpan.FromSeconds(50),
            jitter: () => 1.0);

        _ = await Assert.ThrowsAsync<RepliceraException>(
            () => service.ExecuteAsync(new WhoAmIRequest(), CancellationToken.None));

        Assert.Equal([DataverseService.MaximumRetryAfter, DataverseService.MaximumBackoff], delays);
    }

    [Theory]
    [InlineData(0.0, 1.6)]
    [InlineData(1.0, 2.4)]
    public async Task ExecuteAsync_AppliesJitterToExponentialBackoff(double jitter, double expectedSeconds)
    {
        var delays = new List<TimeSpan>();
        var service = new DataverseService(
            (_, _) => throw new FaultException<OrganizationServiceFault>(Fault(503)),
            (duration, _) =>
            {
                delays.Add(duration);
                return Task.CompletedTask;
            },
            maxRetryCount: 1,
            retryPause: TimeSpan.FromSeconds(2),
            jitter: () => jitter);

        _ = await Assert.ThrowsAsync<RepliceraException>(
            () => service.ExecuteAsync(new WhoAmIRequest(), CancellationToken.None));

        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), Assert.Single(delays));
    }

    private static OrganizationServiceFault Fault(int statusCode)
    {
        var fault = new OrganizationServiceFault { ErrorCode = -1 };
        fault.ErrorDetails["ApiExceptionHttpStatusCode"] = statusCode;
        return fault;
    }

    private sealed class TrackingDisposable : IDisposable
    {
        public bool Disposed { get; private set; }

        public void Dispose() => Disposed = true;
    }
}
