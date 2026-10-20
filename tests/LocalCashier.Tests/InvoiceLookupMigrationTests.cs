using LocalCashier.Application;
using LocalCashier.Domain;
using LocalCashier.Persistence;

namespace LocalCashier.Tests;

public class InvoiceLookupMigrationTests
{
    [Fact]
    public void Upgrading_the_credit_release_uses_indexed_invoice_lookups_and_preserves_existing_records()
    {
        using var folder = new TempFolder();
        var path = folder.Combine("pos.db");
        var old = new PosDatabase(path, 7, null);
        var clock = new SystemClock();
        var owner = new UserService(old, clock).Create("owner", "Owner", Role.Owner, "owner-test-1");
        var product = new CatalogService(old, clock).AddProduct(owner, "W1", "LOAD-1", "Original product", null, 10m);

        var upgraded = new PosDatabase(path);
        Assert.Equal(product, new CatalogService(upgraded, clock).FindByBarcode("LOAD-1")!.Id);
        Assert.True(new UserService(upgraded, clock).AnyUsers());

        using var connection = upgraded.Open();
        foreach (var lookup in new[]
        {
            "SELECT SUM(qty_milli) FROM sale_lines WHERE sale_id=1",
            "SELECT COUNT(*) FROM payments WHERE sale_id=1",
            "SELECT SUM(total_cents) FROM returns WHERE sale_id=1",
            "SELECT SUM(amount_cents) FROM collection_allocations WHERE sale_id=1"
        })
        {
            using var command = connection.CreateCommand();
            command.CommandText = "EXPLAIN QUERY PLAN " + lookup;
            using var reader = command.ExecuteReader();
            var plan = new List<string>();
            while (reader.Read()) plan.Add(reader.GetString(3));
            Assert.True(plan.Any(p => p.StartsWith("SEARCH ", StringComparison.Ordinal)),
                "Invoice detail lookup must seek matching records, not scan full history: " + string.Join("; ", plan));
        }
        Assert.NotEmpty(Directory.GetFiles(upgraded.PreMigrationDirectory, "pos-v7-*.db"));
    }

    [Fact]
    public void An_interrupted_invoice_index_upgrade_leaves_the_credit_release_usable()
    {
        using var folder = new TempFolder();
        var path = folder.Combine("pos.db");
        var old = new PosDatabase(path, 7, null);
        new UserService(old, new SystemClock()).Create("owner", "Owner", Role.Owner, "owner-test-1");
        Assert.Throws<IOException>(() => new PosDatabase(path, 8, step => { if (step == 8) throw new IOException("interrupted upgrade"); }));
        var reopened = new PosDatabase(path, 7, null);
        Assert.True(new UserService(reopened, new SystemClock()).AnyUsers());
        using var connection = reopened.Open();
        using var query = connection.CreateCommand();
        query.CommandText = "PRAGMA user_version";
        Assert.Equal(7L, query.ExecuteScalar());
    }
}
