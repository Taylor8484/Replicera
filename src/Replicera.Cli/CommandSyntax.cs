using Replicera.Core.Errors;

namespace Replicera.Cli;

internal sealed record CommandSyntax(
    IReadOnlySet<string> ValueOptions,
    IReadOnlySet<string> Flags,
    IReadOnlySet<string> RepeatableOptions,
    int PositionalArguments = 0)
{
    private static readonly Dictionary<string, CommandSyntax> Commands = new(StringComparer.Ordinal)
    {
        ["init"] = Define(),
        ["source add"] = Define(
            ["--name", "--url", "--tenant-id", "--client-id", "--secret-env", "--auth", "--certificate-password-env"],
            ["--interactive"]),
        ["source list"] = Define(),
        ["source bootstrap-permissions"] = Define(["--name", "--role-name", "--tables-file"], ["--all-tables", "--without-customizer", "--json"]),
        ["destination add"] = Define(["--name", "--connection-env", "--provider"], ["--interactive"]),
        ["destination list"] = Define(),
        ["job add"] = Define(
            ["--name", "--source", "--destination", "--table", "--batch-size", "--mode"],
            ["--interactive"],
            repeatable: ["--table"]),
        ["job list"] = Define(),
        ["schedule set"] = Define(["--job", "--interval"], ["--run-on-start", "--wait-first"]),
        ["schedule show"] = Define(["--job"], ["--json"]),
        ["schedule disable"] = Define(["--job"]),
        ["schedule enable"] = Define(["--job"]),
        ["tables list"] = Define(),
        ["tables add"] = Define(["--job"], positional: 1),
        ["tables remove"] = Define(["--job"], positional: 1),
        ["inspect"] = Define(["--job"], ["--json"], positional: 1),
        ["sync"] = Define(["--job", "--table"], ["--full", "--json", "--verbose", "--log-json"]),
        ["worker"] = Define(["--job"], ["--json", "--verbose", "--log-json"]),
        ["status"] = Define(["--job"], ["--json"])
    };

    private static readonly HashSet<string> GroupedCommands = new(StringComparer.Ordinal)
    {
        "source",
        "destination",
        "job",
        "schedule",
        "tables"
    };

    public static bool IsHelpRequest(IReadOnlyList<string> arguments) =>
        arguments.Count == 0
        || arguments[0] == "help"
        || arguments.Any(argument => argument is "--help" or "-h");

    /// <summary>
    /// Validates the arguments for a known command and returns its positional arguments,
    /// or returns <see langword="null"/> when the command itself is not recognized.
    /// </summary>
    public static IReadOnlyList<string>? Validate(IReadOnlyList<string> arguments)
    {
        var commandLength = arguments.Count > 1 && GroupedCommands.Contains(arguments[0]) ? 2 : 1;
        var command = string.Join(' ', arguments.Take(commandLength));
        if (!Commands.TryGetValue(command, out var syntax))
        {
            return null;
        }

        var positional = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var index = commandLength; index < arguments.Count; index++)
        {
            var argument = arguments[index];
            if (!argument.StartsWith('-'))
            {
                positional.Add(argument);
                continue;
            }

            var takesValue = argument == "--config" || syntax.ValueOptions.Contains(argument);
            if (!takesValue && !syntax.Flags.Contains(argument))
            {
                throw new RepliceraException(
                    ErrorCategory.Configuration,
                    $"Unknown option '{argument}' for '{command}'. Run 'replicera --help' for usage.");
            }

            if (!seen.Add(argument) && !syntax.RepeatableOptions.Contains(argument))
            {
                throw new RepliceraException(
                    ErrorCategory.Configuration,
                    $"Option '{argument}' was specified more than once.");
            }

            if (takesValue)
            {
                if (index + 1 >= arguments.Count || arguments[index + 1].StartsWith("--", StringComparison.Ordinal))
                {
                    throw new RepliceraException(ErrorCategory.Configuration, $"Option '{argument}' requires a value.");
                }

                index++;
            }
        }

        if (positional.Count != syntax.PositionalArguments)
        {
            throw new RepliceraException(
                ErrorCategory.Configuration,
                syntax.PositionalArguments == 0
                    ? $"Unexpected argument '{positional[0]}' for '{command}'. Run 'replicera --help' for usage."
                    : $"'{command}' requires exactly {syntax.PositionalArguments} table logical name.");
        }

        return positional;
    }

    private static CommandSyntax Define(
        string[]? values = null,
        string[]? flags = null,
        string[]? repeatable = null,
        int positional = 0) => new(
            new HashSet<string>(values ?? [], StringComparer.Ordinal),
            new HashSet<string>(flags ?? [], StringComparer.Ordinal),
            new HashSet<string>(repeatable ?? [], StringComparer.Ordinal),
            positional);
}
