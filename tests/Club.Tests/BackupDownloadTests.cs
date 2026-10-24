using System.Net;
using System.Net.Http.Headers;
using Club.Protocol;
using Xunit;

namespace Club.Tests;

public sealed class BackupDownloadTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "club-download-" + Guid.NewGuid().ToString("N"));
    public BackupDownloadTests() => Directory.CreateDirectory(directory);
    public void Dispose() => Directory.Delete(directory, true);
    private string Target => Path.Combine(directory, "backup.db");
    private static byte[] Bytes()
    {
        var bytes = new byte[512]; "SQLite format 3\0"u8.CopyTo(bytes); return bytes;
    }
    private static HttpResponseMessage Response(byte[]? bytes = null)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes ?? Bytes()) };
        response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/vnd.sqlite3");
        return response;
    }
    [Fact]
    public async Task PublishesOnlyAfterVerifierFinishesAndNeverOverwrites()
    {
        using var response = Response();
        await BackupDownload.Save(response, Target, async (temporary, _) =>
        {
            Assert.False(File.Exists(Target));
            Assert.Equal(Bytes(), await File.ReadAllBytesAsync(temporary));
        });
        Assert.Equal(Bytes(), await File.ReadAllBytesAsync(Target));
        using var repeat = Response();
        await Assert.ThrowsAsync<IOException>(() => BackupDownload.Save(repeat, Target, (_, _) => throw new InvalidOperationException("Must not verify an existing destination.")));
        Assert.Single(Directory.GetFiles(directory));
    }
    [Fact]
    public async Task IntegrityFailureOrCancellationDoesNotLeaveAnAcceptedFile()
    {
        using var response = Response();
        await Assert.ThrowsAsync<InvalidDataException>(() => BackupDownload.Save(response, Target, (_, _) => throw new InvalidDataException("Bad ledger.")));
        Assert.Empty(Directory.GetFiles(directory));
        using var cancel = new CancellationTokenSource();
        using var second = Response();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => BackupDownload.Save(second, Target, (_, _) => { cancel.Cancel(); return Task.CompletedTask; }, cancel.Token));
        Assert.Empty(Directory.GetFiles(directory));
    }
    [Fact]
    public async Task RefusesTruncationWrongHeaderAndOversizedResponses()
    {
        using var truncated = Response(); truncated.Content.Headers.ContentLength = 1024;
        await Assert.ThrowsAsync<InvalidDataException>(() => BackupDownload.Save(truncated, Target, (_, _) => Task.CompletedTask));
        using var corrupt = Response(new byte[512]);
        await Assert.ThrowsAsync<InvalidDataException>(() => BackupDownload.Save(corrupt, Target, (_, _) => Task.CompletedTask));
        using var oversized = Response(); oversized.Content.Headers.ContentLength = BackupDownload.MaximumBytes + 1;
        await Assert.ThrowsAsync<InvalidDataException>(() => BackupDownload.Save(oversized, Target, (_, _) => Task.CompletedTask));
        Assert.Empty(Directory.GetFiles(directory));
    }
    [Fact]
    public async Task HandlesAuthorizationErrorsAndHtmlWithoutSavingThemAsDatabase()
    {
        using var denied = new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent("{\"error\":\"Forbidden\"}") };
        Assert.Equal(403, (await Assert.ThrowsAsync<ApiException>(() => BackupDownload.Save(denied, Target, (_, _) => Task.CompletedTask))).Status);
        using var html = Response(); html.Content.Headers.ContentType = new MediaTypeHeaderValue("text/html");
        await Assert.ThrowsAsync<InvalidDataException>(() => BackupDownload.Save(html, Target, (_, _) => Task.CompletedTask));
        Assert.Empty(Directory.GetFiles(directory));
    }
    [Fact]
    public async Task AnotherFileCreatedDuringDownloadIsPreserved()
    {
        using var response = Response();
        await Assert.ThrowsAsync<IOException>(() => BackupDownload.Save(response, Target, async (_, _) => await File.WriteAllTextAsync(Target, "previous-copy")));
        Assert.Equal("previous-copy", await File.ReadAllTextAsync(Target));
        Assert.Single(Directory.GetFiles(directory));
    }
    [Fact]
    public async Task InterruptedStreamRemovesPartialFileWithoutCallingVerifier()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new InterruptedStream(Bytes())) };
        response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/vnd.sqlite3");
        await Assert.ThrowsAsync<IOException>(() => BackupDownload.Save(response, Target, (_, _) => throw new InvalidOperationException("Incomplete download must not reach verification.")));
        Assert.Empty(Directory.GetFiles(directory));
    }
    private sealed class InterruptedStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            Position >= 16 ? ValueTask.FromException<int>(new IOException("Connection lost.")) : base.ReadAsync(buffer, cancellationToken);
    }
}
