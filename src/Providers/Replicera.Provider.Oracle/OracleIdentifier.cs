using System.Security.Cryptography;
using System.Text;

namespace Replicera.Provider.Oracle;

public static class OracleIdentifier
{
    public const int MaximumByteLength = 128;

    public static string Normalize(string sourceName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceName);
        var normalized = new string(sourceName
            .Select(character => char.IsLetterOrDigit(character) || character == '_' ? char.ToUpperInvariant(character) : '_')
            .ToArray());
        if (char.IsDigit(normalized[0]))
        {
            normalized = $"_{normalized}";
        }

        if (Encoding.UTF8.GetByteCount(normalized) <= MaximumByteLength)
        {
            return normalized;
        }

        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sourceName)))[..12];
        var prefix = normalized;
        while (Encoding.UTF8.GetByteCount(prefix) > MaximumByteLength - hash.Length - 1)
        {
            prefix = prefix[..^1];
        }

        return $"{prefix}_{hash}";
    }

    public static string Quote(string identifier)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(identifier);
        return $"\"{identifier.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
    }

    public static IReadOnlyDictionary<string, string> NormalizeDistinct(IEnumerable<string> sourceNames)
    {
        ArgumentNullException.ThrowIfNull(sourceNames);
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var normalizedToSource = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var sourceName in sourceNames)
        {
            var normalized = Normalize(sourceName);
            if (normalizedToSource.TryGetValue(normalized, out var existing)
                && !string.Equals(existing, sourceName, StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"Names '{existing}' and '{sourceName}' both normalize to '{normalized}'.");
            }

            normalizedToSource[normalized] = sourceName;
            result[sourceName] = normalized;
        }

        return result;
    }
}
