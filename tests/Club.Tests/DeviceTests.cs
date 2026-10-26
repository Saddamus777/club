using System.Security.Cryptography;
using System.Text.Json;
using Club.Protocol;
using Club.Server;
using Microsoft.AspNetCore.Identity;
using Xunit;

namespace Club.Tests;

public sealed class DeviceTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "club-device-" + Guid.NewGuid().ToString("N"));
    private readonly ManualTime time = new();
    private ClubStore store;
    private readonly string owner, customer, computer, package;
    private readonly string token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    private string boot = Key();
    private long sequence;
    private string Header => computer + ":" + token;
    private static string Key() => Guid.NewGuid().ToString();
    public DeviceTests()
    {
        store = Open();
        owner = store.CreateAccount("owner", "password-test-6789", "Owner").Id;
        customer = store.CreateAccount("visitor", "password-test-6789").Id;
        computer = store.AddComputer(owner, new("ПК 1", Key())).GetProperty("id").GetString()!;
        package = store.SavePackage(owner, new(null, "Час", 60, 10_000, true, Key())).GetProperty("id").GetString()!;
        store.TopUp(owner, new(customer, 100_000, "Cash", Key()));
        store.BindDevice(owner, new(computer, token, Key()));
    }
    private ClubStore Open() => new(directory, time, new PasswordHasher<Account>());
    private string Buy() => store.Purchase(customer, new(computer, package, Key()), Header).GetProperty("sessionId").GetString()!;
    private SessionGrant Beat(SessionGrant? grant = null) => store.DeviceHeartbeat(Header,
        new(boot, ++sequence, grant?.SessionId, grant?.Revision ?? 0, grant?.RemainingMs ?? 0, grant?.State ?? "Idle"));
    private JsonElement Snapshot() => JsonSerializer.SerializeToElement(store.Snapshot(owner), ApiClient.Json);
    public void Dispose() { store.Dispose(); Directory.Delete(directory, true); }

    [Fact]
    public void BindingRequiresStaffAndDoesNotExposeTokenInSnapshotOrAudit()
    {
        Assert.Equal(403, Assert.Throws<RuleException>(() => store.BindDevice(customer, new(computer, token, Key()))).Status);
        Assert.DoesNotContain(token, Snapshot().GetRawText());
        using var db = Database.Open(Path.Combine(directory, "club.db"));
        using var cmd = Database.Command(db, null, "SELECT TokenHash FROM Devices");
        Assert.NotEqual(token, cmd.ExecuteScalar());
    }
    [Fact]
    public void OfflineAndUnauthorizedDevicesCannotStartSessions()
    {
        Assert.Equal(409, Assert.Throws<RuleException>(() => Buy()).Status);
        Assert.Equal(401, Assert.Throws<RuleException>(() => store.DeviceHeartbeat(computer + ":" + new string('0', 64), new(boot, 1, null, 0, 0, "Idle"))).Status);
        Beat();
        Assert.Equal(401, Assert.Throws<RuleException>(() => store.Purchase(customer, new(computer, package, Key()))).Status);
        time.Add(TimeSpan.FromSeconds(11));
        Assert.Equal(409, Assert.Throws<RuleException>(() => Buy()).Status);
        Assert.Equal(100_000, store.FindAccount(customer)!.BalanceMinor);
    }
    [Fact]
    public void PurchaseIsDeviceBoundAndRetryDoesNotChargeAgain()
    {
        Beat();
        var request = new PurchaseRequest(computer, package, Key());
        var result = store.Purchase(customer, request, Header);
        Assert.Equal(result.GetRawText(), store.Purchase(customer, request, Header).GetRawText());
        Assert.Equal(90_000, store.FindAccount(customer)!.BalanceMinor);
        Assert.Throws<RuleException>(() => store.BindDevice(owner, new(computer, token, Key())));
        var second = store.AddComputer(owner, new("ПК 2", Key())).GetProperty("id").GetString()!;
        store.BindDevice(owner, new(second, token, Key()));
        Assert.Throws<RuleException>(() => store.Purchase(customer, new(second, package, Key()), Header));
    }
    [Fact]
    public void OfflineTimeIsReconciledAndPauseRequiresActualAcknowledgment()
    {
        Beat(); var id = Buy(); var grant = Beat(); grant = Beat(grant);
        time.Add(TimeSpan.FromMinutes(10)); store.Tick();
        Assert.Equal("Running", Snapshot().GetProperty("sessions")[0].GetProperty("status").GetString());
        store.ChangeSession(owner, id, "pause", new("Пауза", Key()));
        Assert.Throws<RuleException>(() => store.ChangeSession(owner, id, "resume", new("Рано", Key())));
        var pause = Beat(grant with { RemainingMs = 3_000_000 });
        Assert.Equal("Paused", pause.State); Assert.Equal(3_000_000, pause.RemainingMs);
        Beat(pause);
        store.ChangeSession(owner, id, "resume", new("Продолжение", Key()));
        var running = Beat(pause);
        Assert.Equal("Running", running.State); Assert.Equal(3_000_000, running.RemainingMs);
    }
    [Fact]
    public void EndPendingKeepsComputerAndCustomerReservedAndBlocksRefund()
    {
        Beat(); var id = Buy(); var grant = Beat(); Beat(grant);
        store.ChangeSession(owner, id, "end", new("Ушёл", Key()));
        Assert.Throws<RuleException>(() => Buy());
        var other = store.AddComputer(owner, new("Другое", Key())).GetProperty("id").GetString()!;
        Assert.Throws<RuleException>(() => store.Purchase(customer, new(other, package, Key())));
        Assert.Equal(1, Snapshot().GetProperty("computers").EnumerateArray().Single(x => x.GetProperty("id").GetString() == computer).GetProperty("occupied").GetInt32());
        Assert.Throws<RuleException>(() => store.Refund(owner, new(id, 1, "Cash", "Возврат", Key())));
        var ended = Beat(grant with { RemainingMs = 1_800_000 });
        Assert.Equal("Ended", ended.State); Beat(ended);
        Assert.Throws<RuleException>(() => store.Refund(owner, new(id, 5001, "Cash", "Превышение", Key())));
        store.Refund(owner, new(id, 5000, "Cash", "Остаток", Key()));
        Assert.Equal(90_000, store.FindAccount(customer)!.BalanceMinor);
        Buy();
    }
    [Fact]
    public void RebootPausesAndServerRestartCannotRestoreSpentTime()
    {
        Beat(); var id = Buy(); var grant = Beat(); Beat(grant with { RemainingMs = 2_000_000 });
        boot = Key(); sequence = 0;
        var pause = Beat(grant with { State = "Recovery", RemainingMs = 1_900_000 });
        Assert.Equal("Paused", pause.State); Assert.Equal(1_900_000, pause.RemainingMs);
        Beat(pause);
        store.ChangeSession(owner, id, "resume", new("Возобновить", Key()));
        var resumed = Beat(pause); Beat(resumed);
        store.Dispose(); store = Open();
        var recovered = Beat(resumed with { RemainingMs = 1_800_000 });
        Assert.Equal("Paused", recovered.State); Assert.Equal(1_800_000, recovered.RemainingMs);
        Assert.True(recovered.Revision > resumed.Revision);
        Assert.Throws<RuleException>(() => store.ChangeSession(owner, id, "resume", new("Рано", Key())));
    }
    [Fact]
    public void ReplayedReportsCannotRewindTimerOrAckFutureRevision()
    {
        Beat(); Buy(); var grant = Beat();
        var report = new DeviceReport(boot, ++sequence, grant.SessionId, grant.Revision, 1_000_000, "Running");
        Assert.Equal(1_000_000, store.DeviceHeartbeat(Header, report).RemainingMs);
        Assert.Throws<RuleException>(() => store.DeviceHeartbeat(Header, report));
        Assert.Equal(1_000_000, Beat(grant).RemainingMs);
        var future = Beat(grant with { Revision = grant.Revision + 100, RemainingMs = 0 });
        Assert.Equal(1_000_000, future.RemainingMs);
    }
    [Fact]
    public void CompletionReleasesOnlyAfterEnforcementAcknowledgment()
    {
        Beat(); Buy(); var grant = Beat(); Beat(grant);
        Beat(grant with { RemainingMs = 0, State = "Running" });
        Assert.Throws<RuleException>(() => Buy());
        var complete = Beat(grant with { RemainingMs = 0, State = "Completed" });
        Assert.Equal("Completed", complete.State);
        Buy();
    }
    [Fact]
    public void OldBackupMigratesAndManagedBackupPreservesPendingAcknowledgment()
    {
        Beat(); Buy(); var managedBackup = store.Backup(owner); Database.Verify(managedBackup);
        var legacy = Path.Combine(directory, "legacy.db"); File.Copy(managedBackup, legacy);
        using (var db = Database.Open(legacy))
        {
            Database.Execute(db, null, "ALTER TABLE Accounts DROP COLUMN Disabled; ALTER TABLE Accounts DROP COLUMN AuthRevision; DROP TABLE Devices; DROP INDEX OneActiveComputer; DROP INDEX OneActiveCustomer; ALTER TABLE Sessions DROP COLUMN Managed; ALTER TABLE Sessions DROP COLUMN AckRevision; ALTER TABLE Sessions DROP COLUMN Revision; CREATE UNIQUE INDEX OneActiveComputer ON Sessions(ComputerId) WHERE Status IN ('Running','Paused'); CREATE UNIQUE INDEX OneActiveCustomer ON Sessions(CustomerId) WHERE Status IN ('Running','Paused'); PRAGMA user_version=1;");
        }
        Database.Verify(legacy);
        var restore = Path.Combine(directory, "restored"); Database.Restore(restore, legacy);
        using (var upgraded = new ClubStore(restore, time, new PasswordHasher<Account>()))
        {
            Assert.Equal(90_000, upgraded.FindAccount(customer)!.BalanceMinor);
            Database.Verify(upgraded.Backup(owner));
        }
        var restore2 = Path.Combine(directory, "managed-restored"); Database.Restore(restore2, managedBackup);
        using var restored = new ClubStore(restore2, time, new PasswordHasher<Account>());
        var state = JsonSerializer.SerializeToElement(restored.Snapshot(owner), ApiClient.Json).GetProperty("sessions")[0];
        Assert.Equal("Paused", state.GetProperty("status").GetString());
        Assert.True(state.GetProperty("revision").GetInt64() > state.GetProperty("ackRevision").GetInt64());
    }
}

