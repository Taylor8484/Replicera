using System.Collections;
using Microsoft.Xrm.Sdk;
using Replicera.Core.Errors;
using Replicera.Core.Models;

namespace Replicera.Dataverse.ChangeTracking;

public static class DataverseValueConverter
{
    public static object? Convert(object? value) => value switch
    {
        null => null,
        AliasedValue aliased => Convert(aliased.Value),
        EntityReference reference => new LookupValue(reference.Id, reference.LogicalName),
        OptionSetValue option => option.Value,
        OptionSetValueCollection options => ConvertChoices(options),
        Money money => money.Value,
        bool or int or long or decimal or double or string or Guid or DateTime => value,
        _ => throw new NotSupportedException($"Dataverse value type '{value.GetType().FullName}' is not supported.")
    };

    public static IReadOnlyDictionary<string, object?> ConvertAttributes(Entity entity)
    {
        ArgumentNullException.ThrowIfNull(entity);
        return entity.Attributes.ToDictionary(
            pair => pair.Key,
            pair => ConvertColumn(entity.LogicalName, pair.Key, pair.Value),
            StringComparer.OrdinalIgnoreCase);
    }

    private static object? ConvertColumn(string table, string column, object? value)
    {
        try
        {
            return Convert(value);
        }
        catch (NotSupportedException exception)
        {
            throw new RepliceraException(
                ErrorCategory.UnsupportedMetadata,
                $"Column '{column}' in table '{table}' returned unsupported Dataverse value type '{value!.GetType().FullName}'.",
                exception);
        }
    }

    private static ChoiceSetValue ConvertChoices(IEnumerable options)
    {
        var values = options
            .Cast<OptionSetValue>()
            .Select(option => option.Value)
            .Distinct()
            .Order()
            .ToArray();
        return new ChoiceSetValue(values);
    }
}
