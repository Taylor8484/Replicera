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
    public void From_MapsPublicExitCategory(ErrorCategory category, ExitCode expected)
    {
        Assert.Equal(expected, ExitCodeMapper.From(category));
    }
}
