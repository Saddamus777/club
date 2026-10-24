using System.Text.Json;
using Club.Agent;
using Club.Protocol;

if (args.Length == 3 && args[0] == "--export-kiosk-draft")
{
    try
    {
        var input = new FileInfo(args[1]);
        if (input.Length > 524288) throw new InvalidDataException("Draft request is too large.");
        var request = JsonSerializer.Deserialize<KioskDraftRequest>(File.ReadAllText(input.FullName), ApiClient.Json)
            ?? throw new InvalidDataException("Missing draft request.");
        var xml = KioskDraft.Create(request);
        foreach (var path in new[] { request.VisitorExecutable }.Concat(request.Games.Select(g => g.Executable)))
            if (!File.Exists(path)) throw new FileNotFoundException("Draft executable not found: " + path);
        // No configuration or secret is read, and an existing output is never replaced.
        using var output = new FileStream(args[2], FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var writer = new StreamWriter(output, new System.Text.UTF8Encoding(false));
        writer.Write(xml);
        Console.WriteLine("Assigned Access draft exported. No Windows policy was applied.");
    }
    catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or UnauthorizedAccessException or ArgumentException)
    { Console.WriteLine("Draft rejected: " + ex.Message); Environment.ExitCode = 1; }
    return;
}

if (args.Length == 2 && args[0] == "--validate-games")
{
    try
    {
        if (!File.Exists(args[1])) throw new FileNotFoundException("Catalog file not found.");
        var catalog = new LocalGames(args[1]).Read();
        foreach (var game in catalog.List())
        {
            var entry = catalog.Authorize(game.Id, new SessionGrant("validation", 1, 1, "Running")).Game;
            if (!File.Exists(entry.Executable)) throw new FileNotFoundException("Game EXE not found: " + game.Name);
        }
        Console.WriteLine("Catalog validated: " + catalog.List().Length);
    }
    catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or UnauthorizedAccessException)
    { Console.WriteLine("Catalog rejected: " + ex.Message); Environment.ExitCode = 1; }
    return;
}

// A mistyped maintenance command must not start a second session-enforcement worker.
if (args.Length != 0)
{
    Console.WriteLine("Usage: Club.Agent (service), --validate-games FILE, or --export-kiosk-draft REQUEST OUTPUT.");
    Environment.ExitCode = 1;
    return;
}

var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Club", "Agent");
var config = JsonSerializer.Deserialize<AgentConfiguration>(File.ReadAllText(Path.Combine(root, "agent.json")), ApiClient.Json)
    ?? throw new InvalidDataException("Отсутствует конфигурация службы.");
var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddWindowsService(options => options.ServiceName = "ClubAgent");
builder.Services.AddSingleton(config);
builder.Services.AddSingleton(new LocalGames(Path.Combine(root, "games.json")));
builder.Services.AddSingleton(new AgentRuntime(root, config));
builder.Services.AddHostedService<AgentWorker>();
builder.Services.AddHostedService<VisitorPipe>();
await builder.Build().RunAsync();
