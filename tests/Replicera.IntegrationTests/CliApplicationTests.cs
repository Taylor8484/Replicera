using System.Text.Json;
using Microsoft.Extensions.Logging;
using Replicera.Cli;
using Replicera.Core.Configuration;

namespace Replicera.IntegrationTests;

public sealed class CliApplicationTests
{
    [Fact]
    public void ReplicationConsoleLogger_WritesStructuredPropertiesAsJsonLine()
    {
        using var output = new StringWriter();
        var logger = new ReplicationConsoleLogger(output, structured: true);
        var state = new Dictionary<string, object?>
        {
            ["Job"] = "nightly",
            ["Table"] = "account",
            ["RecordsReceived"] = 12,
            ["{OriginalFormat}"] = "not emitted"
        };

        logger.Log(
            LogLevel.Information,
            new EventId(1001, "PageApplied"),
            state,
            null,
            static (_, _) => "unused");

        using var document = JsonDocument.Parse(output.ToString());
        var root = document.RootElement;
        Assert.Equal("Information", root.GetProperty("level").GetString());
        Assert.Equal(1001, root.GetProperty("eventId").GetInt32());
        Assert.Equal("PageApplied", root.GetProperty("eventName").GetString());
        Assert.Equal("nightly", root.GetProperty("properties").GetProperty("Job").GetString());
        Assert.Equal(12, root.GetProperty("properties").GetProperty("RecordsReceived").GetInt32());
        Assert.False(root.GetProperty("properties").TryGetProperty("{OriginalFormat}", out _));
    }

