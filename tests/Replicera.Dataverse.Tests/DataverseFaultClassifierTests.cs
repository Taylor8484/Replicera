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

    [Theory]
    [InlineData(unchecked((int)0x8004F899))]
    [InlineData(unchecked((int)0x80094005))]
    [InlineData(unchecked((int)0x8005E00C))]
    [InlineData(unchecked((int)0x80072493))]
    public void Classify_MissingEntityOnlyForDirectMetadataLookup(int errorCode)
    {
        var request = new RetrieveEntityRequest { LogicalName = "removed_table" };

        Assert.Equal(
            ErrorCategory.SourceMetadataNotFound,
            DataverseFaultClassifier.Classify(request, Fault(errorCode)).Category);
        Assert.Equal(
            ErrorCategory.SourceConnectivity,
            DataverseFaultClassifier.Classify(new WhoAmIRequest(), Fault(errorCode)).Category);
    }

    [Theory]
    [InlineData(unchecked((int)0x80040220))]
    [InlineData(unchecked((int)0x80042F06))]
    public void Classify_PrivilegeFailuresAsAuthorization(int errorCode)
    {
        var fault = Fault(errorCode);

        Assert.Equal(ErrorCategory.Authorization, DataverseFaultClassifier.Classify(new WhoAmIRequest(), fault).Category);
        Assert.False(DataverseFaultClassifier.IsTransient(fault));
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData(400, false)]
    [InlineData(404, false)]
    [InlineData(408, true)]
    [InlineData(429, true)]
    [InlineData(500, true)]
    [InlineData(503, true)]
    public void IsTransient_RequiresThrottlingTimeoutOrServerFailure(int? statusCode, bool expected)
    {
        var fault = Fault(-1);
        if (statusCode is not null)
        {
            fault.ErrorDetails["ApiExceptionHttpStatusCode"] = statusCode.Value;
        }

        Assert.Equal(expected, DataverseFaultClassifier.IsTransient(fault));
    }

    private static OrganizationServiceFault Fault(int errorCode) => new() { ErrorCode = errorCode };
}
