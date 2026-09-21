namespace Replicera.Core.Models;

public enum SourceType
{
    Guid,
    String,
    Text,
    Boolean,
    Int32,
    Int64,
    Decimal,
    Double,
    Money,
    DateTime,
    Choice,
    MultiSelectChoice,
    Lookup
}
public enum DateTimeBehavior
{
    UserLocal,
    DateOnly,
    TimeZoneIndependent
}
