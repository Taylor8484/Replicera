using Replicera.Core.Errors;

namespace Replicera.Core.Tests;

public sealed class ExitCodeMapperTests
{
    [Theory]
    [InlineData(ErrorCategory.Configuration, ExitCode.InvalidInput)]
    [InlineData(ErrorCategory.Authentication, ExitCode.AuthenticationOrAuthorization)]
    [InlineData(ErrorCategory.ExpiredCheckpoint, ExitCode.ResynchronizationRequired)]
    [InlineData(ErrorCategory.SchemaConflict, ExitCode.SchemaOrMetadata)]
    [InlineData(ErrorCategory.Unexpected, ExitCode.Unexpected)]
    [InlineData(ErrorCategory.Authorization, ExitCode.AuthenticationOrAuthorization)]
    [InlineData(ErrorCategory.SourceMetadataNotFound, ExitCode.SourceConnectivity)]
    [InlineData(ErrorCategory.SourceConnectivity, ExitCode.SourceConnectivity)]
    [InlineData(ErrorCategory.Throttling, ExitCode.SourceConnectivity)]
    [InlineData(ErrorCategory.DestinationConnectivity, ExitCode.DestinationConnectivity)]
    [InlineData(ErrorCategory.UnsupportedMetadata, ExitCode.SchemaOrMetadata)]
    [InlineData(ErrorCategory.DestinationWrite, ExitCode.SynchronizationFailure)]
    [InlineData(ErrorCategory.Synchronization, ExitCode.SynchronizationFailure)]
    public void From_MapsPublicExitCategory(ErrorCategory category, ExitCode expected)
    {
        Assert.Equal(expected, ExitCodeMapper.From(category));
    }

    [Fact]
    public void From_MapsEveryCategoryOtherThanUnexpectedToASpecificExitCode()
    {
        foreach (var category in Enum.GetValues<ErrorCategory>().Where(category => category != ErrorCategory.Unexpected))
        {
            Assert.NotEqual(ExitCode.Unexpected, ExitCodeMapper.From(category));
        }
    }
}