    [Fact]
    public async Task Help_ReturnsSuccessAndDocumentsCoreCommands()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = await CliApplication.RunAsync(
            ["--help"],
            output,
            error,
            CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Contains("replicera inspect", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("--json", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("--verbose", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("--log-json", output.ToString(), StringComparison.Ordinal);
        Assert.Equal(string.Empty, error.ToString());
    }

    [Fact]
    public async Task Version_ReturnsReleaseVersion()
    {
        using var output = new StringWriter();

        var exitCode = await CliApplication.RunAsync(
            ["--version"],
            output,
            TextWriter.Null,
            CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Equal("0.1.0", output.ToString().Trim());
    }

    [Fact]
    public async Task Init_CreatesConfigurationWithoutOverwriting()
    {
        var directory = Directory.CreateTempSubdirectory("replicera-test-");
        var path = Path.Combine(directory.FullName, "replicera.json");
        try
        {
            using var firstOutput = new StringWriter();
            var firstExit = await CliApplication.RunAsync(
                ["init", "--config", path],
                firstOutput,
                TextWriter.Null,
                CancellationToken.None);
            using var error = new StringWriter();
            var secondExit = await CliApplication.RunAsync(
                ["init", "--config", path],
                TextWriter.Null,
                error,
                CancellationToken.None);

            Assert.Equal(0, firstExit);
            Assert.Equal(2, secondExit);
            Assert.Contains("already exists", error.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            directory.Delete(true);
        }
    }

    [Fact]
    public async Task ConfigurationCommands_AddSourcesDestinationsAndChangeTables()
    {
        var directory = Directory.CreateTempSubdirectory("replicera-test-");
        var path = Path.Combine(directory.FullName, "replicera.json");
        try
        {
            _ = await CliApplication.RunAsync(["init", "--config", path], TextWriter.Null, TextWriter.Null, CancellationToken.None);
            var sourceExit = await CliApplication.RunAsync(
                [
                    "source", "add",
                    "--name", "development-source",
                    "--url", "https://example.crm.dynamics.com",
                    "--tenant-id", "00000000-0000-0000-0000-000000000001",
                    "--client-id", "00000000-0000-0000-0000-000000000002",
                    "--secret-env", "REPLICERA_DATAVERSE_SECRET",
                    "--config", path
                ],
                TextWriter.Null,
                TextWriter.Null,
                CancellationToken.None);
            var destinationExit = await CliApplication.RunAsync(
                [
                    "destination", "add",
                    "--name", "development-sql",
                    "--connection-env", "REPLICERA_SQL_CONNECTION",
                    "--config", path
                ],
                TextWriter.Null,
                TextWriter.Null,
                CancellationToken.None);

            Assert.Equal(0, sourceExit);
            Assert.Equal(0, destinationExit);
            var partial = await ConfigurationFile.LoadAsync(path, CancellationToken.None);
            Assert.Equal("development-source", Assert.Single(partial.Sources).Name);
            Assert.Equal("sqlserver", Assert.Single(partial.Destinations).Provider);
            var jobExit = await CliApplication.RunAsync(
                [
                    "job", "add",
                    "--name", "development",
                    "--source", "development-source",
                    "--destination", "development-sql",
                    "--table", "account",
                    "--table", "contact",
                    "--config", path
                ],
                TextWriter.Null,
                TextWriter.Null,
                CancellationToken.None);
            var addExit = await CliApplication.RunAsync(
                ["tables", "add", "lead", "--job", "development", "--config", path],
                TextWriter.Null,
                TextWriter.Null,
                CancellationToken.None);
            var removeExit = await CliApplication.RunAsync(
                ["tables", "remove", "contact", "--job", "development", "--config", path],
                TextWriter.Null,
                TextWriter.Null,
                CancellationToken.None);

            Assert.Equal(0, jobExit);
            Assert.Equal(0, addExit);
            Assert.Equal(0, removeExit);
            var complete = await ConfigurationFile.LoadAsync(path, CancellationToken.None);
            Assert.Equal(["account", "lead"], Assert.Single(complete.Jobs).Tables);
        }
        finally
        {
            directory.Delete(true);
        }
    }

    [Fact]
    public async Task SourceAdd_DuplicateNameFailsWithoutChangingConfiguration()
    {
        var directory = Directory.CreateTempSubdirectory("replicera-test-");
        var path = Path.Combine(directory.FullName, "replicera.json");
        var arguments = new[]
        {
            "source", "add",
            "--name", "source",
            "--url", "https://example.crm.dynamics.com",
            "--tenant-id", "00000000-0000-0000-0000-000000000001",
            "--client-id", "00000000-0000-0000-0000-000000000002",
            "--secret-env", "REPLICERA_DATAVERSE_SECRET",
            "--config", path
        };
        try
        {
            _ = await CliApplication.RunAsync(["init", "--config", path], TextWriter.Null, TextWriter.Null, CancellationToken.None);
            Assert.Equal(0, await CliApplication.RunAsync(arguments, TextWriter.Null, TextWriter.Null, CancellationToken.None));
            var before = await File.ReadAllTextAsync(path);
            using var error = new StringWriter();

            var duplicateExit = await CliApplication.RunAsync(arguments, TextWriter.Null, error, CancellationToken.None);

            Assert.Equal(2, duplicateExit);
            Assert.Contains("duplicated", error.ToString(), StringComparison.Ordinal);
            Assert.Equal(before, await File.ReadAllTextAsync(path));
        }
        finally
        {
            directory.Delete(true);
        }
    }

    [Fact]
    public async Task Status_DestinationFailureDoesNotExposeConnectionStringSecret()
    {
        var directory = Directory.CreateTempSubdirectory("replicera-test-");
        var path = Path.Combine(directory.FullName, "replicera.json");
        var variable = $"REPLICERA_TEST_SQL_{Guid.NewGuid():N}";
        const string secret = "super-sensitive-password";
        try
        {
            await ConfigurationFile.SaveAsync(
                path,
                new RepliceraConfiguration
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
                                SecretEnvironmentVariable = "UNUSED_SOURCE_SECRET"
                            }
                        }
                    ],
                    Destinations =
                    [
                        new DestinationConfiguration
                        {
                            Name = "destination",
                            Provider = "sqlserver",
                            ConnectionStringEnvironmentVariable = variable
                        }
                    ],
                    Jobs =
                    [
                        new JobConfiguration
                        {
                            Name = "job",
                            Source = "source",
                            Destination = "destination",
                            Tables = ["account"]
                        }
                    ]
                },
                CancellationToken.None);
            Environment.SetEnvironmentVariable(
                variable,
                $"Server=127.0.0.1,1;User ID=test;Password={secret};Encrypt=False;Connect Timeout=1");
            using var error = new StringWriter();

            var exitCode = await CliApplication.RunAsync(
                ["status", "--config", path],
                TextWriter.Null,
                error,
                CancellationToken.None);

            Assert.Equal(5, exitCode);
            Assert.DoesNotContain(secret, error.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain("Password", error.ToString(), StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, null);
            directory.Delete(true);
        }
    }

    [Fact]
    public async Task InteractiveConfiguration_PromptsForMissingSourceAndDestinationValues()
    {
        var directory = Directory.CreateTempSubdirectory("replicera-test-");
        var path = Path.Combine(directory.FullName, "replicera.json");
        try
        {
            _ = await CliApplication.RunAsync(["init", "--config", path], TextWriter.Null, TextWriter.Null, CancellationToken.None);
            using var sourceInput = new StringReader("""
                interactive-source
                https://example.crm.dynamics.com

                00000000-0000-0000-0000-000000000001
                00000000-0000-0000-0000-000000000002
                REPLICERA_INTERACTIVE_SECRET
                """);
            using var destinationInput = new StringReader("""
                interactive-sql

                REPLICERA_INTERACTIVE_SQL
                """);
            using var jobInput = new StringReader("""
                interactive-job
                interactive-source
                interactive-sql
                account

                """);

            var sourceExit = await CliApplication.RunAsync(
                ["source", "add", "--interactive", "--config", path],
                sourceInput,
                TextWriter.Null,
                TextWriter.Null,
                CancellationToken.None);
            var destinationExit = await CliApplication.RunAsync(
                ["destination", "add", "--interactive", "--config", path],
                destinationInput,
                TextWriter.Null,
                TextWriter.Null,
                CancellationToken.None);
            var jobExit = await CliApplication.RunAsync(
                ["job", "add", "--interactive", "--config", path],
                jobInput,
                TextWriter.Null,
                TextWriter.Null,
                CancellationToken.None);

            Assert.Equal(0, sourceExit);
            Assert.Equal(0, destinationExit);
            Assert.Equal(0, jobExit);
            var configuration = await ConfigurationFile.LoadAsync(path, CancellationToken.None);
            Assert.Equal("interactive-source", Assert.Single(configuration.Sources).Name);
            Assert.Equal(AuthenticationMethod.ClientSecret, configuration.Sources[0].Authentication.Method);
            Assert.Equal("interactive-sql", Assert.Single(configuration.Destinations).Name);
            Assert.Equal("sqlserver", configuration.Destinations[0].Provider);
            Assert.Equal("interactive-job", Assert.Single(configuration.Jobs).Name);
            Assert.Equal(["account"], configuration.Jobs[0].Tables);
        }
        finally
        {
            directory.Delete(true);
        }
    }
}
