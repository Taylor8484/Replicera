using Replicera.Core.Errors;

namespace Replicera.Core.Configuration;

public interface ISecretResolver
{
    string Resolve(string environmentVariable);
}

public sealed class EnvironmentSecretResolver : ISecretResolver
{
    public string Resolve(string environmentVariable)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(environmentVariable);
        var value = Environment.GetEnvironmentVariable(environmentVariable);
        if (string.IsNullOrEmpty(value))
        {
            throw new RepliceraException(
                ErrorCategory.Configuration,
                $"Required environment variable '{environmentVariable}' is not set.");
        }

        return value;
    }
}
