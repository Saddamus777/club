using System.Text.Json;
using Club.Protocol;

namespace Club.Agent;

public sealed class LocalGames(string path)
{
    // Read on demand: a malformed catalog must disable launching, never stop session enforcement.
    public GameCatalog Read()
    {
        if (!File.Exists(path)) return new GameCatalog([]);
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length > 524288) throw new InvalidDataException("Каталог слишком большой.");
        return new GameCatalog(JsonSerializer.Deserialize<GameEntry[]>(file, ApiClient.Json)
            ?? throw new InvalidDataException("Пустой каталог."));
    }
}
