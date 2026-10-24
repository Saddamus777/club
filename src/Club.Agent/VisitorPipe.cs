using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using Club.Protocol;

namespace Club.Agent;

public sealed class VisitorPipe(AgentConfiguration configuration, AgentRuntime runtime, LocalGames games, ILogger<VisitorPipe> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var acl = new PipeSecurity();
        acl.SetAccessRuleProtection(true, false);
        acl.SetOwner(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null));
        acl.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));
        acl.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        acl.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(configuration.VisitorSid), PipeAccessRights.ReadWrite, AccessControlType.Allow));
        while (!stoppingToken.IsCancellationRequested)
        {
            using var pipe = NamedPipeServerStreamAcl.Create(PipeWire.Name, PipeDirection.InOut, 1,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 16384, 16384, acl);
            await pipe.WaitForConnectionAsync(stoppingToken);
            // One interactive visitor connection per gaming PC; credentials/cookies disappear with that pipe.
            try { await Serve(pipe, stoppingToken); }
            catch (Exception ex) when (ex is IOException or InvalidDataException or OperationCanceledException or UnauthorizedAccessException or JsonException)
            { logger.LogDebug("Visitor pipe closed: {Type}", ex.GetType().Name); }
        }
    }
    private async Task Serve(NamedPipeServerStream pipe, CancellationToken ct)
    {
        pipe.RunAsClient(() =>
        {
            using var identity = WindowsIdentity.GetCurrent();
            if (identity.User?.Value != configuration.VisitorSid) throw new UnauthorizedAccessException();
        });
        using var api = new ApiClient(configuration.ServerUrl, configuration.ComputerId, configuration.Token);
        while (pipe.IsConnected)
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(TimeSpan.FromMinutes(2));
            var request = await PipeWire.Read<RpcRequest>(pipe, deadline.Token, 16384);
            RpcResponse response;
            try
            {
                if (request.Path == "/local/status" && request.Json is null)
                    response = new(200, JsonSerializer.Serialize(runtime.Status, ApiClient.Json));
                else if (request.Path == "/local/games" && request.Json is null)
                    response = new(200, JsonSerializer.Serialize(games.Read().List(), ApiClient.Json));
                else if (request.Path == "/local/games/launch" && request.Json is not null)
                {
                    var launch = JsonSerializer.Deserialize<GameLaunchRequest>(request.Json, ApiClient.Json)
                        ?? throw new InvalidDataException();
                    var approval = games.Read().Authorize(launch.Id, runtime.Status.Grant);
                    if (!File.Exists(approval.Game.Executable)) throw new ApiException(404, "EXE не найден. Обратитесь к сотруднику.");
                    response = new(200, JsonSerializer.Serialize(approval, ApiClient.Json));
                }
                else
                {
                    var allowed = request.Json is null ? request.Path is "/api/state" or "/api/security"
                        : request.Path is "/api/login" or "/api/register" or "/api/logout" or "/api/purchases";
                    if (!allowed) throw new UnauthorizedAccessException();
                    if (request.Path == "/api/purchases")
                    {
                        using var doc = JsonDocument.Parse(request.Json!);
                        request = request with { Json = JsonSerializer.Serialize(new
                        {
                            computerId = configuration.ComputerId,
                            packageId = doc.RootElement.GetProperty("packageId").GetString(),
                            operationId = doc.RootElement.GetProperty("operationId").GetString()
                        }, ApiClient.Json) };
                    }
                    if (request.Json is not null) await api.Security(ct);
                    response = await api.Send(request, ct);
                    if (request.Path == "/api/login" && response.Status == 200 && ApiClient.Ensure(response).GetProperty("role").GetString() != "Customer")
                        throw new UnauthorizedAccessException();
                }
            }
            catch (ApiException ex)
            { response = new(ex.Status, JsonSerializer.Serialize(new { error = ex.Message }, ApiClient.Json)); }
            catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidDataException)
            { response = new(400, "{\"error\":\"Некорректный запрос посетителя.\"}"); }
            catch (IOException)
            { response = new(503, "{\"error\":\"Каталог временно недоступен. Обратитесь к сотруднику.\"}"); }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException)
            { response = new(503, "{\"error\":\"Сервер недоступен. Если исход покупки неизвестен, повторите прежнюю операцию.\"}"); }
            await PipeWire.Write(pipe, response, ct);
        }
    }
}
