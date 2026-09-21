namespace Replicera.Core.Errors;

public enum ErrorCategory
{
    Configuration,
    Authentication,
    Authorization,
    SourceMetadataNotFound,
    SourceConnectivity,
    DestinationConnectivity,
    Throttling,
    UnsupportedMetadata,
    SchemaConflict,
    ExpiredCheckpoint,
    DestinationWrite,
    Synchronization,
    Unexpected
}
public sealed class RepliceraException : Exception
{
    public RepliceraException(ErrorCategory category, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        Category = category;
    }

    public ErrorCategory Category { get; }
}

public enum ExitCode
{
    Success = 0,
    InvalidInput = 2,
    AuthenticationOrAuthorization = 3,
    SourceConnectivity = 4,
    DestinationConnectivity = 5,
    SchemaOrMetadata = 6,
    SynchronizationFailure = 7,
    ResynchronizationRequired = 8,
    Unexpected = 70
}

public static class ExitCodeMapper
{
    public static ExitCode From(ErrorCategory category) => category switch
    {
        ErrorCategory.Configuration => ExitCode.InvalidInput,
        ErrorCategory.Authentication or ErrorCategory.Authorization => ExitCode.AuthenticationOrAuthorization,
        ErrorCategory.SourceConnectivity or ErrorCategory.SourceMetadataNotFound or ErrorCategory.Throttling => ExitCode.SourceConnectivity,
        ErrorCategory.DestinationConnectivity => ExitCode.DestinationConnectivity,
        ErrorCategory.UnsupportedMetadata or ErrorCategory.SchemaConflict => ExitCode.SchemaOrMetadata,
        ErrorCategory.ExpiredCheckpoint => ExitCode.ResynchronizationRequired,
        ErrorCategory.DestinationWrite or ErrorCategory.Synchronization => ExitCode.SynchronizationFailure,
        ErrorCategory.Unexpected => ExitCode.Unexpected,
        _ => ExitCode.Unexpected
    };
}
