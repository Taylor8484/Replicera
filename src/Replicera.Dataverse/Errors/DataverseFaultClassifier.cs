using System.Globalization;
using System.Net;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Replicera.Core.Errors;

namespace Replicera.Dataverse.Errors;

internal static class DataverseFaultClassifier
{
    internal const int InvalidArgumentErrorCode = unchecked((int)0x80040203);

    private static readonly HashSet<int> ServiceProtectionErrorCodes =
    [
        -2147015902,
        -2147015903,
        -2147015898
    ];

    public static RepliceraException Classify(OrganizationRequest request, OrganizationServiceFault fault)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(fault);

        if (request is RetrieveEntityChangesRequest { DataVersion: not null } &&
            fault.ErrorCode == InvalidArgumentErrorCode)
        {
            return new RepliceraException(
                ErrorCategory.ExpiredCheckpoint,
                "The Dataverse change checkpoint is expired or invalid; a full resynchronization is required.");
        }

        if (ServiceProtectionErrorCodes.Contains(fault.ErrorCode) || TryGetHttpStatus(fault) == 429)
        {
            return new RepliceraException(
                ErrorCategory.Throttling,
                "Dataverse service-protection limits remained active after the configured retries.");
        }

        return TryGetHttpStatus(fault) switch
        {
            401 => new RepliceraException(
                ErrorCategory.Authentication,
                "Dataverse rejected the configured identity or authentication token."),
            403 => new RepliceraException(
                ErrorCategory.Authorization,
                "The Dataverse identity does not have permission to perform the requested operation."),
            408 or >= 500 => new RepliceraException(
                ErrorCategory.SourceConnectivity,
                "Dataverse was temporarily unavailable after the configured retries."),
            _ => new RepliceraException(
                ErrorCategory.SourceConnectivity,
                "Dataverse rejected the requested operation.")
        };
    }

    private static int? TryGetHttpStatus(OrganizationServiceFault fault)
    {
        if (!fault.ErrorDetails.TryGetValue("ApiExceptionHttpStatusCode", out var value))
        {
            return null;
        }

        return value switch
        {
            HttpStatusCode status => (int)status,
            int status => status,
            long status when status is >= int.MinValue and <= int.MaxValue => (int)status,
            string status when int.TryParse(status, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => null
        };
    }

    internal static TimeSpan? TryGetRetryAfter(OrganizationServiceFault fault)
    {
        if (!fault.ErrorDetails.TryGetValue("Retry-After", out var value))
        {
            return null;
        }

        return value switch
        {
            TimeSpan duration when duration >= TimeSpan.Zero => duration,
            int seconds when seconds >= 0 => TimeSpan.FromSeconds(seconds),
            long seconds when seconds >= 0 => TimeSpan.FromSeconds(seconds),
            string seconds when double.TryParse(seconds, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var parsed) && parsed >= 0 => TimeSpan.FromSeconds(parsed),
            _ => null
        };
    }
}
