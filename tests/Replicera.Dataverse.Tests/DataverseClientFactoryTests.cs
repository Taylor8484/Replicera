using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Replicera.Core.Configuration;
using Replicera.Dataverse.Authentication;

namespace Replicera.Dataverse.Tests;

public sealed class DataverseClientFactoryTests
{
    private static readonly Guid ClientId = Guid.Parse("11111111-2222-3333-4444-555555555555");

    [Fact]
    public void CreateCertificateClient_PassesApplicationIdAsClientIdAndCertificateThumbprint()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=replicera-test", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var generated = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddHours(1));
        var pkcs12 = Convert.ToBase64String(generated.Export(X509ContentType.Pkcs12));
        var source = Source();

        var captured = DataverseClientFactory.CreateCertificateClient(
            source,
            new UnusedSecretResolver(),
            pkcs12,
            (certificate, thumbprint, instanceUrl, clientId) => (certificate.Thumbprint, thumbprint, instanceUrl, clientId));

        Assert.Equal(ClientId.ToString("D"), captured.clientId);
        Assert.Equal(generated.Thumbprint, captured.thumbprint);
        Assert.Equal(generated.Thumbprint, captured.Item1);
        Assert.Equal(source.Url, captured.instanceUrl);
    }

    [Fact]
    public void CreateCertificateClient_RejectsSecretThatIsNotBase64()
    {
        Assert.Throws<CryptographicException>(() => DataverseClientFactory.CreateCertificateClient(
            Source(),
            new UnusedSecretResolver(),
            "not base64!",
            (_, _, _, _) => 0));
    }

    [Fact]
    public void CreateCertificateClient_DisposesCertificateWhenClientCannotBeCreated()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=replicera-test", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var generated = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddHours(1));
        var pkcs12 = Convert.ToBase64String(generated.Export(X509ContentType.Pkcs12));
        X509Certificate2? loaded = null;

        _ = Assert.Throws<InvalidOperationException>(() => DataverseClientFactory.CreateCertificateClient<int>(
            Source(),
            new UnusedSecretResolver(),
            pkcs12,
            (certificate, _, _, _) =>
            {
                loaded = certificate;
                throw new InvalidOperationException("Client construction failed.");
            }));

        Assert.NotNull(loaded);
        Assert.Equal(IntPtr.Zero, loaded.Handle);
    }

    private static SourceConfiguration Source() => new()
    {
        Name = "test",
        Url = new Uri("https://example.crm.dynamics.com"),
        TenantId = Guid.NewGuid(),
        ClientId = ClientId,
        Authentication = new AuthenticationConfiguration
        {
            Method = AuthenticationMethod.Certificate,
            SecretEnvironmentVariable = "REPLICERA_TEST_CERTIFICATE"
        }
    };

    private sealed class UnusedSecretResolver : ISecretResolver
    {
        public string Resolve(string environmentVariable) =>
            throw new InvalidOperationException($"Unexpected secret lookup for '{environmentVariable}'.");
    }
}
