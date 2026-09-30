using System.Security.Cryptography;
using System.Text;

namespace Replicera.Provider.SqlServer;

internal static class SqlServerLockResource
{
    // sp_getapplock silently truncates resource names to 255 characters, so longer job and table
    // names could share a lock. Names within the limit are unchanged, which keeps locks compatible
    // with earlier versions during an upgrade.
    private const int MaximumLength = 255;

    public static string For(string jobName, string logicalName)
    {
        var resource = $"replicera:{jobName}:{logicalName}";
        return resource.Length <= MaximumLength
            ? resource
            : $"replicera:sha256:{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(resource)))}";
    }
}
