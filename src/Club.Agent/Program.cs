using System.Text.Json;
using Club.Agent;
using Club.Protocol;

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
