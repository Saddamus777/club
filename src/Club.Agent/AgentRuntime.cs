using System.Text.Json;
using Club.Protocol;

namespace Club.Agent;

public sealed class AgentRuntime
{
    private readonly object gate = new();
    private readonly SessionClock clock = new(TimeProvider.System);
    private readonly string checkpoint;
    private readonly string visitorSid;
    private bool connected;
    private string message = "Подключение к серверу…";
    public AgentRuntime(string root, AgentConfiguration configuration)
    {
        checkpoint = Path.Combine(root, "session.json");
        visitorSid = configuration.VisitorSid;
        if (File.Exists(checkpoint)) clock.Recover(JsonSerializer.Deserialize<SessionGrant>(File.ReadAllText(checkpoint), ApiClient.Json)
            ?? throw new InvalidDataException("Повреждён остаток сеанса."));
    }
    public AgentStatus Status { get { lock (gate) return new(clock.Current, connected, message); } }
    public void Connection(bool available, string text) { lock (gate) { connected = available; message = text; } }
    public void Apply(SessionGrant grant) { lock (gate) { clock.Apply(grant); Save(); } }
    public SessionGrant EnforceAndSave()
    {
        lock (gate)
        {
            var current = clock.Current;
            // Do not acknowledge pause/end/expiry before Windows confirms the visitor was logged off.
            if (current.State is "Paused" or "Completed" or "Ended" or "Recovery") WindowsSessions.LogOff(visitorSid);
            Save();
            return current;
        }
    }
    private void Save()
    {
        var temp = checkpoint + ".tmp";
        using (var file = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        {
            JsonSerializer.Serialize(file, clock.Current, ApiClient.Json);
            file.Flush(true);
        }
        File.Move(temp, checkpoint, true);
    }
}

public sealed class AgentWorker(AgentRuntime runtime, AgentConfiguration configuration, ILogger<AgentWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Every process start is a new boot: a saved grant cannot resume itself after restart.
        var boot = Guid.NewGuid().ToString();
        long sequence = 0;
        using var api = new ApiClient(configuration.ServerUrl, configuration.ComputerId, configuration.Token);
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(250));
        Task<RpcResponse>? pending = null;
        var nextPoll = DateTimeOffset.MinValue;
        while (!stoppingToken.IsCancellationRequested)
        {
            SessionGrant state;
            try { state = runtime.EnforceAndSave(); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogCritical(ex, "Windows enforcement or checkpoint failed; service will stop and recover.");
                // Best effort logoff even when disk persistence failed. SCM recovery restarts the service.
                Environment.ExitCode = 1;
                WindowsSessions.LogOff(configuration.VisitorSid);
                throw;
            }
            if (pending is { IsCompleted: true })
            {
                try
                {
                    var result = ApiClient.Ensure(await pending);
                    runtime.Apply(result.Deserialize<SessionGrant>(ApiClient.Json) ?? throw new InvalidDataException("Пустое разрешение."));
                    runtime.Connection(true, "Связь с сервером установлена");
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException or JsonException or InvalidDataException)
                {
                    runtime.Connection(false, "Нет связи с сервером. Действует только уже оплаченный остаток.");
                    logger.LogWarning("Heartbeat failed: {Type}", ex.GetType().Name);
                }
                pending = null;
                nextPoll = DateTimeOffset.UtcNow.AddSeconds(1);
                // Enforce the newly received command before sending the next acknowledgment.
                continue;
            }
            if (pending is null && DateTimeOffset.UtcNow >= nextPoll)
            {
                var report = new DeviceReport(boot, ++sequence, state.SessionId, state.Revision, state.RemainingMs, state.State);
                pending = api.Send(new("/device/heartbeat", JsonSerializer.Serialize(report, ApiClient.Json)), stoppingToken);
            }
            await timer.WaitForNextTickAsync(stoppingToken);
        }
    }
}
