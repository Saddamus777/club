using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Club.Server;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Club.Tests;

public sealed class ApiFactory : WebApplicationFactory<Program>
{
    public string DirectoryPath { get; } = Path.Combine(Path.GetTempPath(), "club-api-" + Guid.NewGuid().ToString("N"));
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<ClubStore>();
            var worker = services.FirstOrDefault(x => x.ServiceType == typeof(IHostedService) && x.ImplementationType == typeof(SessionWorker));
            if (worker is not null) services.Remove(worker);
            services.AddSingleton(sp => new ClubStore(DirectoryPath, TimeProvider.System, sp.GetRequiredService<IPasswordHasher<Account>>()));
            services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(DirectoryPath, "keys")));
        });
    }
    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing && Directory.Exists(DirectoryPath)) Directory.Delete(DirectoryPath, true);
    }
}

public sealed class ApiTests
{
    private const string Password = "local-test-password-789";
    private static async Task<string> Token(HttpClient client)
    {
        var response = await client.GetFromJsonAsync<JsonElement>("/api/security");
        var token = response.GetProperty("token").GetString()!;
        client.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
        client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", token);
        return token;
    }
    private static async Task Login(HttpClient client, string username)
    {
        await Token(client);
        var response = await client.PostAsJsonAsync("/api/login", new { username, password = Password });
        response.EnsureSuccessStatusCode();
        await Token(client);
    }

    [Fact]
    public async Task HttpWorkflowProtectsMoneyAndProvidesVerifiedBackup()
    {
        using var factory = new ApiFactory();
        using var admin = factory.CreateClient();
        using var visitor = factory.CreateClient();
        factory.Services.GetRequiredService<ClubStore>().CreateAccount("owner",Password,"Owner");
        await Login(admin,"owner");
        await Token(visitor);
        var signup = await visitor.PostAsJsonAsync("/api/register",new {username="visitor",password=Password,role="Owner"});
        signup.EnsureSuccessStatusCode();
        var customer = (await signup.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString();
        await Login(visitor,"visitor");
        var denied = await visitor.PostAsJsonAsync("/api/admin/topups",new TopUpRequest(customer!,1000,"Cash",Guid.NewGuid().ToString()));
        Assert.Equal(HttpStatusCode.Forbidden,denied.StatusCode);
        var pc = await admin.PostAsJsonAsync("/api/admin/computers",new ComputerRequest("ПК-01",Guid.NewGuid().ToString()));
        pc.EnsureSuccessStatusCode();
        var pcId=(await pc.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString();
        var package=await admin.PostAsJsonAsync("/api/admin/packages",new PackageRequest(null,"Час",60,10_000,true,Guid.NewGuid().ToString()));
        package.EnsureSuccessStatusCode();
        var packageId=(await package.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString();
        var topup=await admin.PostAsJsonAsync("/api/admin/topups",new TopUpRequest(customer!,20_000,"Cash",Guid.NewGuid().ToString()));
        topup.EnsureSuccessStatusCode();
        var purchase=await visitor.PostAsJsonAsync("/api/purchases",new PurchaseRequest(pcId!,packageId!,Guid.NewGuid().ToString()));
        purchase.EnsureSuccessStatusCode();
        var snapshot=await visitor.GetFromJsonAsync<JsonElement>("/api/state");
        Assert.Equal("Customer",snapshot.GetProperty("user").GetProperty("role").GetString());
        Assert.Equal(10_000,snapshot.GetProperty("user").GetProperty("balanceMinor").GetInt64());
        Assert.Equal(JsonValueKind.Null,snapshot.GetProperty("report").ValueKind);
        var backup=await admin.PostAsJsonAsync("/api/admin/backups",new {});
        backup.EnsureSuccessStatusCode();
        var backupPath=Path.Combine(factory.DirectoryPath,"download.db");
        await File.WriteAllBytesAsync(backupPath,await backup.Content.ReadAsByteArrayAsync());
        Database.Verify(backupPath);
        Assert.Contains("attachment",backup.Content.Headers.ContentDisposition!.ToString());
    }

    [Fact]
    public async Task MutationsRequireCsrfTokenAndAuthentication()
    {
        using var factory = new ApiFactory();
        using var client = factory.CreateClient();
        factory.Services.GetRequiredService<ClubStore>().CreateAccount("owner",Password,"Owner");
        Assert.Equal(HttpStatusCode.Unauthorized,(await client.GetAsync("/api/state")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,(await client.PostAsJsonAsync("/api/login",new {username="owner",password=Password})).StatusCode);
        await Login(client,"owner");
        client.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
        var blocked=await client.PostAsJsonAsync("/api/admin/computers",new ComputerRequest("Blocked",Guid.NewGuid().ToString()));
        Assert.Equal(HttpStatusCode.BadRequest,blocked.StatusCode);
        await Token(client);
        var state=await client.GetFromJsonAsync<JsonElement>("/api/state");
        Assert.Empty(state.GetProperty("computers").EnumerateArray());
    }

    [Fact]
    public async Task LoginErrorsAndRateLimitDoNotLeakAccounts()
    {
        using var factory = new ApiFactory();
        using var client = factory.CreateClient();
        factory.Services.GetRequiredService<ClubStore>().CreateAccount("owner",Password,"Owner");
        await Token(client);
        for(var i=0;i<10;i++)
            Assert.Equal(HttpStatusCode.Unauthorized,(await client.PostAsJsonAsync("/api/login",new {username="missing",password="invalid"})).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests,(await client.PostAsJsonAsync("/api/login",new {username="owner",password=Password})).StatusCode);
    }
}
