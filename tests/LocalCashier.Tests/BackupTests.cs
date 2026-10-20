using LocalCashier.Application;
using LocalCashier.Domain;
using LocalCashier.Persistence;
using Microsoft.Data.Sqlite;
using Xunit;

namespace LocalCashier.Tests;

public sealed class TempFolder : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "lct-" + Guid.NewGuid().ToString("N"));

    public TempFolder() => Directory.CreateDirectory(Path);

    public string Combine(string name) => System.IO.Path.Combine(Path, name);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(Path, true); } catch { /* best effort */ }
    }
}

public class BackupTests
{
    const string Secret = "correct horse battery staple";

    static long Scalar(PosDatabase db, string sql)
    {
        using var c = db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    [Fact]
    public void Encrypted_package_round_trips_on_a_different_location_with_a_new_number_series()
    {
        using var f = new PosFixture();
        using var t = new TempFolder();
        f.Sales.Post(f.Sale(2, 20m));
        var pkg = new BackupService(f.Db, f.Clock).CreatePackage(t.Combine("ext"), Secret, external: true);
        Assert.True(File.Exists(pkg));
        Assert.DoesNotContain("SQLite format", File.ReadAllText(pkg, System.Text.Encoding.Latin1));   // not plaintext

        var newLive = t.Combine("new-pc/pos.db");
        var res = RestoreService.Restore(pkg, Secret, newLive, t.Combine("preserved"), f.Clock, f.Owner);
        Assert.Equal(1, res.SalesInBackup);
        Assert.Equal(2, res.NewNumberEpoch);

        var restored = new PosDatabase(newLive);
        Assert.Equal(1, Scalar(restored, "SELECT COUNT(*) FROM sales"));
        Assert.Equal(8, Scalar(restored, "SELECT SUM(qty_milli) FROM stock_movements") / 1000);

        var next = new SaleService(restored, f.Clock).Post(f.Sale(1, 10m));
        Assert.StartsWith("S2-", next.Number);       // never reuses S1-xxxxxx numbers lost after the cutoff
        Assert.Equal(1, Scalar(restored, "SELECT COUNT(*) FROM audit_events WHERE action='backup.restore'"));
    }

    [Fact]
    public void Wrong_secret_corruption_and_truncation_are_rejected_and_live_data_is_untouched()
    {
        using var f = new PosFixture();
        using var t = new TempFolder();
        var pkg = new BackupService(f.Db, f.Clock).CreatePackage(t.Combine("b"), Secret, external: false);
        f.Sales.Post(f.Sale(1, 10m));
        var before = Scalar(f.Db, "SELECT COUNT(*) FROM sales");

        Assert.Equal(PosError.BackupInvalid, Assert.Throws<PosException>(() =>
            RestoreService.Restore(pkg, "wrong secret", f.Db.Path, t.Combine("p"), f.Clock)).Code);

        var bytes = File.ReadAllBytes(pkg);
        var flipped = t.Combine("flipped.lcbk");
        bytes[bytes.Length / 2] ^= 0xFF;
        File.WriteAllBytes(flipped, bytes);
        Assert.Equal(PosError.BackupInvalid, Assert.Throws<PosException>(() =>
            RestoreService.Restore(flipped, Secret, f.Db.Path, t.Combine("p"), f.Clock)).Code);

        var truncated = t.Combine("truncated.lcbk");
        File.WriteAllBytes(truncated, File.ReadAllBytes(pkg)[..^40]);
        Assert.Equal(PosError.BackupInvalid, Assert.Throws<PosException>(() =>
            RestoreService.Restore(truncated, Secret, f.Db.Path, t.Combine("p"), f.Clock)).Code);

        var garbage = t.Combine("garbage.lcbk");
        File.WriteAllText(garbage, "not a backup");
        Assert.Equal(PosError.BackupInvalid, Assert.Throws<PosException>(() =>
            RestoreService.Restore(garbage, Secret, f.Db.Path, t.Combine("p"), f.Clock)).Code);

        Assert.Equal(before, Scalar(f.Db, "SELECT COUNT(*) FROM sales"));
    }

    [Fact]
    public void Restore_over_live_data_preserves_it_first_and_advances_the_epoch()
    {
        using var f = new PosFixture();
        using var t = new TempFolder();
        var pkg = new BackupService(f.Db, f.Clock).CreatePackage(t.Combine("b"), Secret, external: true);
        f.Sales.Post(f.Sale(1, 10m));                     // happens after the backup: will be "lost"
        f.Sales.Post(f.Sale(1, 10m));

        var res = RestoreService.Restore(pkg, Secret, f.Db.Path, t.Combine("preserved"), f.Clock, f.Owner);
        Assert.Equal(0, res.SalesInBackup);
        Assert.True(File.Exists(res.PreservedCopy));

        using (var c = new SqliteConnection($"Data Source={res.PreservedCopy};Pooling=False"))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM sales";
            Assert.Equal(2L, cmd.ExecuteScalar());
        }

        var again = new PosDatabase(f.Db.Path);
        Assert.Equal(0, Scalar(again, "SELECT COUNT(*) FROM sales"));
    }

