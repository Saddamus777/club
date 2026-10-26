using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Club.Server;
using Club.Protocol;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Club.Tests;

public sealed class ApiFactory(bool loopback = true) : WebApplicationFactory<Program>
{
    private ClubStore? testStore;
    public string DirectoryPath { get; } = Path.Combine(Path.GetTempPath(), "club-api-" + Guid.NewGuid().ToString("N"));
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureServices(services =>
        {
            services.AddSingleton<IStartupFilter>(new RemoteAddressFilter(loopback ? IPAddress.Loopback : IPAddress.Parse("192.0.2.10")));
            services.RemoveAll<ClubStore>();
            var worker = services.FirstOrDefault(x => x.ServiceType == typeof(IHostedService) && x.ImplementationType == typeof(SessionWorker));
            if (worker is not null) services.Remove(worker);
            services.AddSingleton(sp => testStore = new ClubStore(DirectoryPath, TimeProvider.System, sp.GetRequiredService<IPasswordHasher<Account>>()));
            services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(DirectoryPath, "keys")));
        });
    }
    public override async ValueTask DisposeAsync()
    {
        // Base synchronous disposal calls DisposeAsync and re-enters Dispose(bool).
        // Cleanup belongs after the entire asynchronous host shutdown, not in that re-entry.
        await base.DisposeAsync().ConfigureAwait(false);
        testStore?.Dispose();
        if (Directory.Exists(DirectoryPath)) Directory.Delete(DirectoryPath, true);
    }
}

