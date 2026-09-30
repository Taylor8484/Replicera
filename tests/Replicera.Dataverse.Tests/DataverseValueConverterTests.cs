using Microsoft.Xrm.Sdk;
using Replicera.Core.Errors;
using Replicera.Core.Models;
using Replicera.Dataverse.ChangeTracking;

namespace Replicera.Dataverse.Tests;

public sealed class DataverseValueConverterTests
{
    [Fact]
    public void Convert_NormalizesSdkWrapperValues()
    {
        var id = Guid.NewGuid();

        Assert.Equal(new LookupValue(id, "account"), DataverseValueConverter.Convert(new EntityReference("account", id)));
        Assert.Equal(4, DataverseValueConverter.Convert(new OptionSetValue(4)));
        Assert.Equal(12.34m, DataverseValueConverter.Convert(new Money(12.34m)));
    }

    [Fact]
    public void Convert_SerializesMultiSelectInStableOrder()
    {
        var options = new OptionSetValueCollection
        {
            new(3),
            new(1),
            new(3)
        };

        var result = Assert.IsType<ChoiceSetValue>(DataverseValueConverter.Convert(options));
        Assert.Equal([1, 3], result.Values);
    }

    [Fact]
    public void Convert_RejectsUnsupportedRuntimeType()
    {
        Assert.Throws<NotSupportedException>(() => DataverseValueConverter.Convert(new byte[] { 1, 2 }));
    }

    [Fact]
    public void ConvertAttributes_ReportsUnsupportedValueWithColumnButNotValue()
    {
        var entity = new Entity("account", Guid.NewGuid())
        {
            ["name"] = "Contoso",
            ["secretblob"] = new byte[] { 0x53, 0x45, 0x43 }
        };

        var exception = Assert.Throws<RepliceraException>(() => DataverseValueConverter.ConvertAttributes(entity));

        Assert.Equal(ErrorCategory.UnsupportedMetadata, exception.Category);
        Assert.Contains("'secretblob'", exception.Message, StringComparison.Ordinal);
        Assert.Contains("'account'", exception.Message, StringComparison.Ordinal);
        Assert.Contains("System.Byte[]", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void MapChange_ReportsUnknownChangeTypeAsUnsupportedMetadata()
    {
        var exception = Assert.Throws<RepliceraException>(() => DataverseChangeReader.MapChange(new object()));

        Assert.Equal(ErrorCategory.UnsupportedMetadata, exception.Category);
    }
}
