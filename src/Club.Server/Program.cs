using System.Security.Claims;
using System.Diagnostics;
using System.Net;
using System.Threading.RateLimiting;
using Club.Server;
using Club.Protocol;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.RateLimiting;

var builder = WebApplication.CreateBuilder(args);
var dataDirectory = Path.GetFullPath(builder.Configuration["Club:DataDirectory"] ??
    Path.Combine(AppContext.BaseDirectory, ".data"));

if (args.Contains("--verify-backup") || args.Contains("--restore-backup"))
{
    var restore = args.Contains("--restore-backup");
    var index = Array.IndexOf(args, restore ? "--restore-backup" : "--verify-backup");
    if (index + 1 >= args.Length) throw new ArgumentException("Укажите путь к файлу резервной копии.");
    if (restore) Database.Restore(dataDirectory, Path.GetFullPath(args[index + 1]));
    else Database.Verify(Path.GetFullPath(args[index + 1]));
    Console.WriteLine(restore ? "Копия восстановлена. Предыдущая база сохранена. Сеансы потребуют ручного возобновления." : "Копия читается; целостность и баланс журнала проверены.");
    return;
}
LanHosting.Configure(builder);
Directory.CreateDirectory(dataDirectory);

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<IPasswordHasher<Account>, PasswordHasher<Account>>();
builder.Services.Configure<PasswordHasherOptions>(options => options.IterationCount = 210_000);
builder.Services.AddSingleton(sp => new ClubStore(dataDirectory, sp.GetRequiredService<TimeProvider>(), sp.GetRequiredService<IPasswordHasher<Account>>()));
builder.Services.AddDataProtection().SetApplicationName("ClubPrototype").PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(dataDirectory, "keys")));
builder.Services.AddAntiforgery(options =>
{
    options.HeaderName = "X-CSRF-TOKEN";
    options.Cookie.SameSite = SameSiteMode.Strict;
    options.Cookie.HttpOnly = true;
});
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie(options =>
{
    options.Cookie.Name = "Club.Session";
    options.Cookie.HttpOnly = true;
    options.Cookie.SameSite = SameSiteMode.Strict;
    options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
    options.ExpireTimeSpan = TimeSpan.FromHours(8);
    options.SlidingExpiration = false;
    options.Events.OnRedirectToLogin = context => { context.Response.StatusCode = 401; return Task.CompletedTask; };
    options.Events.OnRedirectToAccessDenied = context => { context.Response.StatusCode = 403; return Task.CompletedTask; };
    options.Events.OnValidatePrincipal = context =>
    {
        var id = context.Principal?.FindFirstValue(ClaimTypes.NameIdentifier);
        var account = id is null ? null : context.HttpContext.RequestServices.GetRequiredService<ClubStore>().FindAccount(id);
        if (account is null || account.Disabled || context.Principal?.FindFirstValue("credentialStamp") != ClubStore.CredentialStamp(account)) context.RejectPrincipal();
        return Task.CompletedTask;
    };
});
builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("Staff", policy => policy.RequireRole("Owner", "Admin"));
    options.AddPolicy("Owner", policy => policy.RequireRole("Owner"));
    options.AddPolicy("Customer", policy => policy.RequireRole("Customer"));
});
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = 429;
    options.AddPolicy("device", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "local", _ => new FixedWindowRateLimiterOptions
        { PermitLimit = 120, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
    options.AddPolicy("auth", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "local", _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 10, Window = TimeSpan.FromMinutes(1), QueueLimit = 0
        }));
});
if (!builder.Configuration.GetValue<bool>("Club:DisableWorker")) builder.Services.AddHostedService<SessionWorker>();
var app = builder.Build();
var store = app.Services.GetRequiredService<ClubStore>();

if (args.Contains("--create-owner"))
{
    Console.Write("Логин владельца: ");
    var username = Console.ReadLine() ?? "";
    Console.Write("Пароль (10–128 символов, ввод скрыт): ");
    var password = ReadPassword();
    Console.Write("Повторите пароль: ");
    if (password != ReadPassword()) throw new ArgumentException("Пароли не совпадают.");
    store.CreateAccount(username, password, "Owner");
    Console.WriteLine("Аккаунт владельца создан. Запустите сервер обычной командой.");
    await app.DisposeAsync();
    return;
}

