using System.Text.Json;
using Microsoft.Extensions.Logging;
using Replicera.Cli;
using Replicera.Core.Configuration;
using Replicera.Core.Errors;

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
        Assert.Contains("replicera schedule set", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("replicera worker", output.ToString(), StringComparison.Ordinal);
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

    [Theory]
    [InlineData("sync", "--help")]
    [InlineData("worker", "--job", "nightly", "-h")]
    [InlineData("init", "--help")]
    [InlineData("tables", "add", "account", "--help")]
    public async Task Help_AnywhereInArgumentsPrintsUsageWithoutRunningCommand(params string[] arguments)
    {
        var path = Path.Combine(Path.GetTempPath(), $"replicera-help-{Guid.NewGuid():N}.json");
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = await CliApplication.RunAsync([.. arguments, "--config", path], output, error, CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Contains("Usage:", output.ToString(), StringComparison.Ordinal);
        Assert.Equal(string.Empty, error.ToString());
        Assert.False(File.Exists(path));
    }

    [Theory]
    [InlineData("Unknown option '--tabel'", "sync", "--tabel", "contact", "--full")]
    [InlineData("Unknown option '--force'", "init", "--force")]
    [InlineData("Unknown option '--interval'", "worker", "--interval", "00:05:00")]
    [InlineData("'--job' was specified more than once", "sync", "--job", "a", "--job", "b")]
    [InlineData("'--full' was specified more than once", "sync", "--full", "--full")]
    [InlineData("Unexpected argument 'contact'", "sync", "contact")]
    [InlineData("requires exactly 1 table logical name", "inspect", "--json")]
    [InlineData("requires exactly 1 table logical name", "tables", "add", "account", "contact")]
    [InlineData("'--job' requires a value", "status", "--job")]
    public async Task InvalidArguments_AreRejectedBeforeCommandRuns(string expectedError, params string[] arguments)
    {
        using var error = new StringWriter();

        var exitCode = await CliApplication.RunAsync(
            [.. arguments, "--config", Path.Combine(Path.GetTempPath(), $"replicera-missing-{Guid.NewGuid():N}.json")],
            TextWriter.Null,
            error,
            CancellationToken.None);

        Assert.Equal(2, exitCode);
        Assert.Contains(expectedError, error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task BootstrapPermissions_RequiresExplicitTableScope()
    {
        var tablesFile = Path.Combine(Path.GetTempPath(), $"replicera-tables-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(tablesFile, "[\"account\"]");
        try
        {
            using var missingScopeError = new StringWriter();
            var missingScopeExit = await CliApplication.RunAsync(
                ["source", "bootstrap-permissions", "--name", "dev"],
                TextWriter.Null,
                missingScopeError,
                CancellationToken.None);
            using var bothScopesError = new StringWriter();
            var bothScopesExit = await CliApplication.RunAsync(
                ["source", "bootstrap-permissions", "--tables-file", tablesFile, "--all-tables"],
                TextWriter.Null,
                bothScopesError,
                CancellationToken.None);

            Assert.Equal(2, missingScopeExit);
            Assert.Contains("--tables-file", missingScopeError.ToString(), StringComparison.Ordinal);
            Assert.Contains("--all-tables", missingScopeError.ToString(), StringComparison.Ordinal);
            Assert.Equal(2, bothScopesExit);
            Assert.Contains("not both", bothScopesError.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(tablesFile);
        }
    }

    [Theory]
    [InlineData("[\"account\", \"contact\", \"Account\"]", null)]
    [InlineData("[]", "at least one table")]
    [InlineData("[\"account\", \"\"]", "at least one table")]
    [InlineData("{\"tables\": [\"account\"]}", "JSON array")]
    [InlineData("not json", "JSON array")]
    public async Task PermissionTablesFile_AcceptsOnlyJsonArrayOfTableNames(string content, string? expectedError)
    {
        var tablesFile = Path.Combine(Path.GetTempPath(), $"replicera-tables-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(tablesFile, content);
        try
        {
            if (expectedError is null)
            {
                var scope = await RuntimeCommands.LoadPermissionTablesAsync(tablesFile, CancellationToken.None);
                Assert.Equal(["account", "contact"], scope.Tables);
                return;
            }

            var error = await Assert.ThrowsAsync<RepliceraException>(() =>
                RuntimeCommands.LoadPermissionTablesAsync(tablesFile, CancellationToken.None));
            Assert.Equal(ErrorCategory.Configuration, error.Category);
            Assert.Contains(expectedError, error.Message, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(tablesFile);
        }
    }

    [Theory]
    [InlineData("00:05:00", 0, 0, 5, 0)]
    [InlineData("00:00:30", 0, 0, 0, 30)]
    [InlineData("12:00:00", 0, 12, 0, 0)]
    [InlineData("1.00:00:00", 1, 0, 0, 0)]
    public void TryParseInterval_AcceptsExplicitDurations(string value, int days, int hours, int minutes, int seconds)
    {
        Assert.True(CliApplication.TryParseInterval(value, out var interval));
        Assert.Equal(new TimeSpan(days, hours, minutes, seconds), interval);
    }

    [Theory]
    [InlineData("5")]
    [InlineData("5m")]
    [InlineData("00:05")]
    [InlineData("-00:05:00")]
    [InlineData("24:00:00")]
    public void TryParseInterval_RejectsAmbiguousOrInvalidDurations(string value)
    {
        Assert.False(CliApplication.TryParseInterval(value, out _));
    }

    [Fact]
    public async Task UnknownCommand_ReturnsInvalidInput()
    {
        using var error = new StringWriter();

        var exitCode = await CliApplication.RunAsync(["synchronize"], TextWriter.Null, error, CancellationToken.None);

        Assert.Equal(2, exitCode);
        Assert.Contains("unknown command", error.ToString(), StringComparison.Ordinal);
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
                    "--mode", "no-data-loss",
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
                ["tables", "remove", "--job", "development", "contact", "--config", path],
                TextWriter.Null,
                TextWriter.Null,
                CancellationToken.None);

            Assert.Equal(0, jobExit);
            Assert.Equal(0, addExit);
            Assert.Equal(0, removeExit);
            var complete = await ConfigurationFile.LoadAsync(path, CancellationToken.None);
            Assert.Equal(["account", "lead"], Assert.Single(complete.Jobs).Tables);
            Assert.Equal(SynchronizationMode.NoDataLoss, complete.Jobs[0].Sync.Mode);
        }
        finally
        {
            directory.Delete(true);
        }
    }

    [Fact]
    public async Task ScheduleCommands_SetShowDisableAndEnableJobSchedule()
    {
        var directory = Directory.CreateTempSubdirectory("replicera-test-");
        var path = Path.Combine(directory.FullName, "replicera.json");
        try
        {
            await ConfigurationFile.SaveAsync(path, ConfigurationWithOneJob(), CancellationToken.None);
            using var setOutput = new StringWriter();

            var setExit = await CliApplication.RunAsync(
                ["schedule", "set", "--job", "job", "--interval", "00:00:30", "--wait-first", "--config", path],
                setOutput,
                TextWriter.Null,
                CancellationToken.None);
            using var showOutput = new StringWriter();
            var showExit = await CliApplication.RunAsync(
                ["schedule", "show", "--job", "job", "--json", "--config", path],
                showOutput,
                TextWriter.Null,
                CancellationToken.None);
            var disableExit = await CliApplication.RunAsync(
                ["schedule", "disable", "--job", "job", "--config", path],
                TextWriter.Null,
                TextWriter.Null,
                CancellationToken.None);

            Assert.Equal(0, setExit);
            Assert.Equal(0, showExit);
            Assert.Equal(0, disableExit);
            using var shown = JsonDocument.Parse(showOutput.ToString());
            Assert.Equal("job", shown.RootElement.GetProperty("job").GetString());
            Assert.Equal("00:00:30", shown.RootElement.GetProperty("interval").GetString());
            Assert.False(shown.RootElement.GetProperty("runOnStart").GetBoolean());
            var configuration = await ConfigurationFile.LoadAsync(path, CancellationToken.None);
            var schedule = Assert.Single(configuration.Jobs).Schedule;
            Assert.NotNull(schedule);
            Assert.False(schedule.Enabled);
            Assert.Equal(TimeSpan.FromSeconds(30), schedule.Interval);

            Assert.Equal(0, await CliApplication.RunAsync(
                ["schedule", "enable", "--job", "job", "--config", path],
                TextWriter.Null,
                TextWriter.Null,
                CancellationToken.None));
            var enabled = Assert.Single((await ConfigurationFile.LoadAsync(path, CancellationToken.None)).Jobs).Schedule;
            Assert.NotNull(enabled);
            Assert.True(enabled.Enabled);
            Assert.False(enabled.RunOnStart);
            Assert.Equal(TimeSpan.FromSeconds(30), enabled.Interval);
        }
        finally
        {
            directory.Delete(true);
        }
    }

    [Fact]
    public async Task ScheduledWorker_WaitsBeforeFirstRunWhenConfiguredAndStopsCleanly()
    {
        using var cancellation = new CancellationTokenSource();
        using var output = new StringWriter();
        var events = new List<string>();

        var exitCode = await ScheduledWorker.RunAsync(
            "job",
            new ScheduleConfiguration
            {
                Interval = TimeSpan.FromMinutes(5),
                RunOnStart = false
            },
            _ =>
            {
                events.Add("sync");
                cancellation.Cancel();
                return Task.FromResult(new WorkerSyncResult(0));
            },
            output,
            false,
            cancellation.Token,
            (_, _) =>
            {
                events.Add("delay");
                return Task.CompletedTask;
            });

        Assert.Equal(0, exitCode);
        Assert.Equal(["delay", "sync", "delay"], events);
        Assert.Contains("Worker stopped", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ScheduledWorker_ContinuesAfterFailedCycle()
    {
        using var cancellation = new CancellationTokenSource();
        using var output = new StringWriter();
        var attempts = 0;

        var exitCode = await ScheduledWorker.RunAsync(
            "job",
            new ScheduleConfiguration { Interval = TimeSpan.FromMinutes(5) },
            _ =>
            {
                attempts++;
                if (attempts == 2)
                {
                    cancellation.Cancel();
                }

                return Task.FromResult(new WorkerSyncResult(attempts == 1 ? 7 : 0));
            },
            output,
            false,
            cancellation.Token,
            (_, _) => Task.CompletedTask);

        Assert.Equal(0, exitCode);
        Assert.Equal(2, attempts);
        Assert.Contains("failed with exit code 7", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("succeeded", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task DestinationAdd_ValueInsteadOfVariableNameIsRejectedWithoutSavingOrEchoingIt()
    {
        var directory = Directory.CreateTempSubdirectory("replicera-test-");
        var path = Path.Combine(directory.FullName, "replicera.json");
        const string secret = "Server=db;Password=hunter2";
        try
        {
            _ = await CliApplication.RunAsync(["init", "--config", path], TextWriter.Null, TextWriter.Null, CancellationToken.None);
            var before = await File.ReadAllTextAsync(path);
            using var error = new StringWriter();

            var exitCode = await CliApplication.RunAsync(
                ["destination", "add", "--name", "sql", "--connection-env", secret, "--config", path],
                TextWriter.Null,
                error,
                CancellationToken.None);

            Assert.Equal(2, exitCode);
            Assert.Contains("connectionStringEnvironmentVariable", error.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain("hunter2", error.ToString(), StringComparison.Ordinal);
            Assert.Equal(before, await File.ReadAllTextAsync(path));
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

    [Theory]
    [InlineData("sqlserver", "Server=127.0.0.1,1;User ID=test;Password={0};Encrypt=False;Connect Timeout=1")]
    [InlineData("postgresql", "Host=127.0.0.1;Port=1;Username=test;Password={0};Timeout=1")]
    [InlineData("oracle", "User Id=test;Password={0};Data Source=127.0.0.1:1/FREEPDB1;Connection Timeout=2")]
    public async Task Status_DestinationFailureReturnsDestinationExitCodeWithoutSecret(string provider, string connectionFormat)
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
                            Provider = provider,
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
                string.Format(System.Globalization.CultureInfo.InvariantCulture, connectionFormat, secret));
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
    public async Task Cancellation_ReturnsSynchronizationFailureAndReportsCancellation()
    {
        var directory = Directory.CreateTempSubdirectory("replicera-test-");
        var path = Path.Combine(directory.FullName, "replicera.json");
        try
        {
            Assert.Equal(0, await CliApplication.RunAsync(["init", "--config", path], TextWriter.Null, TextWriter.Null, CancellationToken.None));
            using var cancellation = new CancellationTokenSource();
            await cancellation.CancelAsync();
            using var error = new StringWriter();

            var exitCode = await CliApplication.RunAsync(["status", "--config", path], TextWriter.Null, error, cancellation.Token);

            Assert.Equal(7, exitCode);
            Assert.Contains("operation cancelled", error.ToString(), StringComparison.Ordinal);
        }
        finally
        {
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

    private static RepliceraConfiguration ConfigurationWithOneJob() => new()
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
                    SecretEnvironmentVariable = "REPLICERA_TEST_SOURCE_SECRET"
                }
            }
        ],
        Destinations =
        [
            new DestinationConfiguration
            {
                Name = "destination",
                Provider = "sqlserver",
                ConnectionStringEnvironmentVariable = "REPLICERA_TEST_DESTINATION_CONNECTION"
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
    };
}
