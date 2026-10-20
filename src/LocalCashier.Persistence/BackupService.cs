using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using LocalCashier.Domain;
using Microsoft.Data.Sqlite;

namespace LocalCashier.Persistence;

public sealed record BackupManifest(int Format, string AppVersion, int SchemaVersion, string CreatedAt, string DbSha256, string? StoreName);

public sealed record BackupStatus(DateTimeOffset? LastLocal, DateTimeOffset? LastExternal, string? LocalFailure, string? ExternalFailure);

public sealed record RestoreResult(string BackupCreatedAt, long SalesInBackup, string? LastSaleNumber, string? LastSaleAt, string PreservedCopy, long NewNumberEpoch);

static class SqliteFiles
{
    public static string ConnectionString(string path) => new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString();
}

public sealed class BackupService(PosDatabase db, IClock clock)
{
    public const string AppVersion = "0.3.0";

    /// <summary>
    /// Takes a consistent snapshot with SQLite's backup API, packages it with a manifest, encrypts it, and verifies the
    /// finished file (decrypt, hash, integrity check) before reporting success. Failures are recorded, never hidden.
    /// </summary>
    public string CreatePackage(string directory, string secret, bool external)
    {
        var kind = external ? "external" : "local";
        var work = Path.Combine(Path.GetTempPath(), "lcbk-" + Guid.NewGuid().ToString("N"));
        string? part = null;
        string? finalPath = null;
        try
        {
            if (string.IsNullOrEmpty(secret)) throw new PosException(PosError.BackupFailed, "A recovery secret is required.");
            Directory.CreateDirectory(directory);
            Directory.CreateDirectory(work);
            finalPath = Path.Combine(directory, $"LocalCashier-{clock.Now:yyyyMMdd-HHmmss}-{kind}.lcbk");
            part = finalPath + ".part";

            var snapshot = Path.Combine(work, "pos.db");
            int schema;
            string? storeName;
            using (var live = db.Open())
            using (var dest = new SqliteConnection(SqliteFiles.ConnectionString(snapshot)))
            {
                dest.Open();
                live.BackupDatabase(dest);
                if (Sql.Scalar<string>(dest, "PRAGMA integrity_check") != "ok")
                    throw new PosException(PosError.BackupFailed, "The database failed its integrity check; no backup was made.");
                schema = (int)Sql.Scalar<long>(dest, "PRAGMA user_version");
                storeName = Sql.Scalar<string>(dest, "SELECT value FROM settings WHERE key='store_name'");
            }

            var manifest = new BackupManifest(1, AppVersion, schema, clock.Now.ToString("o"), Hash(snapshot), storeName);
            var zipPath = Path.Combine(work, "payload.zip");
            using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
            {
                zip.CreateEntryFromFile(snapshot, "pos.db", CompressionLevel.Optimal);
                var entry = zip.CreateEntry("manifest.json");
                using var w = new StreamWriter(entry.Open());
                w.Write(JsonSerializer.Serialize(manifest));
            }

            BackupCrypto.Encrypt(zipPath, part, secret);
            using (var verifyDir = new TempDir()) { BackupPackage.Open(part, secret, verifyDir.Path); }   // verify before success
            File.Move(part, finalPath, overwrite: true);
            part = null;

            Record(kind, finalPath, true, null);
            if (!external) ApplyRetention(directory);
            return finalPath;
        }
        catch (Exception ex)
        {
            if (part is not null) TryDelete(part);
            var message = ex is PosException ? ex.Message : $"Backup failed: {ex.Message}";
            Record(kind, finalPath, false, message);
            throw ex is PosException ? ex : new PosException(PosError.BackupFailed, message);
        }
        finally
        {
            TryDeleteDir(work);
        }
    }

    public BackupStatus Status()
    {
        using var c = db.Open();
        (DateTimeOffset? Ok, string? Failure) Latest(string kind)
        {
            var ok = Sql.Scalar<string>(c, "SELECT created_at FROM backup_records WHERE kind=@k AND ok=1 ORDER BY id DESC LIMIT 1", ("@k", kind));
            var last = Sql.Query(c, "SELECT ok,message FROM backup_records WHERE kind=@k ORDER BY id DESC LIMIT 1",
                x => (Ok: x.GetInt64(0) == 1, Msg: x.IsDBNull(1) ? null : x.GetString(1)), ("@k", kind)).FirstOrDefault();
            return (ok is null ? null : DateTimeOffset.Parse(ok, CultureInfo.InvariantCulture), last.Msg is not null && !last.Ok ? last.Msg : null);
        }

        var l = Latest("local");
        var e = Latest("external");
        return new BackupStatus(l.Ok, e.Ok, l.Failure, e.Failure);
    }

