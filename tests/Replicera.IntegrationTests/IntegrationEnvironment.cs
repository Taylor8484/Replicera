namespace Replicera.IntegrationTests;

/// <summary>
/// Resolves test database settings. A missing setting skips the test locally, but fails it when
/// <c>REPLICERA_INTEGRATION_REQUIRED=1</c>, as the integration scripts set, so CI cannot pass
/// without actually running the tests.
/// </summary>
internal static class IntegrationEnvironment
{
    public const string RequiredVariable = "REPLICERA_INTEGRATION_REQUIRED";

    public static string RequireConnectionString(string variable)
    {
        var value = Environment.GetEnvironmentVariable(variable);
        if (!string.IsNullOrWhiteSpace(value))
        {
            return value;
        }

        if (Environment.GetEnvironmentVariable(RequiredVariable) == "1")
        {
            throw new InvalidOperationException($"{variable} must be set because {RequiredVariable} is 1.");
        }

        throw Xunit.Sdk.SkipException.ForSkip($"Set {variable} to run this test.");
    }
}
