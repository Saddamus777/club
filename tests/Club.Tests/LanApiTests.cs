using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Club.Protocol;
using Club.Server;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Club.Tests;

public sealed class LanApiTests
{
    [Fact]
    public async Task HeartbeatRequiresTlsAndDeviceCredentialButNotBrowserCookieOrCsrf()
    {
        using var factory = new ApiFactory();
        var store = factory.Services.GetRequiredService<ClubStore>();
        var owner = store.CreateAccount("owner", "test-password-12345", "Owner");
        var pc = store.AddComputer(owner.Id, new("LAN PC", Guid.NewGuid().ToString())).GetProperty("id").GetString()!;
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        store.BindDevice(owner.Id, new(pc, token, Guid.NewGuid().ToString()));
        using var http = factory.CreateClient();
        var report = new DeviceReport(Guid.NewGuid().ToString(), 1, null, 0, 0, "Idle");
        Assert.Equal(HttpStatusCode.Forbidden, (await http.PostAsJsonAsync("/device/heartbeat", report)).StatusCode);
        using var tls = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });
        Assert.Equal(HttpStatusCode.Unauthorized, (await tls.PostAsJsonAsync("/device/heartbeat", report)).StatusCode);
        tls.DefaultRequestHeaders.Add("X-Club-Device", pc + ":" + token);
        var accepted = await tls.PostAsJsonAsync("/device/heartbeat", report);
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        Assert.Equal("Idle", (await accepted.Content.ReadFromJsonAsync<SessionGrant>())!.State);
        Assert.Equal(HttpStatusCode.Conflict, (await tls.PostAsJsonAsync("/device/heartbeat", report)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await tls.GetAsync("/api/state")).StatusCode);
    }
    [Fact]
    public async Task LanRejectsPlaintextStaffLoginAndStaffEndpoints()
    {
        using var factory = new ApiFactory(loopback: false);
        var store = factory.Services.GetRequiredService<ClubStore>();
        store.CreateAccount("owner", "test-password-12345", "Owner");
        using var plain = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Forbidden, (await plain.GetAsync("/api/security")).StatusCode);
        using var tls = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });
        var security = await tls.GetFromJsonAsync<JsonElement>("/api/security");
        tls.DefaultRequestHeaders.Add("X-CSRF-TOKEN", security.GetProperty("token").GetString());
        Assert.Equal(HttpStatusCode.Forbidden, (await tls.PostAsJsonAsync("/api/login", new { username = "owner", password = "test-password-12345" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await tls.PostAsJsonAsync("/api/admin/computers", new { name = "bad", operationId = Guid.NewGuid() })).StatusCode);
    }
    [Theory]
    [InlineData("http://192.168.1.1:5443")]
    [InlineData("https://user:password@example.com")]
    [InlineData("https://example.com/path")]
    [InlineData("https://example.com/?token=secret")]
    public void NativeClientRejectsUnsafeServerUrls(string url) => Assert.Throws<ArgumentException>(() => new ApiClient(url));
}
