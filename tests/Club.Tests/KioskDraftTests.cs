using System.Text.Json;
using System.Xml.Linq;
using Club.Protocol;
using Xunit;

namespace Club.Tests;

public sealed class KioskDraftTests
{
    private static KioskDraftRequest Request => new(@"PC-01\Посетитель", @"C:\Program Files\Club\Visitor\Club.Visitor.exe",
        [new("game", "Game", @"D:\Games\A&B\game.exe", ["--private-argument"])]);
    private static readonly XNamespace Ns = "http://schemas.microsoft.com/AssignedAccess/2017/config";

    [Fact]
    public void TargetsOnlyOneAccountAndListsExactExecutablesWithoutArguments()
    {
        var text = KioskDraft.Create(Request);
        var xml = XDocument.Parse(text);
        Assert.Equal(Ns + "AssignedAccessConfiguration", xml.Root!.Name);
        Assert.Equal(Request.LocalAccount, Assert.Single(xml.Descendants(Ns + "Account")).Value);
        var profile = Assert.Single(xml.Descendants(Ns + "Profile"));
        Assert.True(Guid.TryParse(profile.Attribute("Id")!.Value, out _));
        Assert.Equal(profile.Attribute("Id")!.Value, Assert.Single(xml.Descendants(Ns + "DefaultProfile")).Attribute("Id")!.Value);
        Assert.Equal(new[] { Request.VisitorExecutable, Request.Games[0].Executable },
            xml.Descendants(Ns + "App").Select(app => app.Attribute("DesktopAppPath")!.Value));
        XNamespace rs5 = "http://schemas.microsoft.com/AssignedAccess/201810/config";
        Assert.Equal(Request.VisitorExecutable, Assert.Single(xml.Descendants(Ns + "App"),
            app => app.Attribute(rs5 + "AutoLaunch")?.Value == "true").Attribute("DesktopAppPath")!.Value);
        XNamespace v5 = "http://schemas.microsoft.com/AssignedAccess/2022/config";
        using var pins = JsonDocument.Parse(Assert.Single(xml.Descendants(v5 + "StartPins")).Value);
        Assert.Equal(0, pins.RootElement.GetProperty("pinnedList").GetArrayLength());
        Assert.DoesNotContain("--private-argument", text);
        Assert.Empty(xml.Descendants(Ns + "UserGroup"));
        Assert.Empty(xml.Descendants(Ns + "AutoLogonAccount"));
        Assert.Empty(xml.Descendants(Ns + "KioskModeApp"));
    }

    [Fact]
    public void DeduplicatesPathsAndAllowsEmptyCatalog()
    {
        Assert.Single(XDocument.Parse(KioskDraft.Create(Request with { Games = [] })).Descendants(Ns + "App"));
        var duplicate = Request.Games[0] with { Id = "second", Executable = Request.Games[0].Executable.ToUpperInvariant() };
        Assert.Equal(2, XDocument.Parse(KioskDraft.Create(Request with { Games = [Request.Games[0], duplicate] })).Descendants(Ns + "App").Count());
    }

    [Theory]
    [InlineData("Visitor")]
    [InlineData(@"PC\*")]
    [InlineData(@"PC\Visitor\Extra")]
    [InlineData(@"PC\<Admin/>")]
    [InlineData(@"PC\ Visitor")]
    public void RejectsAmbiguousAccounts(string account) => Assert.Throws<InvalidDataException>(() => KioskDraft.Create(Request with { LocalAccount = account }));

    [Theory]
    [InlineData(@"%ProgramFiles%\Club\Visitor.exe")]
    [InlineData(@"C:\Games\%TEMP%\game.exe")]
    [InlineData(@"C:\Games\*.exe")]
    [InlineData(@"\\server\share\game.exe")]
    public void RejectsExpandedWildcardAndNetworkPaths(string path) =>
        Assert.Throws<InvalidDataException>(() => KioskDraft.Create(Request with { VisitorExecutable = path }));
}
