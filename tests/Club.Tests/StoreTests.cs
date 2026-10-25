using System.Text.Json;
using Club.Server;
using Microsoft.AspNetCore.Identity;
using Xunit;

namespace Club.Tests;

public sealed class ManualTime : TimeProvider
{
    private DateTimeOffset current = new(2026, 10, 23, 10, 0, 0, TimeSpan.Zero);
    public override DateTimeOffset GetUtcNow() => current;
    public void Add(TimeSpan duration) => current += duration;
}

public sealed class StoreTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "club-tests-" + Guid.NewGuid().ToString("N"));
    private readonly ManualTime time = new();
    private ClubStore store;
    private readonly string owner;
    private readonly string customer;
    private readonly string computer;
    private readonly string package;
    private const string Password = "test-only-password-127";
    public StoreTests()
    {
        store = OpenStore();
        owner = store.CreateAccount("owner", Password, "Owner").Id;
        customer = store.CreateAccount("visitor", Password).Id;
        computer = store.AddComputer(owner, new("ПК-01", Key())).GetProperty("id").GetString()!;
        package = store.SavePackage(owner, new(null, "Три часа", 180, 30_000, true, Key())).GetProperty("id").GetString()!;
    }
    [Fact]
    public void VersionTwoUpgradePreservesBalancesAndBlockedStaffSurvivesBackupRestore()
    {
        TopUp(12345);
        store.Dispose();
        using (var db = Database.Open(Path.Combine(directory, "club.db")))
            Database.Execute(db, null, "ALTER TABLE Accounts DROP COLUMN Disabled; ALTER TABLE Accounts DROP COLUMN AuthRevision; PRAGMA user_version=2;");
        store = OpenStore();
        Assert.Equal(12345, store.FindAccount(customer)!.BalanceMinor);
        Assert.False(store.FindAccount(owner)!.Disabled);
        var employee = store.CreateAccount("employee", Password, "Admin", owner);
        store.ManageStaff(owner, employee.Id, new StaffAccessRequest(true, "Увольнение"), null);
        var backup = store.Backup(owner);
        store.Dispose(); Database.Restore(directory, backup); store = OpenStore();
        Assert.True(store.FindAccount(employee.Id)!.Disabled);
        Assert.Equal(1, store.FindAccount(employee.Id)!.AuthRevision);
        Assert.Throws<RuleException>(() => store.TopUp(employee.Id, new(customer, 1, "Cash", Key())));
        Assert.Equal(12345, store.FindAccount(customer)!.BalanceMinor);
    }

    private ClubStore OpenStore() => new(directory, time, new PasswordHasher<Account>());
    private static string Key() => Guid.NewGuid().ToString();
    private void TopUp(long amount = 50_000) => store.TopUp(owner, new(customer, amount, "Cash", Key()));
    private string Buy() => store.Purchase(customer, new(computer, package, Key())).GetProperty("sessionId").GetString()!;
    private JsonElement Snapshot(string? actor = null) => JsonSerializer.SerializeToElement(store.Snapshot(actor ?? owner), new JsonSerializerOptions(JsonSerializerDefaults.Web));
    private void AdvanceSeconds(int seconds)
    {
        for (var i = 0; i < seconds; i += 10)
        {
            time.Add(TimeSpan.FromSeconds(Math.Min(10, seconds - i)));
            store.Tick();
        }
    }
    public void Dispose() { store.Dispose(); Directory.Delete(directory, true); }

    [Fact]
    public void PurchaseIsAtomicAndRepeatable()
    {
        TopUp();
        var request = new PurchaseRequest(computer, package, Key());
        var first = store.Purchase(customer, request);
        var second = store.Purchase(customer, request);
        Assert.Equal(first.GetRawText(), second.GetRawText());
        Assert.Equal(20_000, store.FindAccount(customer)!.BalanceMinor);
        Assert.Single(Snapshot().GetProperty("sessions").EnumerateArray());
        Assert.Equal(30_000, Snapshot().GetProperty("report").GetProperty("purchasesMinor").GetInt64());
    }

    [Fact]
    public void IdempotencyKeyCannotBeReusedForDifferentAmountOrActor()
    {
        var key = Key();
        store.TopUp(owner, new(customer, 1000, "Cash", key));
        var error = Assert.Throws<RuleException>(() => store.TopUp(owner, new(customer, 2000, "Cash", key)));
        Assert.Equal(409, error.Status);
        Assert.Equal(1000, store.FindAccount(customer)!.BalanceMinor);
        Assert.Throws<RuleException>(() => store.TopUp(customer, new(customer, 1000, "Cash", key)));
    }

    [Fact]
    public void InsufficientBalanceLeavesNoSessionOrDebit()
    {
        TopUp(1000);
        Assert.Throws<RuleException>(() => Buy());
        Assert.Empty(Snapshot().GetProperty("sessions").EnumerateArray());
        Assert.Equal(1000, store.FindAccount(customer)!.BalanceMinor);
    }

    [Fact]
    public async Task ConcurrentPurchasesCannotDoubleSpendOrOccupyTwoComputers()
    {
        TopUp(60_000);
        var second = store.AddComputer(owner, new("ПК-02", Key())).GetProperty("id").GetString()!;
        var results = await Task.WhenAll(new[] { computer, second }.Select(id => Task.Run(() =>
        {
            try { store.Purchase(customer, new(id, package, Key())); return true; }
            catch (RuleException ex) when (ex.Status == 409) { return false; }
        })));
        Assert.Single(results, x => x);
        Assert.Equal(30_000, store.FindAccount(customer)!.BalanceMinor);
    }

    [Fact]
    public async Task TwoCustomersCannotBuySameComputer()
    {
        var other = store.CreateAccount("visitor2", Password).Id;
        TopUp(); store.TopUp(owner, new(other, 50_000, "Cash", Key()));
        var results = await Task.WhenAll(new[] { customer, other }.Select(id => Task.Run(() =>
        {
            try { store.Purchase(id, new(computer, package, Key())); return true; }
            catch (RuleException ex) when (ex.Status == 409) { return false; }
        })));
        Assert.Single(results, x => x);
        Assert.Equal(70_000, store.FindAccount(customer)!.BalanceMinor + store.FindAccount(other)!.BalanceMinor);
    }

    [Fact]
    public void ManualPauseFreezesTimeUntilStaffResumes()
    {
        TopUp(); var session = Buy(); AdvanceSeconds(60);
        store.ChangeSession(owner, session, "pause", new("Нет интернета", Key()));
        time.Add(TimeSpan.FromHours(2)); store.Tick();
        Assert.Equal(179 * 60_000, Snapshot().GetProperty("sessions")[0].GetProperty("remainingMs").GetInt64());
        store.ChangeSession(owner, session, "resume", new("Связь восстановлена", Key()));
        AdvanceSeconds(60);
        Assert.Equal(178 * 60_000, Snapshot().GetProperty("sessions")[0].GetProperty("remainingMs").GetInt64());
    }

    [Fact]
    public void ExpiryReleasesComputerWithoutAutomaticWalletRefund()
    {
        var shortPackage = store.SavePackage(owner, new(null, "Минута", 1, 100, true, Key())).GetProperty("id").GetString()!;
        TopUp();store.Purchase(customer, new(computer, shortPackage, Key()));AdvanceSeconds(61);
        var session = Snapshot().GetProperty("sessions")[0];
        Assert.Equal("Completed", session.GetProperty("status").GetString());
        Assert.Equal(0, session.GetProperty("remainingMs").GetInt64());
        Assert.Equal(49_900, store.FindAccount(customer)!.BalanceMinor);
        Assert.Equal(0, Snapshot().GetProperty("computers")[0].GetProperty("occupied").GetInt64());
    }

    [Fact]
    public void RestartPreservesLastConfirmedTimeAndRequiresManualResume()
    {
        TopUp(); Buy(); AdvanceSeconds(60);
        store.Dispose();time.Add(TimeSpan.FromHours(8));store = OpenStore();
        var session = Snapshot().GetProperty("sessions")[0];
        Assert.Equal("Paused", session.GetProperty("status").GetString());
        Assert.Equal(179 * 60_000, session.GetProperty("remainingMs").GetInt64());
        Assert.Equal(20_000, store.FindAccount(customer)!.BalanceMinor);
    }

    [Fact]
    public void ClockDiscontinuityRequiresReviewInsteadOfSpendingUnconfirmedTime()
    {
        TopUp();Buy();time.Add(TimeSpan.FromMinutes(30));store.Tick();
        var session = Snapshot().GetProperty("sessions")[0];
        Assert.Equal("Paused", session.GetProperty("status").GetString());
        Assert.Equal(180 * 60_000, session.GetProperty("remainingMs").GetInt64());
    }

    [Fact]
    public void CashRefundDoesNotIncreaseWalletAndCannotExceedUnusedPart()
    {
        TopUp();var session = Buy();AdvanceSeconds(120 * 60);
        store.ChangeSession(owner, session, "end", new("Отключение света, посетитель ушёл", Key()));
        var request = new RefundRequest(session, 10_000, "Cash", "Компенсация одного часа", Key());
        store.Refund(owner, request); store.Refund(owner, request);
        Assert.Equal(20_000, store.FindAccount(customer)!.BalanceMinor);
        var report = Snapshot().GetProperty("report");
        Assert.Equal(40_000, report.GetProperty("cashNetMinor").GetInt64());
        Assert.Equal(10_000, report.GetProperty("refundsMinor").GetInt64());
        Assert.Equal(30_000, report.GetProperty("purchasesMinor").GetInt64());
        Assert.Throws<RuleException>(() => store.Refund(owner, request with { AmountMinor = 1, OperationId = Key() }));
        Assert.Throws<RuleException>(() => store.ChangeSession(owner, session, "resume", new("Повтор", Key())));
    }

    [Fact]
    public void PackageEditsDoNotChangeExistingPurchase()
    {
        TopUp();Buy();store.SavePackage(owner, new(package, "Новые условия", 60, 9000, false, Key()));
        var session = Snapshot().GetProperty("sessions")[0];
        Assert.Equal(30_000, session.GetProperty("priceMinor").GetInt64());
        Assert.Equal(180 * 60_000, session.GetProperty("totalMs").GetInt64());
        Assert.Empty(Snapshot(customer).GetProperty("packages").EnumerateArray());
    }

    [Fact]
    public void CustomerCannotGrantMoneyTimeOrSeeOwnerReport()
    {
        Assert.Equal(403, Assert.Throws<RuleException>(() => store.TopUp(customer, new(customer,1000,"Cash",Key()))).Status);
        Assert.Throws<RuleException>(() => store.FreeSession(customer,new(customer,computer,60,"test",Key())));
        Assert.Throws<RuleException>(() => store.CreateAccount("employee",Password,"Admin",customer));
        var snapshot = Snapshot(customer);
        Assert.Equal(JsonValueKind.Null,snapshot.GetProperty("report").ValueKind);
        Assert.Empty(snapshot.GetProperty("customers").EnumerateArray());
        Assert.Empty(snapshot.GetProperty("audit").EnumerateArray());
    }

    [Fact]
    public void AdminCanGrantFreeTimeWithAuditButNoFakeIncome()
    {
        var admin = store.CreateAccount("employee",Password,"Admin",owner).Id;
        store.FreeSession(admin,new(customer,computer,60,"Компенсация неисправной мыши",Key()));
        var snapshot=Snapshot();
        Assert.Equal(0,snapshot.GetProperty("report").GetProperty("purchasesMinor").GetInt64());
        Assert.Contains(snapshot.GetProperty("audit").EnumerateArray(),x=>x.GetProperty("action").GetString()=="FreeTimeGranted");
    }

    [Fact]
    public void BackupCanBeRestoredAndPreservesPreviousDatabase()
    {
        TopUp();Buy();var backup=store.Backup(owner);Database.Verify(backup);
        TopUp(1000);store.Dispose();
        Database.Restore(directory,backup);store=OpenStore();
        Assert.Equal(20_000,store.FindAccount(customer)!.BalanceMinor);
        Assert.Equal("Paused",Snapshot().GetProperty("sessions")[0].GetProperty("status").GetString());
        Assert.Single(Directory.GetFiles(directory,"before-restore-*.db"));
        Database.Verify(Directory.GetFiles(directory,"before-restore-*.db")[0]);
    }

    [Fact]
    public void CorruptOrInconsistentBackupIsRejected()
    {
        TopUp();var backup=store.Backup(owner);
        using(var db=Database.Open(backup)) Database.Execute(db,null,"UPDATE Accounts SET BalanceMinor=0 WHERE Id=$0",customer);
        Assert.Throws<InvalidDataException>(()=>Database.Verify(backup));
        Assert.Throws<InvalidDataException>(()=>Database.Restore(directory,backup));
        Assert.Equal(50_000,store.FindAccount(customer)!.BalanceMinor);
    }

    [Fact]
    public void ActiveSessionsRemainVisibleAfterMoreThanHundredClosedSessions()
    {
        TopUp();var active=Buy();
        var other=store.CreateAccount("visitor2",Password).Id;
        var otherPc=store.AddComputer(owner,new("ПК-02",Key())).GetProperty("id").GetString()!;
        for(var i=0;i<101;i++)
        {
            time.Add(TimeSpan.FromMilliseconds(1));
            var id=store.FreeSession(owner,new(other,otherPc,1,"Проверка истории",Key())).GetProperty("sessionId").GetString()!;
            store.ChangeSession(owner,id,"end",new("Проверка",Key()));
        }
        var sessions=Snapshot().GetProperty("sessions").EnumerateArray().ToArray();
        Assert.Equal(101,sessions.Length);
        Assert.Contains(sessions,s=>s.GetProperty("id").GetString()==active);
    }
}