app.Use(async (context, next) =>
{
    context.Response.Headers.ContentSecurityPolicy = "default-src 'self'; script-src 'self'; style-src 'self'; connect-src 'self'; img-src 'self'; frame-ancestors 'none'; base-uri 'none'; form-action 'self'";
    context.Response.Headers.XContentTypeOptions = "nosniff";
    context.Response.Headers["Referrer-Policy"] = "no-referrer";
    context.Response.Headers.CacheControl = "no-store";
    try
    {
        if (context.Connection.RemoteIpAddress is { } address && !IPAddress.IsLoopback(address))
        {
            if (!context.Request.IsHttps) throw new RuleException("Для LAN требуется HTTPS.", 403);
            if (context.Request.Path.StartsWithSegments("/api/admin")) throw new RuleException("Управление доступно только на ПК администратора.", 403);
        }
        await next();
    }
    catch (RuleException ex)
    {
        context.Response.StatusCode = ex.Status;
        await context.Response.WriteAsJsonAsync(new { error = ex.Message });
    }
    catch (BadHttpRequestException)
    {
        context.Response.StatusCode = 400;
        await context.Response.WriteAsJsonAsync(new { error = "Некорректные данные запроса." });
    }
    catch (Exception ex) when (ex is not OperationCanceledException)
    {
        app.Logger.LogError(ex, "Request failed: {Method} {Path}", context.Request.Method, context.Request.Path);
        if (context.Response.HasStarted) throw;
        context.Response.StatusCode = 500;
        await context.Response.WriteAsJsonAsync(new { error = "Операция не выполнена. Проверьте журнал сервера; повторяйте запрос с прежним идентификатором." });
    }
});
app.UseDefaultFiles();
app.UseStaticFiles();
app.UseAuthentication();
app.Use(async (context, next) =>
{
    if (context.Connection.RemoteIpAddress is { } address && !IPAddress.IsLoopback(address) &&
        (context.User.IsInRole("Owner") || context.User.IsInRole("Admin")))
        throw new RuleException("Сотрудник может работать только на ПК администратора.", 403);
    await next();
});
app.UseAuthorization();
app.UseRateLimiter();
app.Use(async (context, next) =>
{
    if (context.Request.Method == "POST" && context.Request.Path.StartsWithSegments("/api"))
    {
        try { await context.RequestServices.GetRequiredService<IAntiforgery>().ValidateRequestAsync(context); }
        catch (AntiforgeryValidationException)
        {
            context.Response.StatusCode = 400;
            await context.Response.WriteAsJsonAsync(new { error = "Обновите страницу: проверка безопасности запроса не пройдена." });
            return;
        }
    }
    await next();
});

// Device endpoint uses its own credential and accepts HTTPS only; it never uses cookie authority.
app.MapPost("/device/heartbeat", (HttpContext context, DeviceReport report) =>
{
    if (!context.Request.IsHttps) throw new RuleException("Для устройства требуется HTTPS.", 403);
    return store.DeviceHeartbeat(context.Request.Headers["X-Club-Device"], report);
}).RequireRateLimiting("device");
app.MapGet("/health", () => Results.Ok(new { status = "ok", prototype = true }));
app.MapGet("/api/security", (HttpContext context, IAntiforgery antiforgery) =>
    Results.Ok(new { token = antiforgery.GetAndStoreTokens(context).RequestToken, ownerExists = store.HasOwner() }));