    [Fact]
    public void Failed_backup_is_reported_never_as_success()
    {
        using var f = new PosFixture();
        using var t = new TempFolder();
        var svc = new BackupService(f.Db, f.Clock);
        svc.CreatePackage(t.Combine("ok"), Secret, external: true);
        var good = svc.Status().LastExternal;
        Assert.NotNull(good);

        var blocker = t.Combine("blocker");
        File.WriteAllText(blocker, "a file where a folder is needed");
        var ex = Assert.Throws<PosException>(() => svc.CreatePackage(Path.Combine(blocker, "sub"), Secret, external: true));
        Assert.Equal(PosError.BackupFailed, ex.Code);

        var status = svc.Status();
        Assert.NotNull(status.ExternalFailure);
        Assert.Equal(good, status.LastExternal);          // the last real success is still what is shown
        Assert.Null(status.LocalFailure);
    }

    [Fact]
    public void Retention_keeps_24_recent_plus_one_per_day_for_30_days()
    {
        using var t = new TempFolder();
        var now = new DateTime(2026, 10, 19, 12, 0, 0);
        for (var h = 0; h < 30; h++) File.WriteAllText(t.Combine($"LocalCashier-{now.AddHours(-h):yyyyMMdd-HHmmss}-local.lcbk"), "x");
        for (var d = 2; d < 45; d++) File.WriteAllText(t.Combine($"LocalCashier-{now.AddDays(-d):yyyyMMdd-HHmmss}-local.lcbk"), "x");
        File.WriteAllText(t.Combine("LocalCashier-20200101-000000-external.lcbk"), "x");    // other kinds are never pruned

        BackupService.ApplyRetention(t.Path);
        var kept = Directory.GetFiles(t.Path, "*-local.lcbk");
        Assert.Equal(24 + 28, kept.Length);   // 24 newest + newest of 28 earlier days (the 30-day window incl. today & yesterday-in-hourly)
        Assert.True(File.Exists(t.Combine("LocalCashier-20200101-000000-external.lcbk")));
    }
}

public class MigrationTests
{
    [Fact]
    public void Failed_migration_leaves_the_original_database_usable_and_a_verified_snapshot_exists()
    {
        using var t = new TempFolder();
        var path = t.Combine("pos.db");
        var v1 = new PosDatabase(path, targetVersion: 1, null);
        new UserService(v1, new SystemClock()).Create("owner", "Owner", Role.Owner, "pw-pw-pw");

        Assert.Throws<IOException>(() => new PosDatabase(path, PosDatabase.SchemaVersion, step => { if (step == 3) throw new IOException("boom"); }));

        // still schema 1, data intact, openable by the old build
        var again = new PosDatabase(path, 1, null);
        Assert.True(new UserService(again, new SystemClock()).AnyUsers());
        using var c = again.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "PRAGMA user_version";
        Assert.Equal(1L, cmd.ExecuteScalar());
        Assert.NotEmpty(Directory.GetFiles(again.PreMigrationDirectory, "pos-v1-*.db"));
    }

    [Fact]
    public void Successful_migration_upgrades_and_a_newer_schema_is_refused()
    {
        using var t = new TempFolder();
        var path = t.Combine("pos.db");
        _ = new PosDatabase(path, targetVersion: 1, null);
        var upgraded = new PosDatabase(path);
        using (var c = upgraded.Open())
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM backup_records";   // a v3 table
            Assert.Equal(0L, cmd.ExecuteScalar());
        }

        var ex = Assert.Throws<PosException>(() => new PosDatabase(path, targetVersion: 1, null));
        Assert.Equal(PosError.BackupInvalid, ex.Code);
    }

    [Fact]
    public void Restoring_an_older_schema_snapshot_migrates_it_on_open()
    {
        using var t = new TempFolder();
        var old = new PosDatabase(t.Combine("old.db"), targetVersion: 1, null);
        var ownerId = new UserService(old, new SystemClock()).Create("owner", "Owner", Role.Owner, "pw-pw-pw");
        var pkg = new BackupService(old, new SystemClock()).CreatePackage(t.Combine("b"), "secret-123", external: false);

        // v1 database has no backup_records table, so the package above would have failed to record; re-check it still produced a file.
        Assert.True(File.Exists(pkg));

        var live = t.Combine("new-pc/pos.db");
        RestoreService.Restore(pkg, "secret-123", live, t.Combine("p"), new SystemClock(), ownerId);
        var db = new PosDatabase(live);
        Assert.True(new UserService(db, new SystemClock()).AnyUsers());
        using var c = db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "PRAGMA user_version";
        Assert.Equal((long)PosDatabase.SchemaVersion, cmd.ExecuteScalar());
    }
}
