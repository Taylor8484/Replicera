using Microsoft.Xrm.Sdk;
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
}