app.MapPost("/api/setup/owner", (OwnerSetupRequest request, HttpContext context) =>
{
    // First-run setup is local-only, CSRF-protected and atomically disabled after one owner exists.
    var address = context.Connection.RemoteIpAddress;
    if (address is null || !IPAddress.IsLoopback(address)) throw new RuleException("Первоначальная настройка доступна только на компьютере сервера.", 403);
    if (request.Password != request.ConfirmPassword) throw new RuleException("Пароли не совпадают.");
    var account = store.CreateAccount(request.Username ?? "", request.Password ?? "", "Owner");
    return Results.Ok(new { account.Id, account.Username, account.Role });
}).RequireRateLimiting("auth");
app.MapPost("/api/register", (RegisterRequest request) =>
{
    if (!store.HasOwner()) throw new RuleException("Сначала завершите первоначальную настройку на стартовой странице.", 409);
    var account = store.CreateAccount(request.Username ?? "", request.Password ?? "");
    return Results.Ok(new { account.Id, account.Username });
}).RequireRateLimiting("auth");
app.MapPost("/api/login", async (RegisterRequest request, HttpContext context) =>
{
    var account = store.FindAccount((request.Username ?? "").Trim(), true);
    if (account is null || !store.VerifyPassword(account, request.Password ?? "")) throw new RuleException("Неверный логин или пароль.", 401);
    if (account.Role is "Owner" or "Admin" && context.Connection.RemoteIpAddress is { } remote && !IPAddress.IsLoopback(remote))
        throw new RuleException("Вход сотрудника доступен только на ПК администратора.", 403);
    var identity = new ClaimsIdentity(new[]
    {
        new Claim(ClaimTypes.NameIdentifier, account.Id), new Claim(ClaimTypes.Name, account.Username), new Claim(ClaimTypes.Role, account.Role), new Claim("credentialStamp", ClubStore.CredentialStamp(account))
    }, CookieAuthenticationDefaults.AuthenticationScheme);
    await context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity));
    return Results.Ok(new { account.Username, account.Role });
}).RequireRateLimiting("auth");
app.MapPost("/api/logout", async (HttpContext context) =>
{
    await context.SignOutAsync();
    return Results.Ok();
}).RequireAuthorization();
app.MapPost("/api/password", async (HttpContext context, PasswordChangeRequest request) =>
{
    store.ChangePassword(Id(context.User), request);
    await context.SignOutAsync();
    return Results.Ok(new { changed = true });
}).RequireAuthorization().RequireRateLimiting("auth");
app.MapGet("/api/state", (ClaimsPrincipal user) => store.Snapshot(Id(user))).RequireAuthorization();
app.MapPost("/api/purchases", (ClaimsPrincipal user, PurchaseRequest request, HttpContext context) => store.Purchase(Id(user), request, context.Request.Headers["X-Club-Device"])).RequireAuthorization("Customer");
var admin = app.MapGroup("/api/admin").RequireAuthorization("Staff");
admin.MapPost("/staff", (ClaimsPrincipal user, RegisterRequest request) =>
{
    var account = store.CreateAccount(request.Username ?? "", request.Password ?? "", "Admin", Id(user));
    return Results.Ok(new { account.Id, account.Username, account.Role });
}).RequireAuthorization("Owner");
admin.MapPost("/staff/{id}/access", (ClaimsPrincipal user, string id, StaffAccessRequest request) =>
{
    store.ManageStaff(Id(user), id, request, null); return Results.Ok();
}).RequireAuthorization("Owner");
admin.MapPost("/staff/{id}/password", (ClaimsPrincipal user, string id, StaffPasswordRequest request) =>
{
    store.ManageStaff(Id(user), id, null, request); return Results.Ok();
}).RequireAuthorization("Owner");
admin.MapPost("/customers/{id}/password", (ClaimsPrincipal user, string id, CustomerPasswordRequest request) =>
{
    store.ResetCustomerPassword(Id(user), id, request); return Results.Ok();
});
admin.MapPost("/devices", (ClaimsPrincipal user, DeviceBinding request) => store.BindDevice(Id(user), request));
admin.MapPost("/computers", (ClaimsPrincipal user, ComputerRequest request) => store.AddComputer(Id(user), request));
admin.MapPost("/packages", (ClaimsPrincipal user, PackageRequest request) => store.SavePackage(Id(user), request));
admin.MapPost("/topups", (ClaimsPrincipal user, TopUpRequest request) => store.TopUp(Id(user), request));
admin.MapPost("/free-sessions", (ClaimsPrincipal user, FreeSessionRequest request) => store.FreeSession(Id(user), request));
admin.MapPost("/sessions/{id}/{action}", (ClaimsPrincipal user, string id, string action, SessionActionRequest request) => store.ChangeSession(Id(user), id, action, request));
admin.MapPost("/refunds", (ClaimsPrincipal user, RefundRequest request) => store.Refund(Id(user), request));
admin.MapPost("/backups", (ClaimsPrincipal user) =>
{
    var backup = store.Backup(Id(user));
    return Results.File(backup, "application/vnd.sqlite3", Path.GetFileName(backup));
});
if (args.Contains("--open-browser") && OperatingSystem.IsWindows())
{
    app.Lifetime.ApplicationStarted.Register(() =>
    {
        try { Process.Start(new ProcessStartInfo("http://127.0.0.1:5080") { UseShellExecute = true }); }
        catch (Exception ex)
        {
            app.Logger.LogWarning(ex, "Could not open the browser. Use 03-open.cmd while the server is running.");
        }
    });
}
app.Run();

static string Id(ClaimsPrincipal user) => user.FindFirstValue(ClaimTypes.NameIdentifier) ?? throw new RuleException("Войдите в аккаунт.", 401);
static string ReadPassword()
{
    if (Console.IsInputRedirected) return Console.ReadLine() ?? "";
    var chars = new List<char>();
    while (true)
    {
        var key = Console.ReadKey(true);
        if (key.Key == ConsoleKey.Enter) { Console.WriteLine(); return new string(chars.ToArray()); }
        if (key.Key == ConsoleKey.Backspace) { if (chars.Count > 0) chars.RemoveAt(chars.Count - 1); }
        else if (!char.IsControl(key.KeyChar) && chars.Count < 128) chars.Add(key.KeyChar);
    }
}

public partial class Program;
