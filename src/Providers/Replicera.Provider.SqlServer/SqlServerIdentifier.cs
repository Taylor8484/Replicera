using System.Security.Cryptography;
using System.Text;

namespace Replicera.Provider.SqlServer;

public static class SqlServerIdentifier
{
    public const int MaximumLength = 128;

    public static string Normalize(string sourceName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceName);

        var normalized = new string(sourceName
            .Select(character => char.IsLetterOrDigit(character) || character == '_' ? character : '_')
            .ToArray());

        if (char.IsDigit(normalized[0]))
        {
            normalized = $"_{normalized}";
        }

        if (normalized.Length <= MaximumLength)
        {
            return normalized;
        }

        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(sourceName)))[..12];
        return $"{normalized[..(MaximumLength - hash.Length - 1)]}_{hash}";
    }

    public static string Quote(string identifier)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(identifier);
        return $"[{identifier.Replace("]", "]]", StringComparison.Ordinal)}]";
    }

    public static IReadOnlyDictionary<string, string> NormalizeDistinct(IEnumerable<string> sourceNames)
    {
        ArgumentNullException.ThrowIfNull(sourceNames);

        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var normalizedToSource = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var sourceName in sourceNames)
        {
            var normalized = Normalize(sourceName);
            if (normalizedToSource.TryGetValue(normalized, out var existing)
                && !string.Equals(existing, sourceName, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Names '{existing}' and '{sourceName}' both normalize to '{normalized}'.");
            }

            normalizedToSource[normalized] = sourceName;
            result[sourceName] = normalized;
        }

        return result;
    }
}
