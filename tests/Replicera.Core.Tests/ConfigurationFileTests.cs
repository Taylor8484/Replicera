using System.Text.Json.Nodes;
using Replicera.Core.Configuration;
using Replicera.Core.Errors;

namespace Replicera.Core.Tests;

public sealed class ConfigurationFileTests : IDisposable
{
    private readonly DirectoryInfo directory = Directory.CreateTempSubdirectory("replicera-config-");

    public void Dispose() => directory.Delete(true);

    [Fact]
    public async Task LoadAsync_AcceptsValidConfiguration()
    {
        var path = await WriteAsync(ValidJson());

        var configuration = await ConfigurationFile.LoadAsync(path, CancellationToken.None);

        Assert.Equal(["account"], Assert.Single(configuration.Jobs).Tables);
    }

    [Theory]
    [InlineData("jobs")]
    [InlineData("sources")]
    [InlineData("destinations")]
    [InlineData("sources.0.authentication")]
    [InlineData("sources.0.name")]
    [InlineData("jobs.0.tables")]
    [InlineData("jobs.0.sync")]
    [InlineData("jobs.0.schema")]
    [InlineData("jobs.0.schema.columnRenames")]
    public async Task LoadAsync_ReportsExplicitNullAsConfigurationError(string property)
    {
        var json = ValidJson();
        SetNull(json, property);
        var path = await WriteAsync(json);

        var exception = await Assert.ThrowsAsync<RepliceraException>(
            () => ConfigurationFile.LoadAsync(path, CancellationToken.None));

        Assert.Equal(ErrorCategory.Configuration, exception.Category);
    }

    [Theory]
    [InlineData("[\"account\", null]", "Table names must not be blank.")]
    [InlineData("[\"account\", \" \"]", "Table names must not be blank.")]
    public async Task LoadAsync_RejectsBlankTableNames(string tables, string expected)
    {
        var json = ValidJson();
        json["jobs"]![0]!["tables"] = JsonNode.Parse(tables);
        var path = await WriteAsync(json);

        var exception = await Assert.ThrowsAsync<RepliceraException>(
            () => ConfigurationFile.LoadAsync(path, CancellationToken.None));

        Assert.Equal(ErrorCategory.Configuration, exception.Category);
        Assert.Contains(expected, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LoadAsync_RejectsNullRenameMappingForTable()
    {
        var json = ValidJson();
        json["jobs"]![0]!["schema"] = new JsonObject { ["columnRenames"] = new JsonObject { ["account"] = null } };
        var path = await WriteAsync(json);

        var exception = await Assert.ThrowsAsync<RepliceraException>(
            () => ConfigurationFile.LoadAsync(path, CancellationToken.None));

        Assert.Equal(ErrorCategory.Configuration, exception.Category);
        Assert.Contains("Column rename mappings are required.", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CreateAsync_ReportsMissingDirectoryAsConfigurationError()
    {
        var path = Path.Combine(directory.FullName, "missing", "replicera.json");

        var exception = await Assert.ThrowsAsync<RepliceraException>(
            () => ConfigurationFile.CreateAsync(path, CancellationToken.None));

        Assert.Equal(ErrorCategory.Configuration, exception.Category);
        Assert.Contains("Could not create configuration", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CreateAsync_DoesNotOverwriteExistingFile()
    {
        var path = Path.Combine(directory.FullName, "replicera.json");
        await File.WriteAllTextAsync(path, "existing");

        var exception = await Assert.ThrowsAsync<RepliceraException>(
            () => ConfigurationFile.CreateAsync(path, CancellationToken.None));

        Assert.Contains("already exists", exception.Message, StringComparison.Ordinal);
        Assert.Equal("existing", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task SaveAsync_PreservesExistingFilePermissions()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var path = await WriteAsync(ValidJson());
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        var configuration = await ConfigurationFile.LoadAsync(path, CancellationToken.None);

        await ConfigurationFile.SaveAsync(path, configuration, CancellationToken.None);

        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
        Assert.Single(directory.GetFiles());
    }

    [Fact]
    public async Task SaveAsync_UpdatesSymbolicLinkTargetAndKeepsLink()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var target = await WriteAsync(ValidJson());
        var link = Path.Combine(directory.FullName, "linked.json");
        File.CreateSymbolicLink(link, target);
        var configuration = await ConfigurationFile.LoadAsync(link, CancellationToken.None);
        var changed = configuration with
        {
            Jobs = [configuration.Jobs[0] with { Tables = ["account", "contact"] }]
        };

        await ConfigurationFile.SaveAsync(link, changed, CancellationToken.None);

        Assert.NotNull(new FileInfo(link).LinkTarget);
        Assert.Equal(["account", "contact"], (await ConfigurationFile.LoadAsync(target, CancellationToken.None)).Jobs[0].Tables);
    }

    private async Task<string> WriteAsync(JsonNode json)
    {
        var path = Path.Combine(directory.FullName, $"{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(path, json.ToJsonString());
        return path;
    }

    private static void SetNull(JsonNode json, string property)
    {
        var segments = property.Split('.');
        var parent = json;
        foreach (var segment in segments[..^1])
        {
            parent = int.TryParse(segment, out var index) ? parent[index]! : parent[segment]!;
        }

        parent[segments[^1]] = null;
    }

    private static JsonNode ValidJson() => JsonNode.Parse("""
        {
          "sources": [
            {
              "name": "source",
              "url": "https://example.crm.dynamics.com",
              "tenantId": "11111111-1111-1111-1111-111111111111",
              "clientId": "22222222-2222-2222-2222-222222222222",
              "authentication": { "method": "clientSecret", "secretEnvironmentVariable": "REPLICERA_CLIENT_SECRET" }
            }
          ],
          "destinations": [
            { "name": "sql", "provider": "sqlserver", "connectionStringEnvironmentVariable": "REPLICERA_SQL_CONNECTION" }
          ],
          "jobs": [
            {
              "name": "nightly",
              "source": "source",
              "destination": "sql",
              "tables": ["account"],
              "sync": { "mode": "complete" },
              "schema": { "columnRenames": {} }
            }
          ]
        }
        """)!;
}
