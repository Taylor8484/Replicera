using Microsoft.Xrm.Sdk.Metadata;
using Replicera.Core.Models;
using CoreDateTimeBehavior = Replicera.Core.Models.DateTimeBehavior;

namespace Replicera.Dataverse.Metadata;

public static class DataverseMetadataTranslator
{
    public static TableDefinition Translate(EntityMetadata entity, string destinationName)
    {
        ArgumentNullException.ThrowIfNull(entity);

        var logicalName = Require(entity.LogicalName, "Entity logical name");
        var entitySetName = Require(entity.EntitySetName, "Entity set name");
        var primaryId = Require(entity.PrimaryIdAttribute, "Primary ID attribute");
        var columns = entity.Attributes
            .Where(attribute => !string.IsNullOrWhiteSpace(attribute.LogicalName))
            .Select(attribute => TranslateColumn(attribute, primaryId))
            .ToArray();

        return new TableDefinition(logicalName, entitySetName, destinationName, columns);
    }

    public static ColumnDefinition TranslateColumn(AttributeMetadata attribute, string primaryIdAttribute)
    {
        ArgumentNullException.ThrowIfNull(attribute);
        var logicalName = Require(attribute.LogicalName, "Attribute logical name");
        var sourceType = MapType(attribute, out var unsupportedReason);
        if (!string.IsNullOrWhiteSpace(attribute.AttributeOf))
        {
            unsupportedReason = $"Dataverse child attribute '{logicalName}' is derived from '{attribute.AttributeOf}' and cannot be requested directly.";
        }
        else if (attribute.IsValidForRead == false)
        {
            unsupportedReason = $"Dataverse attribute '{logicalName}' is not valid for read operations.";
        }

        return new ColumnDefinition
        {
            LogicalName = logicalName,
            SourceType = sourceType,
            IsNullable = attribute.RequiredLevel?.Value is not AttributeRequiredLevel.ApplicationRequired
                and not AttributeRequiredLevel.SystemRequired,
            IsPrimaryKey = string.Equals(logicalName, primaryIdAttribute, StringComparison.OrdinalIgnoreCase),
            MaxLength = (attribute as StringAttributeMetadata)?.MaxLength
                ?? (attribute as MemoAttributeMetadata)?.MaxLength,
            Precision = attribute switch
            {
                DecimalAttributeMetadata => 38,
                MoneyAttributeMetadata => 19,
                _ => null
            },
            Scale = attribute switch
            {
                DecimalAttributeMetadata value => value.Precision,
                MoneyAttributeMetadata value => value.Precision,
                _ => null
            },
            DateTimeBehavior = attribute is DateTimeAttributeMetadata dateTime ? MapDateTimeBehavior(dateTime) : null,
            LookupTargets = attribute is LookupAttributeMetadata lookup ? lookup.Targets ?? [] : [],
            IsCalculated = attribute.SourceType == 1,
            IsRollup = attribute.SourceType == 2,
            UnsupportedReason = unsupportedReason
        };
    }

    private static SourceType MapType(AttributeMetadata attribute, out string? unsupportedReason)
    {
        unsupportedReason = null;
        return attribute switch
        {
            UniqueIdentifierAttributeMetadata => SourceType.Guid,
            StringAttributeMetadata => SourceType.String,
            MemoAttributeMetadata => SourceType.Text,
            BooleanAttributeMetadata => SourceType.Boolean,
            IntegerAttributeMetadata => SourceType.Int32,
            BigIntAttributeMetadata => SourceType.Int64,
            DecimalAttributeMetadata => SourceType.Decimal,
            DoubleAttributeMetadata => SourceType.Double,
            MoneyAttributeMetadata => SourceType.Money,
            DateTimeAttributeMetadata => SourceType.DateTime,
            MultiSelectPicklistAttributeMetadata => SourceType.MultiSelectChoice,
            PicklistAttributeMetadata or StateAttributeMetadata or StatusAttributeMetadata => SourceType.Choice,
            LookupAttributeMetadata => SourceType.Lookup,
            _ => MapDeclaredType(attribute, out unsupportedReason)
        };
    }

    private static SourceType MapDeclaredType(AttributeMetadata attribute, out string? unsupportedReason)
    {
        unsupportedReason = null;
        if (attribute.AttributeTypeName?.Value == "MultiSelectPicklistType")
        {
            return SourceType.MultiSelectChoice;
        }

        return attribute.AttributeType switch
        {
            AttributeTypeCode.Uniqueidentifier => SourceType.Guid,
            AttributeTypeCode.String => SourceType.String,
            AttributeTypeCode.Memo => SourceType.Text,
            AttributeTypeCode.Boolean => SourceType.Boolean,
            AttributeTypeCode.Integer => SourceType.Int32,
            AttributeTypeCode.BigInt => SourceType.Int64,
            AttributeTypeCode.Decimal => SourceType.Decimal,
            AttributeTypeCode.Double => SourceType.Double,
            AttributeTypeCode.Money => SourceType.Money,
            AttributeTypeCode.DateTime => SourceType.DateTime,
            AttributeTypeCode.Picklist or AttributeTypeCode.State or AttributeTypeCode.Status => SourceType.Choice,
            AttributeTypeCode.Lookup or AttributeTypeCode.Customer or AttributeTypeCode.Owner => SourceType.Lookup,
            _ => Unsupported(attribute, out unsupportedReason)
        };
    }

    private static SourceType Unsupported(AttributeMetadata attribute, out string reason)
    {
        reason = $"Dataverse attribute type '{attribute.AttributeTypeName?.Value ?? attribute.AttributeType?.ToString() ?? attribute.GetType().Name}' is not supported.";
        return SourceType.Text;
    }

    private static CoreDateTimeBehavior MapDateTimeBehavior(DateTimeAttributeMetadata attribute)
    {
        return attribute.DateTimeBehavior?.Value switch
        {
            "DateOnly" => CoreDateTimeBehavior.DateOnly,
            "TimeZoneIndependent" => CoreDateTimeBehavior.TimeZoneIndependent,
            _ => CoreDateTimeBehavior.UserLocal
        };
    }

    private static string Require(string? value, string description)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException($"{description} is required.");
        }

        return value;
    }
}
