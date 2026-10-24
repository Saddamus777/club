using System.Text.Json;
using System.Xml.Linq;

namespace Club.Protocol;

public sealed record KioskDraftRequest(string LocalAccount, string VisitorExecutable, GameEntry[] Games);

// A static Assigned Access candidate, never a payment gate or proof of Windows enforcement.
// Apply only in a separate Windows 11 trial after reviewing dependencies and file permissions.
public static class KioskDraft
{
    public static string Create(KioskDraftRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var account = request.LocalAccount?.Split('\\');
        if (account is not { Length: 2 } || account.Any(part => string.IsNullOrWhiteSpace(part) ||
            part != part.Trim() || part.Length > 64 || part.Any(c => char.IsControl(c) || "/\"[]:;|=,+*?<>%".Contains(c))))
            throw new InvalidDataException("Укажите конкретную локальную учётную запись COMPUTER\\Visitor.");
        if (request.Games is null) throw new InvalidDataException("Каталог не задан.");
        _ = new GameCatalog(request.Games);
        _ = new GameCatalog([new("visitor", "Visitor", request.VisitorExecutable, [])]);
        var paths = new[] { request.VisitorExecutable }.Concat(request.Games.Select(g => g.Executable))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        // CSP expands environment variables; a literal filename containing '%' is ambiguous here.
        if (paths.Any(path => path.Contains('%')))
            throw new InvalidDataException("Пути профиля не должны содержать переменные окружения или знак %.");
        XNamespace ns = "http://schemas.microsoft.com/AssignedAccess/2017/config";
        XNamespace rs5 = "http://schemas.microsoft.com/AssignedAccess/201810/config";
        XNamespace v5 = "http://schemas.microsoft.com/AssignedAccess/2022/config";
        var id = Guid.NewGuid().ToString("B").ToUpperInvariant();
        var apps = paths.Select((path, index) => new XElement(ns + "App",
            new XAttribute("DesktopAppPath", path),
            index == 0 ? new XAttribute(rs5 + "AutoLaunch", "true") : null));
        var document = new XDocument(new XElement(ns + "AssignedAccessConfiguration",
            new XAttribute(XNamespace.Xmlns + "rs5", rs5), new XAttribute(XNamespace.Xmlns + "v5", v5),
            new XElement(ns + "Profiles", new XElement(ns + "Profile", new XAttribute("Id", id),
                new XAttribute("Name", "Club trial draft"),
                new XElement(ns + "AllAppsList", new XElement(ns + "AllowedApps", apps)),
                // Start pins are deliberately empty; the Visitor launches automatically.
                new XElement(v5 + "StartPins", JsonSerializer.Serialize(new { pinnedList = Array.Empty<string>() })),
                new XElement(ns + "Taskbar", new XAttribute("ShowTaskbar", "true")))),
            new XElement(ns + "Configs", new XElement(ns + "Config",
                new XElement(ns + "Account", request.LocalAccount),
                new XElement(ns + "DefaultProfile", new XAttribute("Id", id))))));
        return document.ToString();
    }
}
