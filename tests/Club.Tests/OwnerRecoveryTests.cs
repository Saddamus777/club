using Club.Server;
using Microsoft.AspNetCore.Identity;
using Xunit;

namespace Club.Tests;

public sealed class OwnerRecoveryTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "club-owner-recovery-" + Guid.NewGuid().ToString("N"));
    private readonly ManualTime time = new();
    private const string Original = "original-owner-password";
    private const string Changed = " changed-owner-password ";
    private ClubStore Open() => new(directory, time, new PasswordHasher<Account>());
    public void Dispose() { if (Directory.Exists(directory)) Directory.Delete(directory, true); }

    [Fact]
    public void RefusesRunningServerAndMissingDatabaseWithoutCreatingInstallation()
    {
        Assert.Throws<FileNotFoundException>(() => OwnerRecovery.Reset(directory, "owner", Changed, Changed, "Утрата пароля"));
        Assert.False(Directory.Exists(directory));
        using var store = Open();
        var owner = store.CreateAccount("owner", Original, "Owner");
        Assert.Throws<IOException>(() => OwnerRecovery.Reset(directory, "owner", Changed, Changed, "Утрата пароля"));
        Assert.Equal(owner.PasswordHash, store.FindAccount(owner.Id)!.PasswordHash);
        Assert.False(Directory.Exists(Path.Combine(directory, "backups")));
    }

    [Fact]
    public void RecoveryBacksUpOldCredentialsAndPreservesMoneyAndSessionRows()
    {
        string ownerId, customerId, sessionId;
        Account before;
        using (var store = Open())
        {
            before = store.CreateAccount("owner", Original, "Owner"); ownerId = before.Id;
            customerId = store.CreateAccount("visitor", Original).Id;
            var pc = store.AddComputer(ownerId, new("ПК", Guid.NewGuid().ToString())).GetProperty("id").GetString()!;
            store.TopUp(ownerId, new(customerId, 12345, "Cash", Guid.NewGuid().ToString()));
            sessionId = store.FreeSession(ownerId, new(customerId, pc, 60, "Тест", Guid.NewGuid().ToString())).GetProperty("sessionId").GetString()!;
        }
        var copy = OwnerRecovery.Reset(directory, "owner", Changed, Changed, "Утрата пароля");
        Database.Verify(copy);
        using (var backup = Database.Open(copy, true))
        using (var query = Database.Command(backup, null, "SELECT PasswordHash FROM Accounts WHERE Id=$0", ownerId))
            Assert.Equal(before.PasswordHash, query.ExecuteScalar());
        using (var db = Database.Open(Path.Combine(directory, "club.db"), true))
        {
            Assert.Equal(12345, Database.Number(db, null, "SELECT BalanceMinor FROM Accounts WHERE Id=$0", customerId));
            Assert.Equal(3_600_000, Database.Number(db, null, "SELECT RemainingMs FROM Sessions WHERE Id=$0", sessionId));
            using var state = Database.Command(db, null, "SELECT Status FROM Sessions WHERE Id=$0", sessionId);
            Assert.Equal("Running", state.ExecuteScalar()); // Recovery itself must not rewrite sessions.
            using var audit = Database.Command(db, null, "SELECT Details FROM Audit WHERE Action='OwnerAccessRecovered'");
            var text = Assert.IsType<string>(audit.ExecuteScalar());
            Assert.Contains("Утрата пароля", text);
            Assert.DoesNotContain(Changed, text);
        }
        using var reopened = Open();
        var after = reopened.FindAccount(ownerId)!;
        Assert.Equal(before.AuthRevision + 1, after.AuthRevision);
        Assert.NotEqual(ClubStore.CredentialStamp(before), ClubStore.CredentialStamp(after));
        Assert.False(reopened.VerifyPassword(after, Original));
        Assert.True(reopened.VerifyPassword(after, Changed));
        Assert.Equal(12345, reopened.FindAccount(customerId)!.BalanceMinor);
    }

    [Fact]
    public void InvalidInputWrongRoleAndBackupFailureLeaveCredentialsUnchanged()
    {
        Account owner;
        using (var store = Open())
        {
            owner = store.CreateAccount("owner", Original, "Owner");
            store.CreateAccount("visitor", Original);
        }
        Assert.Throws<RuleException>(() => OwnerRecovery.Reset(directory, "owner", Changed, "mismatch", "Причина"));
        Assert.Throws<RuleException>(() => OwnerRecovery.Reset(directory, "owner", Changed, Changed, " "));
        Assert.Throws<RuleException>(() => OwnerRecovery.Reset(directory, "visitor", Changed, Changed, "Причина"));
        Assert.False(Directory.Exists(Path.Combine(directory, "backups")));
        File.WriteAllText(Path.Combine(directory, "backups"), "block backup directory creation");
        Assert.ThrowsAny<IOException>(() => OwnerRecovery.Reset(directory, "owner", Changed, Changed, "Причина"));
        using var reopened = Open();
        Assert.Equal(owner.PasswordHash, reopened.FindAccount(owner.Id)!.PasswordHash);
        Assert.Equal(owner.AuthRevision, reopened.FindAccount(owner.Id)!.AuthRevision);
    }
}
