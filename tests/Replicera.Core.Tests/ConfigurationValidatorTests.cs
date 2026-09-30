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

    [Theory]
    [InlineData(0)]
    [InlineData(604801)]
    public void Validate_RejectsScheduleIntervalsOutsideSupportedRange(int seconds)
    {
        var valid = ValidConfiguration();
        var configuration = valid with
        {
            Jobs =
            [
                valid.Jobs[0] with
                {
                    Schedule = new ScheduleConfiguration { Interval = TimeSpan.FromSeconds(seconds) }
                }
            ]
        };

        var issues = ConfigurationValidator.Validate(configuration);

        Assert.Contains(issues, issue => issue.Path == "jobs[0].schedule.interval");
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

    [Theory]
    [InlineData("Server=db;Password=hunter2")]
    [InlineData("REPLICERA SECRET")]
    [InlineData("1SECRET")]
    [InlineData("secret-value")]
    public void Validate_RejectsEnvironmentVariableNamesThatAreNotIdentifiersWithoutEchoingThem(string name)
    {
        var configuration = ValidConfiguration();
        configuration = configuration with
        {
            Sources =
            [
                configuration.Sources[0] with
                {
                    Authentication = configuration.Sources[0].Authentication with
                    {
                        SecretEnvironmentVariable = name,
                        CertificatePasswordEnvironmentVariable = name
                    }
                }
            ],
            Destinations = [configuration.Destinations[0] with { ConnectionStringEnvironmentVariable = name }]
        };

        var issues = ConfigurationValidator.Validate(configuration);

        Assert.Equal(
            [
                "sources[0].authentication.secretEnvironmentVariable",
                "sources[0].authentication.certificatePasswordEnvironmentVariable",
                "destinations[0].connectionStringEnvironmentVariable"
            ],
            issues.Select(issue => issue.Path));
        Assert.All(issues, issue => Assert.DoesNotContain(name, issue.Message, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("REPLICERA_SECRET")]
    [InlineData("_secret2")]
    [InlineData("a")]
    public void Validate_AcceptsPortableEnvironmentVariableNames(string name)
    {
        var configuration = ValidConfiguration();
        configuration = configuration with
        {
            Destinations = [configuration.Destinations[0] with { ConnectionStringEnvironmentVariable = name }]
        };

        Assert.Empty(ConfigurationValidator.Validate(configuration));
    }

    [Fact]
    public void Validate_RejectsBlankCertificatePasswordVariableWhenSpecified()
    {
        var configuration = ValidConfiguration();
        configuration = configuration with
        {
            Sources =
            [
                configuration.Sources[0] with
                {
                    Authentication = configuration.Sources[0].Authentication with { CertificatePasswordEnvironmentVariable = " " }
                }
            ]
        };

        var issue = Assert.Single(ConfigurationValidator.Validate(configuration));
        Assert.Equal("sources[0].authentication.certificatePasswordEnvironmentVariable", issue.Path);
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
