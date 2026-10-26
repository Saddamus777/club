using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using static Club.Server.Database;

namespace Club.Server;

// Local, offline operation. Access to the database directory is the trust boundary;
// the CLI additionally requires elevation on Windows. Never exposed through HTTP.
public static class OwnerRecovery
{
    public static string Reset(string directory, string username, string password, string confirmation, string reason)
    {
        if (password.Length is < 10 or > 128 || password != confirmation)
            throw new RuleException("Новый пароль: 10–128 символов; повтор должен совпадать.");
        if (string.IsNullOrWhiteSpace(reason) || reason.Trim().Length > 500)
            throw new RuleException("Укажите причину восстановления длиной до 500 символов.");
        var path = Path.Combine(directory, "club.db");
        if (!File.Exists(path)) throw new FileNotFoundException("Существующая база не найдена. Проверьте папку данных.");
        // The ordinary server holds the same handle for its lifetime. Do not create a new installation.
        using var processLock = new FileStream(Path.Combine(directory, "instance.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Verify(path);
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path, Mode = SqliteOpenMode.ReadWrite, Pooling = false, DefaultTimeout = 15
        }.ToString());
        db.Open();
        Execute(db, null, "PRAGMA foreign_keys=ON; PRAGMA synchronous=FULL;");
        if (Number(db, null, "PRAGMA user_version") != Database.Version)
            throw new RuleException("Сначала обновите базу штатным запуском совместимого сервера.");
        string id;
        using (var query = Command(db, null, "SELECT Id FROM Accounts WHERE Username=$0 AND Role='Owner'", username.Trim()))
            id = query.ExecuteScalar() as string ?? throw new RuleException("Аккаунт владельца с этим логином не найден.");
        var backups = Path.Combine(directory, "backups");
        Directory.CreateDirectory(backups);
        var backup = Path.Combine(backups, "before-owner-recovery-" + Guid.NewGuid().ToString("N") + ".db");
        using (var copy = Open(backup)) db.BackupDatabase(copy);
        Verify(backup); // A failed backup must prevent the credential change.
        var owner = new Account(id, username.Trim(), "Owner", "", 0);
        var hasher = new PasswordHasher<Account>(Microsoft.Extensions.Options.Options.Create(new PasswordHasherOptions { IterationCount = 210_000 }));
        var hash = hasher.HashPassword(owner, password);
        using var tx = db.BeginTransaction();
        Execute(db, tx, "UPDATE Accounts SET PasswordHash=$1,AuthRevision=AuthRevision+1,Disabled=0 WHERE Id=$0 AND Role='Owner'", id, hash);
        Execute(db, tx, "INSERT INTO Audit(ActorId,Action,TargetId,Details,CreatedMs) VALUES(NULL,'OwnerAccessRecovered',$0,$1,$2)",
            id, "Локальное восстановление (Windows/ОС: " + Environment.UserName + "): " + reason.Trim(), DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        tx.Commit();
        return backup;
    }
}