    void Record(string kind, string? path, bool ok, string? message)
    {
        try
        {
            using var c = db.Open();
            Sql.Exec(c, "INSERT INTO backup_records(kind,path,created_at,ok,message) VALUES(@k,@p,@t,@o,@m)",
                ("@k", kind), ("@p", path), ("@t", clock.Now.ToString("o")), ("@o", ok ? 1 : 0), ("@m", message));
        }
        catch
        {
            // Recording must never mask the backup outcome (the disk may be the very problem).
        }
    }

    /// <summary>
    /// Keeps the newest <paramref name="hourly"/> local packages plus the newest package of each of the last
    /// <paramref name="daily"/> days. Called only after a newer backup has succeeded.
    /// </summary>
    public static void ApplyRetention(string directory, int hourly = 24, int daily = 30)
    {
        var files = Directory.GetFiles(directory, "LocalCashier-*-local.lcbk")
            .Select(f => (File: f, At: ParseStamp(f))).Where(x => x.At is not null)
            .OrderByDescending(x => x.At).ToList();
        var keep = new HashSet<string>(files.Take(hourly).Select(x => x.File));
        foreach (var day in files.GroupBy(x => x.At!.Value.Date).OrderByDescending(g => g.Key).Take(daily))
            keep.Add(day.First().File);
        foreach (var f in files.Where(x => !keep.Contains(x.File))) TryDelete(f.File);
    }

    static DateTime? ParseStamp(string path)
    {
        var parts = Path.GetFileNameWithoutExtension(path).Split('-');   // LocalCashier-yyyyMMdd-HHmmss-kind
        return parts.Length >= 4 && DateTime.TryParseExact($"{parts[1]}{parts[2]}", "yyyyMMddHHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var t) ? t : null;
    }

    internal static string Hash(string path)
    {
        using var s = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(s));
    }

    internal static void TryDelete(string path) { try { File.Delete(path); } catch { /* best effort */ } }

    internal static void TryDeleteDir(string path) { try { Directory.Delete(path, true); } catch { /* best effort */ } }
}

sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "lcbk-" + Guid.NewGuid().ToString("N"));

    public TempDir() => Directory.CreateDirectory(Path);

    public void Dispose() => BackupService.TryDeleteDir(Path);
}

public static class BackupPackage
{
    /// <summary>Decrypts and fully validates a package into <paramref name="stagingDir"/>; returns the manifest and staged database path.</summary>
    public static (BackupManifest Manifest, string DbPath) Open(string packagePath, string secret, string stagingDir)
    {
        Directory.CreateDirectory(stagingDir);
        var zipPath = Path.Combine(stagingDir, "payload.zip");
        BackupCrypto.Decrypt(packagePath, zipPath, secret);

        BackupManifest? manifest;
        try
        {
            using var zip = ZipFile.OpenRead(zipPath);
            var m = zip.GetEntry("manifest.json") ?? throw new PosException(PosError.BackupInvalid, "The package has no manifest.");
            using (var r = new StreamReader(m.Open())) manifest = JsonSerializer.Deserialize<BackupManifest>(r.ReadToEnd());
            var db = zip.GetEntry("pos.db") ?? throw new PosException(PosError.BackupInvalid, "The package has no database.");
            db.ExtractToFile(Path.Combine(stagingDir, "pos.db"), overwrite: true);
        }
        catch (InvalidDataException)
        {
            throw new PosException(PosError.BackupInvalid, "The backup package is damaged.");
        }

        if (manifest is null || manifest.Format != 1) throw new PosException(PosError.BackupInvalid, "Unsupported backup format.");
        if (manifest.SchemaVersion > PosDatabase.SchemaVersion)
            throw new PosException(PosError.BackupInvalid, "This backup was made by a newer version of the application.");

        var dbPath = Path.Combine(stagingDir, "pos.db");
        if (!string.Equals(BackupService.Hash(dbPath), manifest.DbSha256, StringComparison.OrdinalIgnoreCase))
            throw new PosException(PosError.BackupInvalid, "The backup database does not match its manifest.");
        using (var c = new SqliteConnection(SqliteFiles.ConnectionString(dbPath)))
        {
            c.Open();
            if (Sql.Scalar<string>(c, "PRAGMA integrity_check") != "ok")
                throw new PosException(PosError.BackupInvalid, "The backup database is corrupt.");
        }

        return (manifest, dbPath);
    }
}

