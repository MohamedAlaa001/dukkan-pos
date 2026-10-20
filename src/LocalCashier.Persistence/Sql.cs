using Microsoft.Data.Sqlite;
using LocalCashier.Domain;

namespace LocalCashier.Persistence;

internal static class Sql
{
    static SqliteCommand Cmd(SqliteConnection c, string sql, (string, object?)[] p)
    {
        var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (n, v) in p) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
        return cmd;
    }

    public static int Exec(SqliteConnection c, string sql, params (string, object?)[] p)
    {
        using var cmd = Cmd(c, sql, p);
        return cmd.ExecuteNonQuery();
    }

    public static T? Scalar<T>(SqliteConnection c, string sql, params (string, object?)[] p)
    {
        using var cmd = Cmd(c, sql, p);
        var o = cmd.ExecuteScalar();
        if (o is null or DBNull) return default;
        return (T)Convert.ChangeType(o, Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T));
    }

    public static long Insert(SqliteConnection c, string sql, params (string, object?)[] p)
    {
        Exec(c, sql, p);
        return Scalar<long>(c, "SELECT last_insert_rowid()");
    }

    public static List<T> Query<T>(SqliteConnection c, string sql, Func<SqliteDataReader, T> map, params (string, object?)[] p)
    {
        using var cmd = Cmd(c, sql, p);
        using var r = cmd.ExecuteReader();
        var list = new List<T>();
        while (r.Read()) list.Add(map(r));
        return list;
    }
}

internal static class Db
{
    /// <summary>Like <see cref="InTransaction{T}(SqliteConnection, Func{T})"/>, but refuses anyone who is not an active owner.</summary>
    public static T InOwnerTransaction<T>(SqliteConnection c, Func<T> work, long ownerId) =>
        InTransaction(c, () => { Common.RequireOwner(c, ownerId); return work(); });

    /// <summary>Runs <paramref name="work"/> in one BEGIN IMMEDIATE transaction: commit on success, rollback on any exception.</summary>
    public static T InTransaction<T>(SqliteConnection c, Func<T> work)
    {
        Sql.Exec(c, "BEGIN IMMEDIATE");
        try
        {
            var result = work();
            Sql.Exec(c, "COMMIT");
            return result;
        }
        catch (Exception ex)
        {
            try { Sql.Exec(c, "ROLLBACK"); } catch { /* connection already unusable */ }
            if (ex is SqliteException { SqliteErrorCode: 13 })
                throw new PosException(PosError.StorageFull, "Not enough free space to save. Free space on the data drive and try again.", ex);
            throw;
        }
    }
}
