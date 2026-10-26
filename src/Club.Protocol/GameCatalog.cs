using System.Diagnostics;
using System.Text.RegularExpressions;

namespace Club.Protocol;

public sealed record GameEntry(string Id, string Name, string Executable, string[] Arguments);
public sealed record GameSummary(string Id, string Name);
public sealed record GameLaunchRequest(string Id);
public sealed record GameLaunchApproval(GameEntry Game, string SessionId, long Revision);

// Only administrator-owned configuration supplies paths/arguments. The visitor sends an ID.
// This is a launcher policy, not an OS application allowlist or a kiosk boundary.
public sealed class GameCatalog
{
    private readonly GameEntry[] entries;
    public GameCatalog(IEnumerable<GameEntry> games)
    {
        entries = games.ToArray();
        if (entries.Length > 100) throw new InvalidDataException("В каталоге допускается не более 100 программ.");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var game in entries)
        {
            if (game is null || string.IsNullOrWhiteSpace(game.Id) || !Regex.IsMatch(game.Id, "^[a-zA-Z0-9_-]{1,64}$") || !ids.Add(game.Id))
                throw new InvalidDataException("Недопустимый или повторяющийся ID программы.");
            if (string.IsNullOrWhiteSpace(game.Name) || game.Name.Length > 100 || game.Name.Any(char.IsControl))
                throw new InvalidDataException("Недопустимое название программы.");
            ValidateExecutable(game.Executable);
            if (game.Arguments is null || game.Arguments.Length > 32 || game.Arguments.Any(a => a is null || a.Length > 1024 || a.Any(char.IsControl)))
                throw new InvalidDataException("Недопустимые аргументы программы.");
        }
        entries = entries.Select(Clone).ToArray();
    }
    public GameSummary[] List() => entries.Select(g => new GameSummary(g.Id, g.Name)).ToArray();
    public GameLaunchApproval Authorize(string id, SessionGrant grant)
    {
        if (grant.State != "Running" || grant.RemainingMs <= 0 || string.IsNullOrWhiteSpace(grant.SessionId))
            throw new ApiException(409, "Запуск доступен только во время действующего сеанса.");
        var game = entries.SingleOrDefault(g => g.Id == id)
            ?? throw new ApiException(404, "Программа отсутствует в разрешённом каталоге.");
        return new(Clone(game), grant.SessionId, grant.Revision);
    }
    public static ProcessStartInfo StartInfo(GameEntry game)
    {
        // Validate even the service response; never use a shell, URL handler, elevation or raw arguments.
        _ = new GameCatalog([game]);
        var start = new ProcessStartInfo(game.Executable)
        {
            UseShellExecute = false,
            WorkingDirectory = game.Executable[..(game.Executable.LastIndexOf('\\') + 1)]
        };
        foreach (var argument in game.Arguments) start.ArgumentList.Add(argument);
        return start;
    }
    private static GameEntry Clone(GameEntry game) => game with { Arguments = [.. game.Arguments] };
    private static void ValidateExecutable(string path)
    {
        // Windows drive-qualified local EXE only, with identical validation on Linux and Windows.
        if (string.IsNullOrWhiteSpace(path) || path.Length > 260 || !Regex.IsMatch(path, "^[a-zA-Z]:\\\\") ||
            !path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || path[3..].Any(c => char.IsControl(c) || "<>:\"/|?*".Contains(c)) ||
            path[3..].Split('\\').Any(part => string.IsNullOrWhiteSpace(part) || part is "." or ".." || part.EndsWith('.') || part.EndsWith(' ')))
            throw new InvalidDataException("Укажите полный локальный путь к EXE, без сетевого адреса или переменных окружения.");
    }
}
