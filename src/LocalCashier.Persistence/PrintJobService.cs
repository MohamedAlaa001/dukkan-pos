using LocalCashier.Domain;

namespace LocalCashier.Persistence;

public enum PrintStatus { Queued, Submitting, Submitted, Failed, Uncertain }

public sealed record PrintJobRow(long Id, long SaleId, string SaleNumber, string Kind, PrintStatus Status, bool IsCopy, string? Printer, string? Error, string CreatedAt);

/// <summary>
/// Print lifecycle, kept apart from the sale's financial status. "Submitted" means handed to the Windows spooler — it is not proof of paper.
/// A job found mid-submission after a restart becomes "uncertain": the owner reprints it (marked as a copy) instead of the app guessing.
/// </summary>
public sealed class PrintJobService(PosDatabase db, IClock clock)
{
    static PrintStatus Parse(string s) => Enum.Parse<PrintStatus>(s, ignoreCase: true);

    public long Enqueue(long saleId, string kind, bool isCopy)
    {
        using var c = db.Open();
        return Db.InTransaction(c, () =>
        {
            if (Sql.Scalar<long>(c, "SELECT COUNT(*) FROM sales WHERE id=@s", ("@s", saleId)) == 0)
                throw new PosException(PosError.NotFound, "Sale not found.");
            var now = Common.Stamp(clock);
            return Sql.Insert(c, "INSERT INTO print_jobs(sale_id,status,is_copy,created_at,kind,updated_at) VALUES(@s,'queued',@cp,@t,@k,@t)",
                ("@s", saleId), ("@cp", isCopy ? 1 : 0), ("@t", now), ("@k", kind));
        });
    }

    public void BeginSubmit(long jobId, string? printer) => Update(jobId, "submitting", printer, null);

    public void MarkSubmitted(long jobId) => Update(jobId, "submitted", null, null, clearError: true);

    public void MarkFailed(long jobId, string error) => Update(jobId, "failed", null, error);

    void Update(long jobId, string status, string? printer, string? error, bool clearError = false)
    {
        using var c = db.Open();
        Db.InTransaction(c, () =>
        {
            var n = Sql.Exec(c,
                "UPDATE print_jobs SET status=@s, printer=COALESCE(@p,printer), error=CASE WHEN @ce=1 THEN NULL ELSE COALESCE(@e,error) END, updated_at=@t WHERE id=@i",
                ("@s", status), ("@p", printer), ("@e", error), ("@ce", clearError ? 1 : 0), ("@t", Common.Stamp(clock)), ("@i", jobId));
            if (n == 0) throw new PosException(PosError.NotFound, "Print job not found.");
            return 0;
        });
    }

    /// <summary>Run at startup: anything left "submitting" cannot be confirmed, so it is flagged uncertain.</summary>
    public int RecoverUncertain()
    {
        using var c = db.Open();
        return Db.InTransaction(c, () => Sql.Exec(c, "UPDATE print_jobs SET status='uncertain', updated_at=@t WHERE status='submitting'", ("@t", Common.Stamp(clock))));
    }

    /// <summary>Jobs that still need attention (not yet submitted, failed, or uncertain), oldest first.</summary>
    public IReadOnlyList<PrintJobRow> Attention()
    {
        using var c = db.Open();
        return Query(c, "WHERE j.status IN ('queued','failed','uncertain')");
    }

    public IReadOnlyList<PrintJobRow> ForSale(long saleId)
    {
        using var c = db.Open();
        return Query(c, "WHERE j.sale_id=" + saleId.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    static List<PrintJobRow> Query(Microsoft.Data.Sqlite.SqliteConnection c, string where) =>
        Sql.Query(c,
            $"SELECT j.id,j.sale_id,s.number,j.kind,j.status,j.is_copy,j.printer,j.error,j.created_at FROM print_jobs j JOIN sales s ON s.id=j.sale_id {where} ORDER BY j.id",
            x => new PrintJobRow(x.GetInt64(0), x.GetInt64(1), x.GetString(2), x.GetString(3), Parse(x.GetString(4)), x.GetInt64(5) == 1,
                x.IsDBNull(6) ? null : x.GetString(6), x.IsDBNull(7) ? null : x.GetString(7), x.GetString(8)));

    public PrintJobRow? Get(long jobId)
    {
        using var c = db.Open();
        return Query(c, "WHERE j.id=" + jobId.ToString(System.Globalization.CultureInfo.InvariantCulture)).FirstOrDefault();
    }
}
