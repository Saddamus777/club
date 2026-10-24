using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Club.Protocol;
using Microsoft.Data.Sqlite;
using static Club.Server.Database;

namespace Club.Server;

public sealed partial class ClubStore
{
    public JsonElement BindDevice(string actor, DeviceBinding request) => Mutate(actor, "BindDevice", request.OperationId,
        request, (db, tx) =>
        {
            RequireStaff(db, tx, actor);
            if (request.Token is null || request.Token.Length != 64 || !request.Token.All(Uri.IsHexDigit))
                throw new RuleException("Требуется случайный ключ устройства длиной 32 байта.");
            if (Number(db, tx, "SELECT COUNT(*) FROM Computers WHERE Id=$0", request.ComputerId) == 0)
                throw new RuleException("Место не найдено.", 404);
            if (Number(db, tx, "SELECT COUNT(*) FROM Sessions WHERE ComputerId=$0 AND (Status IN ('Running','Paused') OR (Managed=1 AND AckRevision<Revision))", request.ComputerId) > 0)
                throw new RuleException("Сначала завершите и подтвердите все сеансы места.", 409);
            Execute(db, tx, """
                INSERT INTO Devices(ComputerId,TokenHash) VALUES($0,$1) ON CONFLICT(ComputerId)
                DO UPDATE SET TokenHash=excluded.TokenHash,BootId=NULL,Sequence=0,LastSeenMs=0
                """, request.ComputerId, Hash(request.Token));
            Audit(db, tx, actor, "DeviceBound", request.ComputerId, "Ключ устройства установлен/заменён; требуется подключение службы.");
            return new { request.ComputerId };
        });

    private static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
    private static string RequireDevice(SqliteConnection db, SqliteTransaction tx, string? header, string? expected = null)
    {
        var parts = (header ?? "").Split(':');
        if (parts.Length != 2 || parts[1].Length != 64 || (expected is not null && parts[0] != expected))
            throw new RuleException("Устройство не авторизовано.", 401);
        var rows = Rows(db, tx, "SELECT TokenHash AS hash FROM Devices WHERE ComputerId=$0", parts[0]);
        if (rows.Count != 1 || !CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes((string)rows[0]["hash"]!), Encoding.ASCII.GetBytes(Hash(parts[1]))))
            throw new RuleException("Устройство не авторизовано.", 401);
        return parts[0];
    }
    private void RequireAvailableDevice(SqliteConnection db, SqliteTransaction tx, string computer)
    {
        var devices = Rows(db, tx, "SELECT BootId AS boot,LastSeenMs AS seen FROM Devices WHERE ComputerId=$0", computer);
        if (devices.Count == 0) return; // Legacy local accounting remains available.
        var seen = (long)devices[0]["seen"]!;
        if (devices[0]["boot"] is null || Now - seen is < 0 or > 10_000)
            throw new RuleException("Игровой ПК не в сети. Покупка и возобновление недоступны.", 409);
    }

    public SessionGrant DeviceHeartbeat(string? header, DeviceReport report)
    {
        if (!Guid.TryParse(report.BootId, out _) || report.Sequence < 1 || report.RemainingMs < 0 || report.Revision < 0 ||
            report.State is not ("Idle" or "Running" or "Paused" or "Completed" or "Ended" or "Recovery"))
            throw new RuleException("Некорректное подтверждение устройства.");
        lock (gate)
        {
            using var db = Open(path);
            using var tx = db.BeginTransaction();
            var computer = RequireDevice(db, tx, header);
            var device = Rows(db, tx, "SELECT BootId AS boot,Sequence AS seq FROM Devices WHERE ComputerId=$0", computer)[0];
            var reboot = !Equals(device["boot"], report.BootId);
            if (!reboot && report.Sequence <= (long)device["seq"]!)
                throw new RuleException("Устаревшее подтверждение устройства.", 409);
            var sessions = ReadSessions(db, tx, "WHERE ComputerId=$0 AND Managed=1 AND (Status IN ('Running','Paused') OR AckRevision<Revision)", computer);
            if (sessions.Count > 1) throw new InvalidDataException("Несколько неподтверждённых сеансов одного места.");
            SessionGrant grant = new(null, 0, 0, "Idle");
            if (sessions.Count == 1)
            {
                var session = sessions[0];
                var revision = Number(db, tx, "SELECT Revision FROM Sessions WHERE Id=$0", session.Id);
                // A lower persisted remainder is useful even across a reboot or an outstanding pause/end.
                var remaining = session.RemainingMs;
                if (report.SessionId == session.Id && report.Revision <= revision)
                    remaining = Math.Min(remaining, report.RemainingMs);
                var status = session.Status;
                var ack = Number(db, tx, "SELECT AckRevision FROM Sessions WHERE Id=$0", session.Id);
                if (reboot && device["boot"] is not null && status == "Running")
                {
                    status = "Paused";
                    revision++;
                    Audit(db, tx, null, "DeviceRecoveryPause", session.Id, "Перезапуск службы/ПК; требуется подтверждение сотрудника.");
                }
                if (!reboot && report.SessionId == session.Id && report.Revision == revision)
                {
                    if (report.State == status || (status == "Running" && report.State == "Completed" && remaining == 0)) ack = revision;
                    if (status == "Running" && report.State == "Completed" && remaining == 0)
                    {
                        status = "Completed";
                        Audit(db, tx, null, "DeviceTimeExpired", session.Id, "Служба подтвердила окончание времени и выход посетителя.");
                    }
                }
                Execute(db, tx, "UPDATE Sessions SET Status=$1,RemainingMs=$2,Revision=$3,AckRevision=$4,LastTickMs=$5 WHERE Id=$0",
                    session.Id, status, remaining, revision, ack, Now);
                grant = new(session.Id, revision, remaining, status);
            }
            Execute(db, tx, "UPDATE Devices SET BootId=$1,Sequence=$2,LastSeenMs=$3 WHERE ComputerId=$0", computer, report.BootId, report.Sequence, Now);
            tx.Commit();
            return grant;
        }
    }
}
