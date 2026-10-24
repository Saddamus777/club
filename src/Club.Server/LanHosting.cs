using System.Net;
using System.Security.Cryptography.X509Certificates;

namespace Club.Server;

public static class LanHosting
{
    public static void Configure(WebApplicationBuilder builder)
    {
        // Explicit listeners prevent ASPNETCORE_URLS or a copied launch profile from opening plaintext LAN.
        var enabled = builder.Configuration.GetValue<bool>("Club:Lan:Enabled");
        X509Certificate2? certificate = null;
        IPAddress? address = null;
        var port = builder.Configuration.GetValue("Club:Lan:Port", 5443);
        if (enabled)
        {
            if (!IPAddress.TryParse(builder.Configuration["Club:Lan:Address"], out address) || IPAddress.IsLoopback(address) ||
                address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any) || port is < 1024 or > 65535)
                throw new InvalidOperationException("Укажите конкретный LAN IP-адрес сервера и порт 1024–65535.");
            certificate = X509CertificateLoader.LoadPkcs12FromFile(
                builder.Configuration["Club:Lan:CertificatePath"] ?? throw new InvalidOperationException("Укажите TLS-сертификат."),
                builder.Configuration["Club:Lan:CertificatePassword"]);
            if (!certificate.HasPrivateKey || certificate.NotAfter.ToUniversalTime() <= DateTime.UtcNow)
                throw new InvalidOperationException("Требуется действующий TLS-сертификат с закрытым ключом.");
        }
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.Limits.MaxRequestBodySize = 16_384;
            options.Listen(IPAddress.Loopback, 5080);
            if (enabled) options.Listen(address!, port, endpoint => endpoint.UseHttps(certificate!));
        });
    }
}