public static class RestoreService
{
    /// <summary>
    /// Restores a package over the live database file. The caller must have authenticated an owner and closed all
    /// other users of the database. Nothing is replaced until the package is fully validated and the current data
    /// has been preserved; a corrupt or unsupported package leaves the live database untouched.
    /// </summary>
    public static RestoreResult Restore(string packagePath, string secret, string liveDbPath, string preserveDir, IClock clock, long? ownerId = null)
    {
        using var staging = new TempDir();
        var (manifest, stagedDb) = BackupPackage.Open(packagePath, secret, staging.Path);   // throws before anything changes

        Directory.CreateDirectory(preserveDir);
        var preserved = Path.Combine(preserveDir, $"pre-restore-{clock.Now:yyyyMMdd-HHmmss}.db");
        long liveEpoch = 1;
        if (File.Exists(liveDbPath))
        {
            RequireExclusive(liveDbPath);
            try
            {
                using var live = new SqliteConnection(SqliteFiles.ConnectionString(liveDbPath));
                live.Open();
                using var dest = new SqliteConnection(SqliteFiles.ConnectionString(preserved));
                dest.Open();
                live.BackupDatabase(dest);
                if (Sql.Scalar<string>(dest, "PRAGMA integrity_check") != "ok")
                    throw new PosException(PosError.BackupFailed, "The preservation copy of current data failed verification; restore cancelled.");
                liveEpoch = Sql.Scalar<long?>(live, "SELECT value FROM settings WHERE key='number_epoch'") ?? 1;
            }
            catch (SqliteException)
            {
                // Current data unreadable: keep the raw files if possible and continue with the restore.
                preserved += ".raw";
                File.Copy(liveDbPath, preserved, overwrite: true);
            }
        }
        else preserved = "";

        foreach (var ext in new[] { "-wal", "-shm" }) BackupService.TryDelete(liveDbPath + ext);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(liveDbPath))!);
        File.Copy(stagedDb, liveDbPath, overwrite: true);

        // Opening applies any pending migrations (with their own verified snapshot), then a fresh number series starts.
        var restored = new PosDatabase(liveDbPath);
        using var c = restored.Open();
        return Db.InTransaction(c, () =>
        {
            var backupEpoch = Sql.Scalar<long?>(c, "SELECT value FROM settings WHERE key='number_epoch'") ?? 1;
            var epoch = Math.Max(backupEpoch, liveEpoch) + 1;
            Sql.Exec(c, "INSERT INTO settings(key,value) VALUES('number_epoch',@v) ON CONFLICT(key) DO UPDATE SET value=@v", ("@v", epoch.ToString(CultureInfo.InvariantCulture)));
            Sql.Exec(c, "INSERT INTO audit_events(ts,actor_id,action,target,details) VALUES(@t,@a,'backup.restore',@p,@d)",
                ("@t", clock.Now.ToString("o")), ("@a", ownerId), ("@p", Path.GetFileName(packagePath)), ("@d", $"backup_created={manifest.CreatedAt};epoch={epoch}"));
            var sales = Sql.Scalar<long>(c, "SELECT COUNT(*) FROM sales");
            var last = Sql.Query(c, "SELECT number,created_at FROM sales ORDER BY id DESC LIMIT 1", x => (x.GetString(0), x.GetString(1))).FirstOrDefault();
            return new RestoreResult(manifest.CreatedAt, sales, last.Item1, last.Item2, preserved, epoch);
        });
    }

    static void RequireExclusive(string path)
    {
        try
        {
            using var c = new SqliteConnection(SqliteFiles.ConnectionString(path));
            c.Open();
            Sql.Exec(c, "PRAGMA busy_timeout=1000");
            Sql.Exec(c, "BEGIN EXCLUSIVE");
            Sql.Exec(c, "ROLLBACK");
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode is 5 or 6)
        {
            throw new PosException(PosError.BackupFailed, "The database is in use. Close other windows of the application and try again.");
        }
        catch (SqliteException)
        {
            // Unreadable/corrupt database: handled by the caller's preservation fallback.
        }
    }
}
