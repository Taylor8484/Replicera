namespace Replicera.Provider.Oracle;

internal static class OracleValueConverter
{
    public static byte[] ToBytes(Guid value) => value.ToByteArray(bigEndian: true);

    public static Guid ToGuid(byte[] value) => new(value, bigEndian: true);
}
