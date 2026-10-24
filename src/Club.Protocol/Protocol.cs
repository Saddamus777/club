using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace Club.Protocol;

public sealed record DeviceReport(string BootId, long Sequence, string? SessionId, long Revision, long RemainingMs, string State);
public sealed record SessionGrant(string? SessionId, long Revision, long RemainingMs, string State);
public sealed record DeviceBinding(string ComputerId, string Token, string OperationId);
public sealed record RpcRequest(string Path, string? Json = null);
public sealed record RpcResponse(int Status, string Json);
public sealed record AgentStatus(SessionGrant Grant, bool Connected, string Message);
public sealed record AgentConfiguration(string ServerUrl, string ComputerId, string Token, string VisitorSid);

// Length-prefixed messages bound memory use and survive partial pipe reads.
public static class PipeWire
{
    public const string Name = "Club.Visitor.v1";
    public static async Task Write<T>(Stream stream, T message, CancellationToken ct)
    {
        var data = JsonSerializer.SerializeToUtf8Bytes(message, ApiClient.Json);
        if (data.Length > 1_048_576) throw new InvalidDataException("Сообщение слишком большое.");
        var prefix = new byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(prefix, data.Length);
        await stream.WriteAsync(prefix, ct);
        await stream.WriteAsync(data, ct);
        await stream.FlushAsync(ct);
    }
    public static async Task<T> Read<T>(Stream stream, CancellationToken ct, int maximum = 1_048_576)
    {
        var prefix = new byte[4];
        await stream.ReadExactlyAsync(prefix, ct);
        var length = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(prefix);
        if (length < 1 || length > maximum) throw new InvalidDataException("Недопустимый размер сообщения.");
        var data = new byte[length];
        await stream.ReadExactlyAsync(data, ct);
        return JsonSerializer.Deserialize<T>(data, ApiClient.Json) ?? throw new InvalidDataException("Пустое сообщение.");
    }
}

public sealed class ApiException(int status, string message) : InvalidOperationException(message)
{
    public int Status { get; } = status;
}

public sealed class ApiClient : IDisposable
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly HttpClient http;
    public ApiClient(string serverUrl, string? device = null, string? token = null)
    {
        var uri = new Uri(serverUrl);
        if (uri.Scheme != "https" && !(uri.Scheme == "http" && uri.IsLoopback))
            throw new ArgumentException("Для локальной сети требуется HTTPS.");
        if (!string.IsNullOrEmpty(uri.UserInfo) || uri.AbsolutePath != "/" || uri.Query != "" || uri.Fragment != "")
            throw new ArgumentException("Укажите только адрес сервера и порт.");
        http = new(new HttpClientHandler { CookieContainer = new CookieContainer(), AllowAutoRedirect = false })
        { BaseAddress = uri, Timeout = TimeSpan.FromSeconds(5) };
        if (device is not null) http.DefaultRequestHeaders.Add("X-Club-Device", device + ":" + token);
    }
    public async Task<RpcResponse> Send(RpcRequest request, CancellationToken ct = default)
    {
        using var message = new HttpRequestMessage(request.Json is null ? HttpMethod.Get : HttpMethod.Post, request.Path);
        if (request.Json is not null) message.Content = new StringContent(request.Json, Encoding.UTF8, "application/json");
        using var response = await http.SendAsync(message, ct);
        var json = await response.Content.ReadAsStringAsync(ct);
        return new((int)response.StatusCode, json.Length == 0 ? "{}" : json);
    }
    public async Task Security(CancellationToken ct = default)
    {
        var result = await Send(new("/api/security"), ct);
        Ensure(result);
        using var doc = JsonDocument.Parse(result.Json);
        http.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
        http.DefaultRequestHeaders.Add("X-CSRF-TOKEN", doc.RootElement.GetProperty("token").GetString());
    }
    public async Task DownloadBackup(string destination, Func<string, CancellationToken, Task> verify, CancellationToken ct = default)
    {
        if (File.Exists(destination)) throw new IOException("Этот файл уже существует. Выберите новое имя копии.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(2));
        await Security(timeout.Token);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/admin/backups")
            { Content = new StringContent("{}", Encoding.UTF8, "application/json") };
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        await BackupDownload.Save(response, destination, verify, timeout.Token);
    }
    public static JsonElement Ensure(RpcResponse response)
    {
        if (response.Status is >= 200 and < 300) return JsonSerializer.Deserialize<JsonElement>(response.Json);
        string error;
        try { error = JsonSerializer.Deserialize<JsonElement>(response.Json).GetProperty("error").GetString()!; }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException) { error = "Ошибка сервера: " + response.Status; }
        throw new ApiException(response.Status, error);
    }
    public void Dispose() => http.Dispose();
}
