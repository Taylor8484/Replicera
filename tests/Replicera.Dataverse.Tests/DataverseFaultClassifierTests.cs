using Microsoft.Crm.Sdk.Messages;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Replicera.Core.Errors;
using Replicera.Dataverse.Errors;

namespace Replicera.Dataverse.Tests;

public sealed class DataverseFaultClassifierTests
{
    [Theory]
    [InlineData(-2147015902)]
    [InlineData(-2147015903)]
    [InlineData(-2147015898)]
    public void Classify_ServiceProtectionCodesAsThrottling(int errorCode)
    {
        var exception = DataverseFaultClassifier.Classify(
            new WhoAmIRequest(),
            Fault(errorCode));

        Assert.Equal(ErrorCategory.Throttling, exception.Category);
    }

    [Theory]
    [InlineData(401, ErrorCategory.Authentication)]
    [InlineData(403, ErrorCategory.Authorization)]
    [InlineData(408, ErrorCategory.SourceConnectivity)]
    [InlineData(429, ErrorCategory.Throttling)]
    [InlineData(503, ErrorCategory.SourceConnectivity)]
    public void Classify_HttpStatusMetadata(int statusCode, ErrorCategory expected)
    {
        var fault = Fault(-1);
        fault.ErrorDetails["ApiExceptionHttpStatusCode"] = statusCode;

        var exception = DataverseFaultClassifier.Classify(new WhoAmIRequest(), fault);

        Assert.Equal(expected, exception.Category);
    }

    [Fact]
    public void Classify_InvalidArgumentAsExpiredOnlyForCheckpointRequest()
    {
        var fault = Fault(DataverseFaultClassifier.InvalidArgumentErrorCode);
        var checkpointRequest = new RetrieveEntityChangesRequest
        {
            EntityName = "account",
            DataVersion = "opaque-checkpoint"
        };

        var checkpointException = DataverseFaultClassifier.Classify(checkpointRequest, fault);
        var otherException = DataverseFaultClassifier.Classify(new WhoAmIRequest(), fault);

        Assert.Equal(ErrorCategory.ExpiredCheckpoint, checkpointException.Category);
        Assert.Equal(ErrorCategory.SourceConnectivity, otherException.Category);
        Assert.DoesNotContain("opaque-checkpoint", checkpointException.ToString(), StringComparison.Ordinal);
    }

    private static OrganizationServiceFault Fault(int errorCode) => new() { ErrorCode = errorCode };
}
