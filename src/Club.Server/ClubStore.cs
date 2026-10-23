using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using static Club.Server.Database;

namespace Club.Server;

public sealed partial class ClubStore : IDisposable
{
    private readonly object gate = new();
    private readonly string path;
    private readonly string directory;
    private readonly FileStream processLock;
    private readonly TimeProvider time;
    private readonly IPasswordHasher<Account> passwords;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    public long Now => time.GetUtcNow().ToUnixTimeMilliseconds();

    public ClubStore(string dataDirectory, TimeProvider clock, IPasswordHasher<Account> hasher)
    {
        directory = dataDirectory;
        Directory.CreateDirectory(directory);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        path = Path.Combine(directory, "club.db");
        processLock = new FileStream(Path.Combine(directory, "instance.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        time = clock;
        passwords = hasher;
        using var db = Open(path);
        Initialize(db);
        using var tx = db.BeginTransaction();
        var interrupted = Rows(db, tx, "SELECT Id AS id FROM Sessions WHERE Status='Running'");
        Execute(db, tx, "UPDATE Sessions SET Status='Paused',Revision=Revision+Managed WHERE Status='Running'");
        foreach (var session in interrupted) Audit(db, tx, null, "RecoveryPause", (string)session["id"]!, "Перезапуск сервера; сохранён последний подтверждённый остаток.");
        tx.Commit();
    }

    public void Dispose() => processLock.Dispose();

    public Account? FindAccount(string value, bool byName = false)
    {
        lock (gate)
        {
            using var db = Open(path);
            return ReadAccount(db, null, value, byName);
        }
    }
    private static Account? ReadAccount(SqliteConnection db, SqliteTransaction? tx, string value, bool byName = false)
    {
        using var cmd = Command(db, tx, "SELECT Id,Username,Role,PasswordHash,BalanceMinor FROM Accounts WHERE " + (byName ? "Username" : "Id") + "=$0", value);
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetInt64(4)) : null;
    }

    public Account CreateAccount(string username, string password, string role = "Customer", string? actor = null)
    {
        username = username.Trim();
        if (!Regex.IsMatch(username, @"\A[a-zA-Z0-9_.-]{3,32}\z")) throw new RuleException("Логин: 3–32 латинские буквы, цифры, точка, дефис или подчёркивание.");
        if (password.Length is < 10 or > 128) throw new RuleException("Пароль должен содержать от 10 до 128 символов.");
        if (role is not ("Customer" or "Admin" or "Owner")) throw new RuleException("Неизвестная роль.");
        var account = new Account(Guid.NewGuid().ToString("N"), username, role, "", 0);
        account = account with { PasswordHash = passwords.HashPassword(account, password) };
        lock (gate)
        {
            using var db = Open(path);
            using var tx = db.BeginTransaction();
            if (role == "Owner" && Number(db, tx, "SELECT COUNT(*) FROM Accounts WHERE Role='Owner'") > 0)
                throw new RuleException("Владелец уже создан. Повторная инициализация запрещена.", 409);
            if (role == "Admin") RequireStaff(db, tx, actor!, true);
            if (ReadAccount(db, tx, username, true) is not null) throw new RuleException("Этот логин уже занят.", 409);
            Execute(db, tx, "INSERT INTO Accounts(Id,Username,Role,PasswordHash) VALUES($0,$1,$2,$3)", account.Id, username, role, account.PasswordHash);
            Audit(db, tx, actor ?? account.Id, "AccountCreated", account.Id, role);
            tx.Commit();
        }
        return account;
    }

    public bool VerifyPassword(Account account, string password) => password.Length <= 128 &&
        passwords.VerifyHashedPassword(account, account.PasswordHash, password) != PasswordVerificationResult.Failed;

    public bool HasOwner()
    {
        lock (gate)
        {
            using var db = Open(path);
            return Number(db, null, "SELECT COUNT(*) FROM Accounts WHERE Role='Owner'") > 0;
        }
    }

    public JsonElement AddComputer(string actor, ComputerRequest request) => Mutate(actor, "Computer", request.OperationId, request, (db, tx) =>
    {
        RequireStaff(db, tx, actor);
        var name = Text(request.Name, 80);
        if (Number(db, tx, "SELECT COUNT(*) FROM Computers WHERE Name=$0", name) > 0) throw new RuleException("Такое место уже существует.", 409);
        var id = Guid.NewGuid().ToString("N");
        Execute(db, tx, "INSERT INTO Computers VALUES($0,$1)", id, name);
        Audit(db, tx, actor, "ComputerCreated", id, name);
        return new { id };
    });

    public JsonElement SavePackage(string actor, PackageRequest request) => Mutate(actor, "Package", request.OperationId, request, (db, tx) =>
    {
        RequireStaff(db, tx, actor);
        var name = Text(request.Name, 80);
        ValidateMinutes(request.Minutes);
        ValidateMoney(request.PriceMinor);
        var id = request.Id ?? Guid.NewGuid().ToString("N");
        var previous = Rows(db, tx, "SELECT Name AS name, Minutes AS minutes, PriceMinor AS priceMinor, Active AS active FROM Packages WHERE Id=$0", id);
        if (request.Id is not null && previous.Count == 0) throw new RuleException("Пакет не найден.", 404);
        Execute(db, tx, """
            INSERT INTO Packages VALUES($0,$1,$2,$3,$4) ON CONFLICT(Id) DO UPDATE SET
              Name=excluded.Name,Minutes=excluded.Minutes,PriceMinor=excluded.PriceMinor,Active=excluded.Active
            """, id, name, request.Minutes, request.PriceMinor, request.Active ? 1 : 0);
        Audit(db, tx, actor, "PackageSaved", id, JsonSerializer.Serialize(new { before = previous.FirstOrDefault(), after = request }, JsonOptions));
        return new { id };
    });

    public JsonElement TopUp(string actor, TopUpRequest request) => Mutate(actor, "TopUp", request.OperationId, request, (db, tx) =>
    {
        RequireStaff(db, tx, actor);
        ValidateMoney(request.AmountMinor);
        ValidateMethod(request.Method);
        var customer = RequireCustomer(db, tx, request.CustomerId);
        if (customer.BalanceMinor + request.AmountMinor > 1_000_000_000) throw new RuleException("Превышен допустимый баланс прототипа.");
        Execute(db, tx, "UPDATE Accounts SET BalanceMinor=BalanceMinor+$1 WHERE Id=$0", customer.Id, request.AmountMinor);
        Ledger(db, tx, actor, customer.Id, "TopUp", request.AmountMinor, request.AmountMinor, request.Method, null, "Пополнение вручную");
        Audit(db, tx, actor, "TopUp", customer.Id, $"{request.AmountMinor} тыйын; {request.Method}");
        return new { balanceMinor = customer.BalanceMinor + request.AmountMinor };
    });

    public JsonElement Purchase(string actor, PurchaseRequest request, string? deviceHeader = null) => Mutate(actor, "Purchase", request.OperationId, request, (db, tx) =>
    {
        if (Number(db, tx, "SELECT COUNT(*) FROM Devices WHERE ComputerId=$0", request.ComputerId) > 0)
            RequireDevice(db, tx, deviceHeader, request.ComputerId);
        var customer = RequireCustomer(db, tx, actor);
        var packages = Rows(db, tx, "SELECT Name AS name, Minutes AS minutes, PriceMinor AS price FROM Packages WHERE Id=$0 AND Active=1", request.PackageId);
        if (packages.Count == 0) throw new RuleException("Пакет недоступен.", 404);
        var package = packages[0];
        var price = (long)package["price"]!;
        if (customer.BalanceMinor < price) throw new RuleException("Недостаточно средств на балансе.", 409);
        var session = Start(db, tx, actor, customer.Id, request.ComputerId, (string)package["name"]!, (long)package["minutes"]!, price);
        Execute(db, tx, "UPDATE Accounts SET BalanceMinor=BalanceMinor-$1 WHERE Id=$0", customer.Id, price);
        Ledger(db, tx, actor, customer.Id, "Purchase", price, -price, null, session, "Покупка пакета");
        return new { sessionId = session, balanceMinor = customer.BalanceMinor - price };
    });

    public JsonElement FreeSession(string actor, FreeSessionRequest request) => Mutate(actor, "FreeSession", request.OperationId, request, (db, tx) =>
    {
        RequireStaff(db, tx, actor);
        RequireCustomer(db, tx, request.CustomerId);
        ValidateMinutes(request.Minutes);
        var reason = Text(request.Reason, 500);
        var session = Start(db, tx, actor, request.CustomerId, request.ComputerId, "Бесплатное время", request.Minutes, 0);
        Audit(db, tx, actor, "FreeTimeGranted", session, reason);
        return new { sessionId = session };
    });

    private string Start(SqliteConnection db, SqliteTransaction tx, string actor, string customer, string computer, string package, long minutes, long price)
    {
        if (Number(db, tx, "SELECT COUNT(*) FROM Computers WHERE Id=$0", computer) == 0) throw new RuleException("Место не найдено.", 404);
        RequireAvailableDevice(db, tx, computer);
        if (Number(db, tx, "SELECT COUNT(*) FROM Sessions WHERE (ComputerId=$0 OR CustomerId=$1) AND (Status IN ('Running','Paused') OR (Managed=1 AND AckRevision<Revision))", computer, customer) > 0)
            throw new RuleException("У посетителя или места уже есть незавершённый сеанс.", 409);
        var id = Guid.NewGuid().ToString("N");
        Execute(db, tx, "INSERT INTO Sessions(Id,CustomerId,ComputerId,PackageName,PriceMinor,TotalMs,RemainingMs,Status,LastTickMs,CreatedMs) VALUES($0,$1,$2,$3,$4,$5,$5,'Running',$6,$6)", id, customer, computer, package, price, minutes * 60_000, Now);
        Execute(db, tx, "UPDATE Sessions SET Managed=1 WHERE Id=$0 AND EXISTS(SELECT 1 FROM Devices WHERE ComputerId=$1)", id, computer);
        Audit(db, tx, actor, "SessionStarted", id, $"{package}; {minutes} мин; {price} тыйын");
        return id;
    }

    public JsonElement ChangeSession(string actor, string id, string action, SessionActionRequest request) => Mutate(actor, "Session:" + action, request.OperationId, new { id, request }, (db, tx) =>
    {
        RequireStaff(db, tx, actor);
        var reason = Text(request.Reason, 500);
        var session = GetSession(db, tx, id);
        if (Number(db, tx, "SELECT COUNT(*) FROM Sessions WHERE Id=$0 AND Managed=1 AND AckRevision<Revision", id) > 0)
            throw new RuleException("Дождитесь подтверждения предыдущей команды игровым ПК.", 409);
        if (action == "resume") RequireAvailableDevice(db, tx, session.ComputerId);
        var next = action switch
        {
            "pause" when session.Status == "Running" => "Paused",
            "resume" when session.Status == "Paused" => "Running",
            "end" when session.Status is "Running" or "Paused" => "Ended",
            _ => throw new RuleException("Действие недоступно для текущего состояния сеанса.", 409)
        };
        Execute(db, tx, "UPDATE Sessions SET Status=$1,LastTickMs=$2,Revision=Revision+Managed WHERE Id=$0", id, next, Now);
        Audit(db, tx, actor, "Session:" + action, id, reason);
        return new { sessionId = id, status = next, remainingMs = session.RemainingMs };
    });

    public JsonElement Refund(string actor, RefundRequest request) => Mutate(actor, "Refund", request.OperationId, request, (db, tx) =>
    {
        RequireStaff(db, tx, actor);
        ValidateMoney(request.AmountMinor);
        ValidateMethod(request.Method);
        var reason = Text(request.Reason, 500);
        var session = GetSession(db, tx, request.SessionId);
        if (session.Status != "Ended") throw new RuleException("Сначала завершите сеанс. Возврат доступен для неиспользованного остатка.", 409);
        if (Number(db, tx, "SELECT COUNT(*) FROM Sessions WHERE Id=$0 AND Managed=1 AND AckRevision<Revision", session.Id) > 0)
            throw new RuleException("Игровой ПК ещё не подтвердил завершение. Возврат пока недоступен.", 409);
        var refunded = Number(db, tx, "SELECT COALESCE(SUM(AmountMinor),0) FROM Ledger WHERE Kind='Refund' AND SessionId=$0", session.Id);
        // Exact proportional cap in integer minor units; round down, never create extra money.
        var limit = session.PriceMinor * session.RemainingMs / session.TotalMs - refunded;
        if (request.AmountMinor > limit) throw new RuleException("Сумма превышает стоимость доступного неиспользованного остатка.", 409);
        Ledger(db, tx, actor, session.CustomerId, "Refund", request.AmountMinor, 0, request.Method, session.Id, reason);
        Audit(db, tx, actor, "ManualRefund", session.Id, $"{request.AmountMinor} тыйын; {request.Method}; {reason}");
        return new { refundedMinor = request.AmountMinor, availableRefundMinor = limit - request.AmountMinor };
    });

    public void Tick()
    {
        lock (gate)
        {
            using var db = Open(path);
            using var tx = db.BeginTransaction();
            Advance(db, tx);
            tx.Commit();
        }
    }

    private void Advance(SqliteConnection db, SqliteTransaction tx)
    {
        var sessions = ReadSessions(db, tx, "WHERE Status='Running' AND Managed=0");
        foreach (var session in sessions)
        {
            var elapsed = Now - session.LastTickMs;
            // A suspended/stalled server or clock discontinuity needs administrator review.
            if (elapsed < 0 || elapsed > 15_000)
            {
                Execute(db, tx, "UPDATE Sessions SET Status='Paused',LastTickMs=$1 WHERE Id=$0", session.Id, Now);
                Audit(db, tx, null, "ClockGapPause", session.Id, "Разрыв работы сервера; требуется ручное возобновление.");
                continue;
            }
            if (elapsed == 0) continue;
            var remaining = Math.Max(0, session.RemainingMs - elapsed);
            Execute(db, tx, "UPDATE Sessions SET RemainingMs=$1,LastTickMs=$2,Status=$3 WHERE Id=$0", session.Id, remaining, Now, remaining == 0 ? "Completed" : "Running");
            if (remaining == 0) Audit(db, tx, null, "TimeExpired", session.Id, "Время истекло. Прототип не управляет Windows.");
        }
    }

    public object Snapshot(string actor)
    {
        lock (gate)
        {
            using var db = Open(path);
            using var tx = db.BeginTransaction();
            Advance(db, tx);
            var account = ReadAccount(db, tx, actor) ?? throw new RuleException("Войдите снова.", 401);
            var staff = account.Role is "Owner" or "Admin";
            var sessions = Rows(db, tx, """
                SELECT s.Id AS id,s.CustomerId AS customerId,a.Username AS username,s.ComputerId AS computerId,c.Name AS computerName,
                  s.PackageName AS packageName,s.PriceMinor AS priceMinor,s.TotalMs AS totalMs,s.RemainingMs AS remainingMs,
                  s.Status AS status,s.CreatedMs AS createdMs,s.Managed AS managed,s.Revision AS revision,s.AckRevision AS ackRevision,
                  CASE WHEN s.Status='Ended' AND (s.Managed=0 OR s.AckRevision=s.Revision) THEN s.PriceMinor*s.RemainingMs/s.TotalMs -
                    COALESCE((SELECT SUM(l.AmountMinor) FROM Ledger l WHERE l.SessionId=s.Id AND l.Kind='Refund'),0)
                    ELSE 0 END AS availableRefundMinor
                FROM Sessions s JOIN Accounts a ON a.Id=s.CustomerId JOIN Computers c ON c.Id=s.ComputerId
                WHERE (s.Status IN ('Running','Paused') OR (s.Managed=1 AND s.AckRevision<s.Revision) OR s.Id IN (
                  SELECT Id FROM Sessions WHERE Status IN ('Ended','Completed') ORDER BY CreatedMs DESC LIMIT 100))
                """ + (staff ? "" : " AND s.CustomerId=$0") + " ORDER BY s.CreatedMs DESC", staff ? [] : [actor]);
            var computers = Rows(db, tx, """
                SELECT c.Id AS id,c.Name AS name,CASE WHEN EXISTS(SELECT 1 FROM Sessions s WHERE s.ComputerId=c.Id
                  AND (s.Status IN ('Running','Paused') OR (s.Managed=1 AND s.AckRevision<s.Revision))) THEN 1 ELSE 0 END AS occupied,
                  CASE WHEN d.ComputerId IS NULL THEN 0 ELSE 1 END AS managed,
                  COALESCE(d.LastSeenMs,0) AS lastSeenMs FROM Computers c LEFT JOIN Devices d ON d.ComputerId=c.Id ORDER BY c.Name
                """);
            var packages = Rows(db, tx, "SELECT Id AS id,Name AS name,Minutes AS minutes,PriceMinor AS priceMinor,Active AS active FROM Packages" + (staff ? "" : " WHERE Active=1") + " ORDER BY Minutes,Name");
            var customers = staff ? Rows(db, tx, "SELECT Id AS id,Username AS username,BalanceMinor AS balanceMinor FROM Accounts WHERE Role='Customer' ORDER BY Username") : [];
            object? report = null;
            List<Dictionary<string, object?>> audit = [];
            if (staff)
            {
                report = new
                {
                    topUpsMinor = Number(db, tx, "SELECT COALESCE(SUM(AmountMinor),0) FROM Ledger WHERE Kind='TopUp'"),
                    purchasesMinor = Number(db, tx, "SELECT COALESCE(SUM(AmountMinor),0) FROM Ledger WHERE Kind='Purchase'"),
                    refundsMinor = Number(db, tx, "SELECT COALESCE(SUM(AmountMinor),0) FROM Ledger WHERE Kind='Refund'"),
                    walletBalancesMinor = Number(db, tx, "SELECT COALESCE(SUM(BalanceMinor),0) FROM Accounts"),
                    cashNetMinor = Number(db, tx, "SELECT COALESCE(SUM(CASE Kind WHEN 'TopUp' THEN AmountMinor WHEN 'Refund' THEN -AmountMinor ELSE 0 END),0) FROM Ledger WHERE Method='Cash'"),
                    transferNetMinor = Number(db, tx, "SELECT COALESCE(SUM(CASE Kind WHEN 'TopUp' THEN AmountMinor WHEN 'Refund' THEN -AmountMinor ELSE 0 END),0) FROM Ledger WHERE Method='Transfer'"),
                    byEmployee = Rows(db, tx, """
                        SELECT a.Username AS username,l.Kind AS kind,l.Method AS method,SUM(l.AmountMinor) AS amountMinor,COUNT(*) AS operations
                        FROM Ledger l JOIN Accounts a ON a.Id=l.ActorId WHERE l.Kind IN ('TopUp','Refund') GROUP BY a.Id,l.Kind,l.Method
                        """)
                };
                audit = Rows(db, tx, """
                    SELECT x.Id AS id,COALESCE(a.Username,'Система') AS actor,x.Action AS action,x.TargetId AS targetId,x.Details AS details,x.CreatedMs AS createdMs
                    FROM Audit x LEFT JOIN Accounts a ON a.Id=x.ActorId ORDER BY x.Id DESC LIMIT 100
                    """);
            }
            tx.Commit();
            return new { nowMs = Now, user = new { account.Id, account.Username, account.Role, account.BalanceMinor }, computers, packages, sessions, customers, report, audit, prototype = true };
        }
    }

    public string Backup(string? actor)
    {
        lock (gate)
        {
            using var db = Open(path);
            using (var tx = db.BeginTransaction())
            {
                if (actor is not null) RequireStaff(db, tx, actor);
                Audit(db, tx, actor, "BackupRequested", "database", actor is null ? "Автоматическая локальная копия" : "Выгрузка копии; факт переноса на флешку программа не подтверждает");
                tx.Commit();
            }
            var backups = Path.Combine(directory, "backups");
            Directory.CreateDirectory(backups);
            var destination = Path.Combine(backups, $"club-{time.GetUtcNow():yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.db");
            using (var target = Open(destination)) db.BackupDatabase(target);
            Verify(destination);
            return destination;
        }
    }

    private JsonElement Mutate(string actor, string kind, string commandId, object body, Func<SqliteConnection, SqliteTransaction, object> action)
    {
        if (!Guid.TryParse(commandId, out _)) throw new RuleException("Требуется уникальный идентификатор операции.");
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(body, JsonOptions))));
        lock (gate)
        {
            using var db = Open(path);
            using var tx = db.BeginTransaction();
            var previous = Rows(db, tx, "SELECT ActorId AS actor,Kind AS kind,Fingerprint AS fingerprint,ResultJson AS result FROM Commands WHERE Id=$0", commandId);
            if (previous.Count > 0)
            {
                var row = previous[0];
                if (!Equals(row["actor"], actor) || !Equals(row["kind"], kind) || !Equals(row["fingerprint"], fingerprint))
                    throw new RuleException("Идентификатор уже использован для другой операции.", 409);
                return JsonSerializer.Deserialize<JsonElement>((string)row["result"]!);
            }
            Advance(db, tx);
            var result = action(db, tx);
            var json = JsonSerializer.Serialize(result, JsonOptions);
            Execute(db, tx, "INSERT INTO Commands VALUES($0,$1,$2,$3,$4,$5)", commandId, actor, kind, fingerprint, json, Now);
            tx.Commit();
            return JsonSerializer.Deserialize<JsonElement>(json);
        }
    }

    private void Audit(SqliteConnection db, SqliteTransaction tx, string? actor, string action, string target, string details) =>
        Execute(db, tx, "INSERT INTO Audit(ActorId,Action,TargetId,Details,CreatedMs) VALUES($0,$1,$2,$3,$4)", actor, action, target, details, Now);
    private void Ledger(SqliteConnection db, SqliteTransaction tx, string actor, string customer, string kind, long amount, long delta, string? method, string? session, string reason) =>
        Execute(db, tx, "INSERT INTO Ledger(CustomerId,ActorId,Kind,AmountMinor,WalletDelta,Method,SessionId,Reason,CreatedMs) VALUES($0,$1,$2,$3,$4,$5,$6,$7,$8)", customer, actor, kind, amount, delta, method, session, reason, Now);

    private static void RequireStaff(SqliteConnection db, SqliteTransaction tx, string actor, bool ownerOnly = false)
    {
        var user = ReadAccount(db, tx, actor);
        if (user is null || (ownerOnly ? user.Role != "Owner" : user.Role is not ("Owner" or "Admin"))) throw new RuleException("Недостаточно прав.", 403);
    }
    private static Account RequireCustomer(SqliteConnection db, SqliteTransaction tx, string id)
    {
        var user = ReadAccount(db, tx, id);
        return user is { Role: "Customer" } ? user : throw new RuleException("Аккаунт посетителя не найден.", 404);
    }
    private static string Text(string? value, int max)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Trim().Length > max) throw new RuleException($"Введите текст длиной от 1 до {max} символов.");
        return value.Trim();
    }
    private static void ValidateMoney(long amount)
    {
        if (amount is <= 0 or > 100_000_000) throw new RuleException("Сумма должна быть от 0,01 до 1 000 000 сомов.");
    }
    private static void ValidateMinutes(int minutes)
    {
        if (minutes is < 1 or > 1440) throw new RuleException("Длительность: от 1 минуты до 24 часов.");
    }
    private static void ValidateMethod(string method)
    {
        if (method is not ("Cash" or "Transfer")) throw new RuleException("Выберите наличные или перевод.");
    }
    private static SessionRow GetSession(SqliteConnection db, SqliteTransaction tx, string id) =>
        ReadSessions(db, tx, "WHERE Id=$0", id).FirstOrDefault() ?? throw new RuleException("Сеанс не найден.", 404);
    private static List<SessionRow> ReadSessions(SqliteConnection db, SqliteTransaction tx, string where, params object?[] args)
    {
        using var cmd = Command(db, tx, "SELECT Id,CustomerId,ComputerId,PackageName,PriceMinor,TotalMs,RemainingMs,Status,LastTickMs,CreatedMs FROM Sessions " + where, args);
        using var reader = cmd.ExecuteReader();
        List<SessionRow> result = [];
        while (reader.Read()) result.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetInt64(4), reader.GetInt64(5), reader.GetInt64(6), reader.GetString(7), reader.GetInt64(8), reader.GetInt64(9)));
        return result;
    }
    private static List<Dictionary<string, object?>> Rows(SqliteConnection db, SqliteTransaction? tx, string sql, params object?[] args)
    {
        using var cmd = Command(db, tx, sql, args);
        using var reader = cmd.ExecuteReader();
        List<Dictionary<string, object?>> result = [];
        while (reader.Read())
        {
            Dictionary<string, object?> row = [];
            for (var i = 0; i < reader.FieldCount; i++) row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            result.Add(row);
        }
        return result;
    }
}