public sealed class MonotonicTime : TimeProvider
{
    private long ticks;
    public override long TimestampFrequency => 1000;
    public override long GetTimestamp() => ticks;
    public void Advance(long ms) => ticks += ms;
}

public sealed class SessionClockTests
{
    [Fact]
    public void TimerRunsOfflineAndRepeatedGrantsNeverAddTime()
    {
        var time = new MonotonicTime(); var clock = new SessionClock(time);
        var grant = new SessionGrant("s", 1, 600_000, "Running"); clock.Apply(grant);
        time.Advance(120_000); clock.Apply(grant);
        Assert.Equal(480_000, clock.Current.RemainingMs);
        time.Advance(480_000); Assert.Equal("Completed", clock.Current.State);
        clock.Apply(grant); Assert.Equal(0, clock.Current.RemainingMs);
    }
    [Fact]
    public void PausePreservesTimeAndRecoveryNeverAutoResumes()
    {
        var time = new MonotonicTime(); var clock = new SessionClock(time);
        clock.Apply(new("s", 1, 600_000, "Running")); time.Advance(10_000);
        clock.Apply(new("s", 2, 600_000, "Paused")); time.Advance(100_000);
        Assert.Equal(590_000, clock.Current.RemainingMs);
        var saved = clock.Current; clock = new SessionClock(time); clock.Recover(saved);
        time.Advance(100_000); Assert.Equal("Recovery", clock.Current.State); Assert.Equal(590_000, clock.Current.RemainingMs);
        clock.Apply(new("s", 3, 590_000, "Running")); time.Advance(1000);
        Assert.Equal(589_000, clock.Current.RemainingMs);
        Assert.Throws<InvalidDataException>(() => clock.Apply(new("s", 2, 590_000, "Running")));
    }
    [Fact]
    public async Task PipeRejectsOversizedInputBeforeAllocatingPayload()
    {
        using var stream = new MemoryStream();
        await stream.WriteAsync(BitConverter.GetBytes(int.MaxValue)); stream.Position = 0;
        await Assert.ThrowsAsync<InvalidDataException>(() => PipeWire.Read<RpcRequest>(stream, CancellationToken.None, 16384));
    }
}
