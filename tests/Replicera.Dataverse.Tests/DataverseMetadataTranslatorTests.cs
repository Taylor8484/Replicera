using Microsoft.Xrm.Sdk.Metadata;
using Replicera.Core.Models;
using Replicera.Dataverse.Metadata;

namespace Replicera.Dataverse.Tests;

public sealed class DataverseMetadataTranslatorTests
{
    [Fact]
    public void TranslateColumn_MapsSchemaWithoutProviderTypes()
    {
        var primaryKey = DataverseMetadataTranslator.TranslateColumn(
            new UniqueIdentifierAttributeMetadata { LogicalName = "accountid" },
            "accountid");
        var name = DataverseMetadataTranslator.TranslateColumn(
            new StringAttributeMetadata { LogicalName = "name", MaxLength = 100 },
            "accountid");
        var lookup = DataverseMetadataTranslator.TranslateColumn(
            new LookupAttributeMetadata { LogicalName = "ownerid", Targets = ["systemuser", "team"] },
            "accountid");

        Assert.True(primaryKey.IsPrimaryKey);
        Assert.Equal(SourceType.String, name.SourceType);
        Assert.Equal(100, name.MaxLength);
        Assert.Equal(["systemuser", "team"], lookup.LookupTargets);
    }

    [Fact]
    public void TranslateColumn_RecordsDataverseNumericRanges()
    {
        var amount = DataverseMetadataTranslator.TranslateColumn(
            new DecimalAttributeMetadata { LogicalName = "amount", Precision = 4 },
            "accountid");
        var revenue = DataverseMetadataTranslator.TranslateColumn(
            new MoneyAttributeMetadata { LogicalName = "revenue", Precision = 2 },
            "accountid");

        Assert.Equal((38, 4, 12), (amount.Precision, amount.Scale, amount.MaxIntegerDigits));
        Assert.Equal((19, 2, 15), (revenue.Precision, revenue.Scale, revenue.MaxIntegerDigits));
    }

    [Theory]
    [InlineData(0, 2, 2)]
    [InlineData(null, 3, 3)]
    [InlineData(1, 2, 4)]
    [InlineData(2, 2, 4)]
    [InlineData(0, null, 4)]
    public void TranslateColumn_UsesWidestMoneyScaleUnlessColumnPrecisionApplies(int? precisionSource, int? precision, int expectedScale)
    {
        var revenue = DataverseMetadataTranslator.TranslateColumn(
            new MoneyAttributeMetadata { LogicalName = "revenue", Precision = precision, PrecisionSource = precisionSource },
            "accountid");

        Assert.Equal(expectedScale, revenue.Scale);
    }

    [Fact]
    public void TranslateColumn_MarksUnknownTypeUnsupported()
    {
        var attribute = new ImageAttributeMetadata { LogicalName = "entityimage" };

        var column = DataverseMetadataTranslator.TranslateColumn(attribute, "accountid");

        Assert.False(column.IsSupported);
        Assert.Contains("not supported", column.UnsupportedReason, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(AttributeTypeCode.CalendarRules)]
    [InlineData(AttributeTypeCode.EntityName)]
    [InlineData(AttributeTypeCode.ManagedProperty)]
    [InlineData(AttributeTypeCode.PartyList)]
    [InlineData(AttributeTypeCode.Virtual)]
    public void TranslateColumn_MarksUnmappedDeclaredTypesUnsupported(AttributeTypeCode attributeType)
    {
        var attribute = new AttributeMetadata { LogicalName = "unsupported" };
        SetMetadataProperty(attribute, nameof(AttributeMetadata.AttributeType), attributeType);

        var column = DataverseMetadataTranslator.TranslateColumn(attribute, "accountid");

        Assert.False(column.IsSupported);
        Assert.Contains(attributeType.ToString(), column.UnsupportedReason, StringComparison.Ordinal);
    }

    [Fact]
    public void TranslateColumn_MarksDerivedChildAttributeUnsupported()
    {
        var attribute = new StringAttributeMetadata { LogicalName = "createdbyname" };
        SetMetadataProperty(attribute, nameof(AttributeMetadata.AttributeOf), "createdby");

        var column = DataverseMetadataTranslator.TranslateColumn(attribute, "accountid");

        Assert.False(column.IsSupported);
        Assert.Contains("cannot be requested directly", column.UnsupportedReason, StringComparison.Ordinal);
    }

    [Fact]
    public void TranslateColumn_MarksNonReadableAttributeUnsupported()
    {
        var attribute = new StringAttributeMetadata { LogicalName = "internalvalue" };
        SetMetadataProperty(attribute, nameof(AttributeMetadata.IsValidForRead), false);

        var column = DataverseMetadataTranslator.TranslateColumn(attribute, "accountid");

        Assert.False(column.IsSupported);
        Assert.Contains("not valid for read", column.UnsupportedReason, StringComparison.Ordinal);
    }

    [Fact]
    public void TranslateColumn_UsesDeclaredTypeForBaseMetadata()
    {
        var attribute = new AttributeMetadata { LogicalName = "accountid" };
        SetMetadataProperty(attribute, nameof(AttributeMetadata.AttributeType), AttributeTypeCode.Uniqueidentifier);

        var column = DataverseMetadataTranslator.TranslateColumn(attribute, "accountid");

        Assert.Equal(SourceType.Guid, column.SourceType);
        Assert.True(column.IsPrimaryKey);
        Assert.True(column.IsSupported);
    }

    private static void SetMetadataProperty(AttributeMetadata attribute, string propertyName, object value)
    {
        var property = typeof(AttributeMetadata).GetProperty(propertyName)
            ?? throw new InvalidOperationException($"Metadata property '{propertyName}' was not found.");
        property.SetValue(attribute, value);
    }
}
