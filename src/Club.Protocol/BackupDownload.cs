namespace Club.Protocol;

public static class BackupDownload
{
    public const long MaximumBytes = 1_073_741_824;

    // Publish only a complete, verified file. A previous backup is never overwritten.
    public static async Task Save(HttpResponseMessage response, string destination,
        Func<string, CancellationToken, Task> verify, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(verify);
        if (!response.IsSuccessStatusCode)
            ApiClient.Ensure(new((int)response.StatusCode, await response.Content.ReadAsStringAsync(ct)));
        if (!string.Equals(response.Content.Headers.ContentType?.MediaType, "application/vnd.sqlite3", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Сервер вернул не резервную копию SQLite.");
        var expected = response.Content.Headers.ContentLength;
        if (expected is > MaximumBytes or < 100)
            throw new InvalidDataException("Недопустимый размер копии (предел 1 ГиБ).");
        destination = Path.GetFullPath(destination);
        if (File.Exists(destination)) throw new IOException("Этот файл уже существует. Выберите новое имя копии.");
        var temporary = Path.Combine(Path.GetDirectoryName(destination)!, ".club-backup-" + Guid.NewGuid().ToString("N") + ".part");
        try
        {
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous | FileOptions.WriteThrough))
            await using (var input = await response.Content.ReadAsStreamAsync(ct))
            {
                var header = new byte[16];
                await input.ReadExactlyAsync(header, ct);
                if (!header.AsSpan().SequenceEqual("SQLite format 3\0"u8)) throw new InvalidDataException("Повреждён заголовок резервной копии.");
                await output.WriteAsync(header, ct);
                var buffer = new byte[65536];
                long total = header.Length;
                int count;
                while ((count = await input.ReadAsync(buffer, ct)) != 0)
                {
                    total += count;
                    if (total > MaximumBytes) throw new InvalidDataException("Копия превышает предел 1 ГиБ.");
                    await output.WriteAsync(buffer.AsMemory(0, count), ct);
                }
                if (total < 100 || (expected.HasValue && total != expected.Value))
                    throw new InvalidDataException("Копия загружена не полностью.");
                await output.FlushAsync(ct);
                output.Flush(true);
            }
            await verify(temporary, ct);
            ct.ThrowIfCancellationRequested();
            File.Move(temporary, destination, false);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
