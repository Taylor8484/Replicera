using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.PowerPlatform.Dataverse.Client;
using Replicera.Core.Configuration;

namespace Replicera.Dataverse.Authentication;

public static class DataverseClientFactory
{
    public static DataverseService Create(SourceConfiguration source, ISecretResolver secrets)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(secrets);
        var secret = secrets.Resolve(source.Authentication.SecretEnvironmentVariable);
        var client = source.Authentication.Method switch
        {
            AuthenticationMethod.ClientSecret => new ServiceClient(
                source.Url,
                source.ClientId.ToString("D"),
                secret,
                true,
                NullLogger.Instance),
            AuthenticationMethod.Certificate => CreateCertificateClient(source, secrets, secret),
            _ => throw new ArgumentOutOfRangeException(
                nameof(source),
                source.Authentication.Method,
                "Unsupported Dataverse authentication method.")
        };
        client.MaxRetryCount = 0;
        return new DataverseService(client);
    }

    private static ServiceClient CreateCertificateClient(
        SourceConfiguration source,
        ISecretResolver secrets,
        string base64Pkcs12)
    {
        string? password = null;
        if (!string.IsNullOrWhiteSpace(source.Authentication.CertificatePasswordEnvironmentVariable))
        {
            password = secrets.Resolve(source.Authentication.CertificatePasswordEnvironmentVariable);
        }

        byte[] certificateBytes;
        try
        {
            certificateBytes = Convert.FromBase64String(base64Pkcs12);
        }
        catch (FormatException exception)
        {
            throw new CryptographicException("Certificate secret must contain a base64-encoded PKCS#12 document.", exception);
        }

        var certificate = X509CertificateLoader.LoadPkcs12(
            certificateBytes,
            password,
            X509KeyStorageFlags.EphemeralKeySet);
        return new ServiceClient(
            certificate,
            StoreName.My,
            source.ClientId.ToString("D"),
            source.Url,
            true,
            null!,
            string.Empty,
            null!,
            NullLogger.Instance);
    }
}
