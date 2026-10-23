using System.Text.Json;
using Club.Agent;
using Club.Protocol;

var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Club", "Agent");
var config = JsonSerializer.Deserialize<AgentConfiguration>(File.ReadAllText(Path.Combine(root, "agent.json")), ApiClient.Json)
    ?? throw new InvalidDataException("Отсутствует конфигурация службы.");
var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddWindowsService(options => options.ServiceName = "ClubAgent");
builder.Services.AddSingleton(config);
builder.Services.AddSingleton(new AgentRuntime(root, config));
builder.Services.AddHostedService<AgentWorker>();
builder.Services.AddHostedService<VisitorPipe>();
await builder.Build().RunAsync();
