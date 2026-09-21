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