public sealed class RemoteAddressFilter(IPAddress address) : IStartupFilter
{
    public Action<Microsoft.AspNetCore.Builder.IApplicationBuilder> Configure(Action<Microsoft.AspNetCore.Builder.IApplicationBuilder> next) => app =>
    {
        app.Use((context, continuation) =>
        {
            context.Connection.RemoteIpAddress = address;
            return continuation(context);
        });
        next(app);
    };
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
    public async Task FirstRunOwnerCanBeCreatedLocallyAndCannotBeOverwritten()
    {
        await using var factory = new ApiFactory();
        using var client = factory.CreateClient();
        await Token(client);
        var request = new OwnerSetupRequest("owner", Password, Password);
        var created = await client.PostAsJsonAsync("/api/setup/owner", request);
        created.EnsureSuccessStatusCode();
        Assert.Equal("Owner", (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("role").GetString());
        var again = await client.PostAsJsonAsync("/api/setup/owner", request with { Username = "another" });
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Null(factory.Services.GetRequiredService<ClubStore>().FindAccount("another", true));
        await Login(client, "owner");
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/state")).StatusCode);
    }

    [Fact]
    public async Task OwnerSetupRequiresCsrfAndMatchingPasswords()
    {
        await using var factory = new ApiFactory();
        using var client = factory.CreateClient();
        var request = new OwnerSetupRequest("owner", Password, Password);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/setup/owner", request)).StatusCode);
        await Token(client);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/setup/owner", request with { ConfirmPassword = "different" })).StatusCode);
        Assert.False(factory.Services.GetRequiredService<ClubStore>().HasOwner());
    }

    [Fact]
    public async Task OwnerSetupRejectsNonLoopbackRequests()
    {
        await using var factory = new ApiFactory(loopback: false);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });
        await Token(client);
        var response = await client.PostAsJsonAsync("/api/setup/owner", new OwnerSetupRequest("owner", Password, Password));
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.False(factory.Services.GetRequiredService<ClubStore>().HasOwner());
    }

    [Fact]
    public async Task ConcurrentFirstRunRequestsCreateOnlyOneOwner()
    {
        await using var factory = new ApiFactory();
        using var first = factory.CreateClient();
        using var second = factory.CreateClient();
        await Token(first); await Token(second);
        var responses = await Task.WhenAll(
            first.PostAsJsonAsync("/api/setup/owner",new OwnerSetupRequest("owner1",Password,Password)),
            second.PostAsJsonAsync("/api/setup/owner",new OwnerSetupRequest("owner2",Password,Password)));
        Assert.Single(responses, x => x.StatusCode == HttpStatusCode.OK);
        Assert.Single(responses, x => x.StatusCode == HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task HttpWorkflowProtectsMoneyAndProvidesVerifiedBackup()
    {
        await using var factory = new ApiFactory();
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
        await BackupDownload.Save(backup, backupPath, (file, _) => { Database.Verify(file); return Task.CompletedTask; });
        Database.Verify(backupPath);
        Assert.Contains("attachment",backup.Content.Headers.ContentDisposition!.ToString());
        var restoredDirectory = Path.Combine(factory.DirectoryPath, "restored");
        Database.Restore(restoredDirectory, backupPath);
        using var restored = new ClubStore(restoredDirectory, TimeProvider.System, factory.Services.GetRequiredService<IPasswordHasher<Account>>());
        Assert.Equal(10_000, restored.FindAccount(customer!)!.BalanceMinor);
        Assert.Equal("Paused", JsonSerializer.SerializeToElement(restored.Snapshot(restored.FindAccount("owner", true)!.Id), ApiClient.Json).GetProperty("sessions")[0].GetProperty("status").GetString());
    }

    [Fact]
    public async Task OnlyOwnerCanCreateOrListStaffAndPasswordsAreNotExposed()
    {
        await using var factory = new ApiFactory();
        var store = factory.Services.GetRequiredService<ClubStore>();
        store.CreateAccount("owner", Password, "Owner");
        store.CreateAccount("visitor", Password);
        using var owner = factory.CreateClient(); using var admin = factory.CreateClient(); using var visitor = factory.CreateClient();
        await Login(owner, "owner");
        var created = await owner.PostAsJsonAsync("/api/admin/staff", new { username = "employee", password = " employee-password-123 ", role = "Owner" });
        created.EnsureSuccessStatusCode();
        Assert.Equal("Admin", (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("role").GetString());
        await Token(admin);
        (await admin.PostAsJsonAsync("/api/login", new { username = "employee", password = " employee-password-123 " })).EnsureSuccessStatusCode();
        await Token(admin); await Login(visitor, "visitor");
        foreach (var client in new[] { admin, visitor })
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/admin/staff", new { username = "forbidden", password = Password })).StatusCode);
            Assert.Empty((await client.GetFromJsonAsync<JsonElement>("/api/state")).GetProperty("employees").EnumerateArray());
        }
        var state = await owner.GetFromJsonAsync<JsonElement>("/api/state");
        var employees = state.GetProperty("employees");
        Assert.Equal(2, employees.GetArrayLength());
        Assert.All(employees.EnumerateArray(), employee => Assert.Equal(new[] { "id", "username", "role", "disabled" }, employee.EnumerateObject().Select(p => p.Name)));
        Assert.Equal(HttpStatusCode.Conflict, (await owner.PostAsJsonAsync("/api/admin/staff", new { username = "employee", password = Password })).StatusCode);
    }

    [Fact]
    public async Task MutationsRequireCsrfTokenAndAuthentication()
    {
        await using var factory = new ApiFactory();
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
    public async Task PasswordChangeRejectsOldPasswordAndAllPreviousCookies()
    {
        await using var factory = new ApiFactory();
        factory.Services.GetRequiredService<ClubStore>().CreateAccount("owner", Password, "Owner");
        using var first = factory.CreateClient(); using var second = factory.CreateClient();
        await Login(first, "owner"); await Login(second, "owner");
        var bad = await first.PostAsJsonAsync("/api/password", new PasswordChangeRequest("wrong", " changed-password-123 ", " changed-password-123 "));
        Assert.Equal(HttpStatusCode.Forbidden, bad.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await second.GetAsync("/api/state")).StatusCode);
        var change = await first.PostAsJsonAsync("/api/password", new PasswordChangeRequest(Password, " changed-password-123 ", " changed-password-123 "));
        change.EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Unauthorized, (await first.GetAsync("/api/state")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await second.GetAsync("/api/state")).StatusCode);
        await Token(first);
        Assert.Equal(HttpStatusCode.Unauthorized, (await first.PostAsJsonAsync("/api/login", new { username = "owner", password = Password })).StatusCode);
        (await first.PostAsJsonAsync("/api/login", new { username = "owner", password = " changed-password-123 " })).EnsureSuccessStatusCode();
        var state = await first.GetFromJsonAsync<JsonElement>("/api/state");
        Assert.DoesNotContain("changed-password", state.ToString());
        Assert.Contains(state.GetProperty("audit").EnumerateArray(), row => row.GetProperty("action").GetString() == "PasswordChanged");
    }

    [Fact]
    public async Task OwnerCanRevokeStaffWithoutRevivingCookiesOnEnable()
    {
        await using var factory = new ApiFactory();
        var store = factory.Services.GetRequiredService<ClubStore>();
        var ownerId = store.CreateAccount("owner", Password, "Owner").Id;
        var employee = store.CreateAccount("employee", Password, "Admin", ownerId);
        using var owner = factory.CreateClient(); using var staff = factory.CreateClient();
        await Login(owner, "owner"); await Login(staff, "employee");
        Assert.Equal(HttpStatusCode.Forbidden, (await staff.PostAsJsonAsync($"/api/admin/staff/{ownerId}/access", new StaffAccessRequest(true, "deny"))).StatusCode);
        (await owner.PostAsJsonAsync($"/api/admin/staff/{employee.Id}/access", new StaffAccessRequest(true, "Увольнение"))).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Unauthorized, (await staff.GetAsync("/api/state")).StatusCode);
        using var attempt = factory.CreateClient(); await Token(attempt);
        Assert.Equal(HttpStatusCode.Unauthorized, (await attempt.PostAsJsonAsync("/api/login", new { username = "employee", password = Password })).StatusCode);
        (await owner.PostAsJsonAsync($"/api/admin/staff/{employee.Id}/access", new StaffAccessRequest(false, "Возврат"))).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Unauthorized, (await staff.GetAsync("/api/state")).StatusCode);
        (await owner.PostAsJsonAsync($"/api/admin/staff/{employee.Id}/password", new StaffPasswordRequest("new-password-123", "new-password-123", "Сброс"))).EnsureSuccessStatusCode();
        await Token(attempt);
        Assert.Equal(HttpStatusCode.Unauthorized, (await attempt.PostAsJsonAsync("/api/login", new { username = "employee", password = Password })).StatusCode);
        (await attempt.PostAsJsonAsync("/api/login", new { username = "employee", password = "new-password-123" })).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Forbidden, (await owner.PostAsJsonAsync($"/api/admin/staff/{ownerId}/access", new StaffAccessRequest(true, "deny"))).StatusCode);
    }

    [Theory]
    [InlineData("Owner")]
    [InlineData("Admin")]
    public async Task StaffCanRecoverCustomerPasswordWithoutChangingMoneyOrPaidSession(string actorRole)
    {
        await using var factory = new ApiFactory();
        var store = factory.Services.GetRequiredService<ClubStore>();
        var owner = store.CreateAccount("owner", Password, "Owner");
        var employee = store.CreateAccount("employee", Password, "Admin", owner.Id);
        var customer = store.CreateAccount("visitor", Password);
        var pc = store.AddComputer(owner.Id, new("ПК", Guid.NewGuid().ToString())).GetProperty("id").GetString()!;
        var tariff = store.SavePackage(owner.Id, new(null, "Час", 60, 10_000, true, Guid.NewGuid().ToString())).GetProperty("id").GetString()!;
        store.TopUp(owner.Id, new(customer.Id, 50_000, "Cash", Guid.NewGuid().ToString()));
        var session = store.Purchase(customer.Id, new(pc, tariff, Guid.NewGuid().ToString())).GetProperty("sessionId").GetString();
        using var staff = factory.CreateClient(); using var first = factory.CreateClient(); using var second = factory.CreateClient();
        await Login(staff, actorRole == "Owner" ? "owner" : "employee");
        await Login(first, "visitor"); await Login(second, "visitor");
        var path = $"/api/admin/customers/{customer.Id}/password";
        var reset = new CustomerPasswordRequest(" recovered-password-789 ", " recovered-password-789 ", "Посетитель забыл пароль");
        Assert.Equal(HttpStatusCode.Forbidden, (await first.PostAsJsonAsync(path, reset)).StatusCode);
        foreach (var target in new[] { owner.Id, employee.Id, Guid.NewGuid().ToString() })
            Assert.Equal(HttpStatusCode.NotFound, (await staff.PostAsJsonAsync($"/api/admin/customers/{target}/password", reset)).StatusCode);
        var before = store.FindAccount(customer.Id)!;
        foreach (var invalid in new[] { reset with { ConfirmPassword = "different" }, reset with { Reason = " " }, reset with { NewPassword = "short", ConfirmPassword = "short" } })
            Assert.Equal(HttpStatusCode.BadRequest, (await staff.PostAsJsonAsync(path, invalid)).StatusCode);
        staff.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
        Assert.Equal(HttpStatusCode.BadRequest, (await staff.PostAsJsonAsync(path, reset)).StatusCode);
        await Token(staff);
        Assert.Equal(before.PasswordHash, store.FindAccount(customer.Id)!.PasswordHash);
        Assert.Equal(HttpStatusCode.OK, (await second.GetAsync("/api/state")).StatusCode);
        using var db = Database.Open(Path.Combine(factory.DirectoryPath, "club.db"));
        using var commands = Database.Command(db, null, "SELECT COUNT(*) FROM Commands");
        var commandCount = commands.ExecuteScalar();
        (await staff.PostAsJsonAsync(path, reset)).EnsureSuccessStatusCode();
        Assert.Equal(commandCount, commands.ExecuteScalar());
        Assert.Equal(before.AuthRevision + 1, store.FindAccount(customer.Id)!.AuthRevision);
        Assert.Equal(40_000, store.FindAccount(customer.Id)!.BalanceMinor);
        Assert.Equal(HttpStatusCode.Unauthorized, (await first.GetAsync("/api/state")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await second.GetAsync("/api/state")).StatusCode);
        await Token(first);
        Assert.Equal(HttpStatusCode.Unauthorized, (await first.PostAsJsonAsync("/api/login", new { username = "visitor", password = Password })).StatusCode);
        (await first.PostAsJsonAsync("/api/login", new { username = "visitor", password = reset.NewPassword })).EnsureSuccessStatusCode();
        var state = await staff.GetFromJsonAsync<JsonElement>("/api/state");
        var row = Assert.Single(state.GetProperty("sessions").EnumerateArray());
        Assert.Equal(session, row.GetProperty("id").GetString());
        Assert.Equal("Running", row.GetProperty("status").GetString());
        Assert.Equal(10_000, state.GetProperty("report").GetProperty("purchasesMinor").GetInt64());
        Assert.DoesNotContain("recovered-password", state.ToString());
        var audit = Assert.Single(state.GetProperty("audit").EnumerateArray(), a => a.GetProperty("action").GetString() == "CustomerPasswordReset");
        Assert.Equal(customer.Id, audit.GetProperty("targetId").GetString());
        Assert.Equal(reset.Reason, audit.GetProperty("details").GetString());
    }

    [Fact]
    public async Task LoginErrorsAndRateLimitDoNotLeakAccounts()
    {
        await using var factory = new ApiFactory();
        using var client = factory.CreateClient();
        factory.Services.GetRequiredService<ClubStore>().CreateAccount("owner",Password,"Owner");
        await Token(client);
        for(var i=0;i<10;i++)
            Assert.Equal(HttpStatusCode.Unauthorized,(await client.PostAsJsonAsync("/api/login",new {username="missing",password="invalid"})).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests,(await client.PostAsJsonAsync("/api/login",new {username="owner",password=Password})).StatusCode);
    }
}
