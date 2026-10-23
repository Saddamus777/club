using Microsoft.Data.Sqlite;

namespace Club.Server;

public static class Database
{
    public const int Version = 1;
    public static SqliteConnection Open(string path, bool readOnly = false)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path, Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWriteCreate,
            Pooling = false, DefaultTimeout = 15
        }.ToString());
        connection.Open();
        if (!readOnly) Execute(connection, null, "PRAGMA foreign_keys=ON; PRAGMA synchronous=FULL;");
        return connection;
    }

    public static SqliteCommand Command(SqliteConnection c, SqliteTransaction? t, string sql, params object?[] args)
    {
        var cmd = c.CreateCommand();
        cmd.Transaction = t;
        cmd.CommandText = sql;
        for (var i = 0; i < args.Length; i++) cmd.Parameters.AddWithValue("$" + i, args[i] ?? DBNull.Value);
        return cmd;
    }
    public static void Execute(SqliteConnection c, SqliteTransaction? t, string sql, params object?[] args)
    {
        using var cmd = Command(c, t, sql, args);
        cmd.ExecuteNonQuery();
    }
    public static long Number(SqliteConnection c, SqliteTransaction? t, string sql, params object?[] args)
    {
        using var cmd = Command(c, t, sql, args);
        return Convert.ToInt64(cmd.ExecuteScalar() ?? 0);
    }
    public static void Initialize(SqliteConnection c)
    {
        var version = Number(c, null, "PRAGMA user_version");
        if (version != 0 && version != Version) throw new InvalidOperationException("Неподдерживаемая версия базы.");
        Execute(c, null, "PRAGMA journal_mode=WAL;");
        if (version == Version) return;
        using var tx = c.BeginTransaction();
        Execute(c, tx, """
            CREATE TABLE Accounts(Id TEXT PRIMARY KEY, Username TEXT NOT NULL COLLATE NOCASE UNIQUE,
              Role TEXT NOT NULL CHECK(Role IN ('Owner','Admin','Customer')), PasswordHash TEXT NOT NULL,
              BalanceMinor INTEGER NOT NULL DEFAULT 0 CHECK(BalanceMinor >= 0));
            CREATE TABLE Computers(Id TEXT PRIMARY KEY, Name TEXT NOT NULL UNIQUE);
            CREATE TABLE Packages(Id TEXT PRIMARY KEY, Name TEXT NOT NULL, Minutes INTEGER NOT NULL CHECK(Minutes > 0),
              PriceMinor INTEGER NOT NULL CHECK(PriceMinor > 0), Active INTEGER NOT NULL CHECK(Active IN (0,1)));
            CREATE TABLE Sessions(Id TEXT PRIMARY KEY, CustomerId TEXT NOT NULL REFERENCES Accounts(Id),
              ComputerId TEXT NOT NULL REFERENCES Computers(Id), PackageName TEXT NOT NULL,
              PriceMinor INTEGER NOT NULL CHECK(PriceMinor >= 0), TotalMs INTEGER NOT NULL CHECK(TotalMs > 0),
              RemainingMs INTEGER NOT NULL CHECK(RemainingMs >= 0 AND RemainingMs <= TotalMs),
              Status TEXT NOT NULL CHECK(Status IN ('Running','Paused','Completed','Ended')),
              LastTickMs INTEGER NOT NULL, CreatedMs INTEGER NOT NULL);
            CREATE UNIQUE INDEX OneActiveComputer ON Sessions(ComputerId) WHERE Status IN ('Running','Paused');
            CREATE UNIQUE INDEX OneActiveCustomer ON Sessions(CustomerId) WHERE Status IN ('Running','Paused');
            CREATE TABLE Ledger(Id INTEGER PRIMARY KEY AUTOINCREMENT, CustomerId TEXT NOT NULL REFERENCES Accounts(Id),
              ActorId TEXT NOT NULL REFERENCES Accounts(Id), Kind TEXT NOT NULL CHECK(Kind IN ('TopUp','Purchase','Refund')),
              AmountMinor INTEGER NOT NULL CHECK(AmountMinor > 0), WalletDelta INTEGER NOT NULL,
              Method TEXT CHECK(Method IN ('Cash','Transfer')), SessionId TEXT REFERENCES Sessions(Id),
              Reason TEXT NOT NULL, CreatedMs INTEGER NOT NULL);
            CREATE TABLE Audit(Id INTEGER PRIMARY KEY AUTOINCREMENT, ActorId TEXT, Action TEXT NOT NULL,
              TargetId TEXT NOT NULL, Details TEXT NOT NULL, CreatedMs INTEGER NOT NULL);
            CREATE TABLE Commands(Id TEXT PRIMARY KEY, ActorId TEXT NOT NULL, Kind TEXT NOT NULL,
              Fingerprint TEXT NOT NULL, ResultJson TEXT NOT NULL, CreatedMs INTEGER NOT NULL);
            PRAGMA user_version=1;
            """);
        tx.Commit();
    }

    public static void Verify(string path)
    {
        using var db = Open(path, true);
        using var cmd = Command(db, null, "PRAGMA integrity_check");
        if (!Equals(cmd.ExecuteScalar(), "ok")) throw new InvalidDataException("Нарушена целостность базы.");
        if (Number(db, null, "PRAGMA user_version") != Version) throw new InvalidDataException("Неподдерживаемый формат копии.");
        using var foreign = Command(db, null, "PRAGMA foreign_key_check");
        using (var reader = foreign.ExecuteReader())
            if (reader.Read()) throw new InvalidDataException("Нарушены связи в базе.");
        if (Number(db, null, """
            SELECT COUNT(*) FROM Accounts a WHERE a.BalanceMinor !=
              COALESCE((SELECT SUM(WalletDelta) FROM Ledger l WHERE l.CustomerId=a.Id),0)
            """) != 0) throw new InvalidDataException("Баланс не сходится с журналом операций.");
        // Require the rest of this application's schema even if an unrelated SQLite file declares version 1.
        foreach (var table in new[] { "Computers", "Packages", "Sessions", "Audit", "Commands" })
            _ = Number(db, null, $"SELECT COUNT(*) FROM {table}");
    }

    public static void Restore(string dataDirectory, string source)
    {
        Verify(source);
        Directory.CreateDirectory(dataDirectory);
        using var processLock = new FileStream(Path.Combine(dataDirectory, "instance.lock"), FileMode.OpenOrCreate,
            FileAccess.ReadWrite, FileShare.None);
        var target = Path.Combine(dataDirectory, "club.db");
        var temporary = Path.Combine(dataDirectory, $"restore-{Guid.NewGuid():N}.db");
        using (var src = Open(source, true))
        using (var dst = Open(temporary)) src.BackupDatabase(dst);
        Verify(temporary);
        if (File.Exists(target))
        {
            var previous = Path.Combine(dataDirectory, $"before-restore-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.db");
            using var current = Open(target);
            using var copy = Open(previous);
            current.BackupDatabase(copy);
        }
        foreach (var suffix in new[] { "-wal", "-shm" }) File.Delete(target + suffix);
        File.Move(temporary, target, true);
        // Old authentication cookies must not remain valid after a financial rollback.
        var keys = Path.Combine(dataDirectory, "keys");
        if (Directory.Exists(keys)) Directory.Move(keys, Path.Combine(dataDirectory, $"keys-before-restore-{Guid.NewGuid():N}"));
    }
}
