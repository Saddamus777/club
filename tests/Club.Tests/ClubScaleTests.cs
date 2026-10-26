using System.Security.Cryptography;
using System.Text.Json;
using Club.Protocol;
using Club.Server;
using Microsoft.AspNetCore.Identity;
using Xunit;

namespace Club.Tests;

// A concurrent server/store scenario, not a network throughput or physical-PC benchmark.
public sealed class ClubScaleTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "club-scale-" + Guid.NewGuid().ToString("N"));
    private readonly ManualTime time = new();
    private ClubStore store;
    private readonly string owner;
    private readonly string package;
    private static string Key() => Guid.NewGuid().ToString();
    public ClubScaleTests()
    {
        store = Open(root);
        owner = store.CreateAccount("owner", "scale-test-password", "Owner").Id;
        package = store.SavePackage(owner, new(null, "Час", 60, 10_000, true, Key())).GetProperty("id").GetString()!;
    }
    private ClubStore Open(string directory) => new(directory, time, new PasswordHasher<Account>());
    public void Dispose() { store.Dispose(); Directory.Delete(root, true); }
    private sealed class Station(string customer, string computer, string header)
    {
        public string Customer { get; } = customer;
        public string Computer { get; } = computer;
        public string Header { get; } = header;
        public string Boot { get; } = Key();
        public long Sequence;
        public SessionGrant Grant = new(null, 0, 0, "Idle");
        public string Session = "";
    }
    private SessionGrant Beat(Station pc, SessionGrant? report = null)
    {
        report ??= pc.Grant;
        return pc.Grant = store.DeviceHeartbeat(pc.Header,
            new(pc.Boot, ++pc.Sequence, report.SessionId, report.Revision, report.RemainingMs, report.State));
    }
    // Release all operations from one async barrier without blocking thread-pool threads.
    private static async Task Together(IEnumerable<Action> operations)
    {
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tasks = operations.Select(action => Task.Run(async () => { await start.Task; action(); })).ToArray();
        start.SetResult();
        await Task.WhenAll(tasks);
    }
    private JsonElement Snapshot(ClubStore source) => JsonSerializer.SerializeToElement(source.Snapshot(owner), ApiClient.Json);
    private void AssertMoney(ClubStore source)
    {
        var report = Snapshot(source).GetProperty("report");
        Assert.Equal(3_000_000, report.GetProperty("topUpsMinor").GetInt64());
        Assert.Equal(300_000, report.GetProperty("purchasesMinor").GetInt64());
        Assert.Equal(50_000, report.GetProperty("refundsMinor").GetInt64());
        Assert.Equal(2_700_000, report.GetProperty("walletBalancesMinor").GetInt64());
        Assert.Equal(2_950_000, report.GetProperty("cashNetMinor").GetInt64());
    }

    [Fact]
    public async Task ThirtyStationsPreserveMoneyAndTimeAcrossRetriesOfflineEndRestartAndRestore()
    {
        var stations = Enumerable.Range(1, 30).Select(i =>
        {
            var customer = store.CreateAccount("visitor" + i, "scale-test-password").Id;
            var computer = store.AddComputer(owner, new("ПК " + i, Key())).GetProperty("id").GetString()!;
            var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            store.BindDevice(owner, new(computer, token, Key()));
            store.TopUp(owner, new(customer, 100_000, "Cash", Key()));
            return new Station(customer, computer, computer + ":" + token);
        }).ToArray();
        await Together(stations.Select<Station, Action>(pc => () => Beat(pc)));
        var purchases = stations.Select(pc => new PurchaseRequest(pc.Computer, package, Key())).ToArray();
        // Three simultaneously released retries per operation must still debit exactly once.
        await Together(stations.SelectMany((pc, i) => Enumerable.Range(0, 3).Select<int, Action>(_ => () =>
            store.Purchase(pc.Customer, purchases[i], pc.Header))));
        await Together(stations.Select<Station, Action>(pc => () =>
        {
            pc.Session = store.Purchase(pc.Customer, purchases[Array.IndexOf(stations, pc)], pc.Header).GetProperty("sessionId").GetString()!;
            Assert.Equal("Running", Beat(pc).State);
            Beat(pc);
            Assert.Equal(90_000, store.FindAccount(pc.Customer)!.BalanceMinor);
            Assert.Equal(409, Assert.Throws<RuleException>(() =>
                store.Purchase(pc.Customer, new(pc.Computer, package, Key()), pc.Header)).Status);
        }));
        Assert.Equal(30, Snapshot(store).GetProperty("sessions").GetArrayLength());
        // The server must not infer device time consumption while all devices are offline.
        time.Add(TimeSpan.FromMinutes(30)); store.Tick();
        Assert.All(Snapshot(store).GetProperty("sessions").EnumerateArray(), s =>
            Assert.Equal(3_600_000, s.GetProperty("remainingMs").GetInt64()));
        await Together(stations.Select<Station, Action>(pc => () => Beat(pc, pc.Grant with { RemainingMs = 1_800_000 })));
        var ended = stations.Take(10).ToArray();
        await Together(ended.Select<Station, Action>(pc => () =>
        {
            store.ChangeSession(owner, pc.Session, "end", new("Ушёл", Key()));
            Assert.Equal(409, Assert.Throws<RuleException>(() =>
                store.Refund(owner, new(pc.Session, 5000, "Cash", "Остаток", Key()))).Status);
            Assert.Equal("Ended", Beat(pc).State);
            Beat(pc); // Acknowledge actual enforcement before allowing cash refund.
        }));
        var refunds = ended.Select(pc => new RefundRequest(pc.Session, 5000, "Cash", "Остаток", Key())).ToArray();
        await Together(refunds.SelectMany(request => Enumerable.Range(0, 3).Select<int, Action>(_ => () => store.Refund(owner, request))));
        AssertMoney(store);
        Assert.Equal(20, Snapshot(store).GetProperty("computers").EnumerateArray().Sum(pc => pc.GetProperty("occupied").GetInt32()));
        store.Dispose(); store = Open(root);
        var active = stations.Skip(10).ToArray();
        await Together(active.Select<Station, Action>(pc => () =>
        {
            var previousRevision = pc.Grant.Revision;
            var paused = Beat(pc, pc.Grant with { RemainingMs = 1_799_000 });
            Assert.Equal("Paused", paused.State);
            Assert.Equal(1_799_000, paused.RemainingMs);
            Assert.True(paused.Revision > previousRevision);
            Assert.Equal(409, Assert.Throws<RuleException>(() =>
                store.ChangeSession(owner, pc.Session, "resume", new("Рано", Key()))).Status);
            Beat(pc);
        }));
        AssertMoney(store);
        var backup = store.Backup(owner); Database.Verify(backup);
        var restoredRoot = Path.Combine(root, "restored"); Database.Restore(restoredRoot, backup);
        using var restored = Open(restoredRoot);
        AssertMoney(restored);
        foreach (var pc in stations) Assert.Equal(90_000, restored.FindAccount(pc.Customer)!.BalanceMinor);
        var restoredSessions = Snapshot(restored).GetProperty("sessions").EnumerateArray().ToArray();
        Assert.Equal(10, restoredSessions.Count(s => s.GetProperty("status").GetString() == "Ended"));
        Assert.Equal(20, restoredSessions.Count(s => s.GetProperty("status").GetString() == "Paused"));
        Assert.All(restoredSessions.Where(s => s.GetProperty("status").GetString() == "Paused"), s =>
            Assert.Equal(1_799_000, s.GetProperty("remainingMs").GetInt64()));
        Database.Verify(restored.Backup(owner));
    }
}
