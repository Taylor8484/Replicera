using Replicera.Core.Configuration;

namespace Replicera.Core.Tests;

public sealed class ConfigurationValidatorTests
{
    [Fact]
    public void Validate_AcceptsValidConfiguration()
    {
        var issues = ConfigurationValidator.Validate(ValidConfiguration());

        Assert.Empty(issues);
    }

    [Theory]
    [InlineData("postgresql")]
    [InlineData("postgres")]
    [InlineData("oracle")]
    public void Validate_AcceptsSupportedProviderNames(string provider)
    {
        var configuration = ValidConfiguration();
        configuration = configuration with
        {
            Destinations = [configuration.Destinations[0] with { Provider = provider }]
        };

        Assert.Empty(ConfigurationValidator.Validate(configuration));
    }

    [Fact]
    public void Validate_ReportsUnknownReferencesAndBatchSize()
    {
        var configuration = ValidConfiguration() with
        {
            Jobs =
            [
                new JobConfiguration
                {
                    Name = "nightly",
                    Source = "missing-source",
                    Destination = "missing-destination",
                    Tables = ["account"],
                    Sync = new SyncPolicy { BatchSize = 5_001 }
                }
            ]
        };

        var issues = ConfigurationValidator.Validate(configuration);

        Assert.Contains(issues, issue => issue.Path == "jobs[0].source");
        Assert.Contains(issues, issue => issue.Path == "jobs[0].destination");
        Assert.Contains(issues, issue => issue.Path == "jobs[0].sync.batchSize");
    }

    [Fact]
    public void Validate_TreatsNamesAsCaseInsensitive()
    {
        var source = ValidConfiguration().Sources[0];
        var configuration = ValidConfiguration() with
        {
            Sources = [source, source with { Name = "SOURCE" }]
        };

        var issues = ConfigurationValidator.Validate(configuration);

        Assert.Contains(issues, issue => issue.Path == "sources" && issue.Message.Contains("duplicated", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_RejectsRenameMappingsForUnconfiguredTablesAndDuplicateTargets()
    {
        var valid = ValidConfiguration();
        var configuration = valid with
        {
            Jobs =
            [
                valid.Jobs[0] with
                {
                    Schema = new SchemaPolicy
                    {
                        ColumnRenames = new Dictionary<string, IReadOnlyDictionary<string, string>>
                        {
                            ["contact"] = new Dictionary<string, string>
                            {
                                ["old_one"] = "new_name",
                                ["old_two"] = "NEW_NAME"
                            }
                        }
                    }
                }
            ]
        };

        var issues = ConfigurationValidator.Validate(configuration);

        Assert.Contains(issues, issue => issue.Message.Contains("not configured", StringComparison.Ordinal));
        Assert.Contains(issues, issue => issue.Message.Contains("Multiple columns", StringComparison.Ordinal));
    }

    private static RepliceraConfiguration ValidConfiguration() => new()
    {
        Sources =
        [
            new SourceConfiguration
            {
                Name = "source",
                Url = new Uri("https://example.crm.dynamics.com"),
                TenantId = Guid.NewGuid(),
                ClientId = Guid.NewGuid(),
                Authentication = new AuthenticationConfiguration
                {
                    Method = AuthenticationMethod.ClientSecret,
                    SecretEnvironmentVariable = "REPLICERA_CLIENT_SECRET"
                }
            }
        ],
        Destinations =
        [
            new DestinationConfiguration
            {
                Name = "sql",
                Provider = "sqlserver",
                ConnectionStringEnvironmentVariable = "REPLICERA_SQL_CONNECTION"
            }
        ],
        Jobs =
        [
            new JobConfiguration
            {
                Name = "nightly",
                Source = "source",
                Destination = "sql",
                Tables = ["account"]
            }
        ]
    };
}
