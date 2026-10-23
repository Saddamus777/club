using Club.Protocol;
using Xunit;

namespace Club.Tests;

public sealed class GameCatalogTests
{
    private static GameEntry Game => new("game", "Игра", @"C:\Games\Example\game.exe", ["--profile", "two words"]);
    private static SessionGrant Running => new("session", 3, 10_000, "Running");

    [Theory]
    [InlineData("Idle")]
    [InlineData("Paused")]
    [InlineData("Completed")]
    [InlineData("Ended")]
    [InlineData("Recovery")]
    public void NoLaunchWithoutRunningGrant(string state)
    {
        var catalog = new GameCatalog([Game]);
        Assert.Equal(409, Assert.Throws<ApiException>(() => catalog.Authorize("game", Running with { State = state })).Status);
    }

    [Fact]
    public void RequiresRemainingTimeAndSessionAndKnownId()
    {
        var catalog = new GameCatalog([Game]);
        Assert.Throws<ApiException>(() => catalog.Authorize("game", Running with { RemainingMs = 0 }));
        Assert.Throws<ApiException>(() => catalog.Authorize("game", Running with { SessionId = null }));
        Assert.Equal(404, Assert.Throws<ApiException>(() => catalog.Authorize(@"C:\Windows\cmd.exe", Running)).Status);
        var approval = catalog.Authorize("game", Running);
        Assert.Equal("session", approval.SessionId);
        Assert.Equal(3, approval.Revision);
        Assert.Equal(Game.Executable, approval.Game.Executable);
    }

    [Theory]
    [InlineData("game.exe")]
    [InlineData(@"C:game.exe")]
    [InlineData(@"\\server\share\game.exe")]
    [InlineData("https://example.com/game.exe")]
    [InlineData(@"C:\Games\..\game.exe")]
    [InlineData(@"C:\Games\game.exe:stream.exe")]
    [InlineData(@"C:\Games\game.cmd")]
    [InlineData(@"C:\Games\game.exe\")]
    public void RejectsAmbiguousOrNonExecutablePaths(string path) =>
        Assert.Throws<InvalidDataException>(() => new GameCatalog([Game with { Executable = path }]));

    [Fact]
    public void CatalogIsBoundedAndCannotBeMutatedByCaller()
    {
        Assert.Throws<InvalidDataException>(() => new GameCatalog([Game, Game]));
        Assert.Throws<InvalidDataException>(() => new GameCatalog([Game with { Arguments = null! }]));
        Assert.Throws<InvalidDataException>(() => new GameCatalog([Game with { Id = "../game" }]));
        Assert.Throws<InvalidDataException>(() => new GameCatalog([null!]));
        var game = Game;
        var catalog = new GameCatalog([game]);
        game.Arguments[0] = "modified";
        var approval = catalog.Authorize("game", Running);
        Assert.Equal("--profile", approval.Game.Arguments[0]);
        approval.Game.Arguments[0] = "modified again";
        Assert.Equal("--profile", catalog.Authorize("game", Running).Game.Arguments[0]);
    }

    [Fact]
    public void StartInfoUsesExactArgumentBoundariesWithoutShellOrElevation()
    {
        var start = GameCatalog.StartInfo(Game with { Arguments = ["two words", "& whoami", "\"quoted\""] });
        Assert.False(start.UseShellExecute);
        Assert.Empty(start.Verb);
        Assert.Empty(start.UserName);
        Assert.Empty(start.Arguments);
        Assert.Equal(new[] { "two words", "& whoami", "\"quoted\"" }, start.ArgumentList);
        Assert.Equal(@"C:\Games\Example\", start.WorkingDirectory);
        Assert.Equal(@"C:\", GameCatalog.StartInfo(Game with { Executable = @"C:\game.exe" }).WorkingDirectory);
    }

    [Fact]
    public void OfflineGrantExpiresWithoutNetworkOrWallClock()
    {
        var time = new TestTime();
        var clock = new SessionClock(time);
        clock.Apply(Running);
        var catalog = new GameCatalog([Game]);
        Assert.Equal("game", catalog.Authorize("game", clock.Current).Game.Id);
        time.Tick = 10_000;
        Assert.Throws<ApiException>(() => catalog.Authorize("game", clock.Current));
        clock.Recover(Running);
        Assert.Throws<ApiException>(() => catalog.Authorize("game", clock.Current));
    }

    private sealed class TestTime : TimeProvider
    {
        public long Tick { get; set; }
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => Tick;
    }
}
