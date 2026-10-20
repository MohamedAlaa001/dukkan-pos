using LocalCashier.Domain;
using Microsoft.Data.Sqlite;

namespace LocalCashier.Persistence;

/// <summary>Owns the SQLite file: durable pragmas, versioned schema, connection factory.</summary>
public sealed class PosDatabase
{
    public const int SchemaVersion = 16;
    readonly string _connectionString;

    public string Path { get; }

    public int TargetVersion { get; }

    public PosDatabase(string path) : this(path, SchemaVersion, null) { }

    /// <param name="targetVersion">Highest schema this process supports (tests lower it to emulate an older build).</param>
    /// <param name="afterMigrationStep">Test hook called after each migration step, inside the migration transaction.</param>
    public PosDatabase(string path, int targetVersion, Action<int>? afterMigrationStep)
    {
        Path = path;
        TargetVersion = targetVersion;
        _connectionString = new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString();
        using var c = Open();
        Migrate(c, afterMigrationStep);
    }

    /// <summary>Directory where verified pre-migration snapshots are kept.</summary>
    public string PreMigrationDirectory => System.IO.Path.Combine(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(Path))!, "premigration");

    public SqliteConnection Open()
    {
        var c = new SqliteConnection(_connectionString);
        c.Open();
        Sql.Exec(c, "PRAGMA foreign_keys=ON");
        Sql.Exec(c, "PRAGMA journal_mode=WAL");
        Sql.Exec(c, "PRAGMA synchronous=FULL");
        Sql.Exec(c, "PRAGMA busy_timeout=5000");
        return c;
    }

    void Migrate(SqliteConnection c, Action<int>? hook)
    {
        var current = Sql.Scalar<long>(c, "PRAGMA user_version");
        if (current > TargetVersion)
            throw new PosException(PosError.BackupInvalid,
                $"Database schema {current} is newer than this application supports ({TargetVersion}). Install a newer version.");
        if (current == TargetVersion) return;

        // An existing business database is snapshotted and verified before any migration touches it.
        if (current > 0) CreateVerifiedSnapshot(c, current);

        // All steps run in one transaction: a failure leaves the original schema untouched.
        Db.InTransaction(c, () =>
        {
            if (current < 1 && TargetVersion >= 1) { Sql.Exec(c, SchemaV1); hook?.Invoke(1); }
            if (current < 2 && TargetVersion >= 2) { Sql.Exec(c, SchemaV2); hook?.Invoke(2); }
            if (current < 3 && TargetVersion >= 3) { Sql.Exec(c, SchemaV3); hook?.Invoke(3); }
            if (current < 4 && TargetVersion >= 4) { Sql.Exec(c, SchemaV4); hook?.Invoke(4); }
            if (current < 5 && TargetVersion >= 5) { Sql.Exec(c, SchemaV5); hook?.Invoke(5); }
            if (current < 6 && TargetVersion >= 6) { Sql.Exec(c, SchemaV6); hook?.Invoke(6); }
            if (current < 7 && TargetVersion >= 7) { Sql.Exec(c, SchemaV7); hook?.Invoke(7); }
            if (current < 8 && TargetVersion >= 8) { Sql.Exec(c, SchemaV8); hook?.Invoke(8); }
            if (current < 9 && TargetVersion >= 9) { Sql.Exec(c, SchemaV9); hook?.Invoke(9); }
            if (current < 10 && TargetVersion >= 10) { Sql.Exec(c, SchemaV10); hook?.Invoke(10); }
            if (current < 11 && TargetVersion >= 11) { Sql.Exec(c, SchemaV11); hook?.Invoke(11); }
            if (current < 12 && TargetVersion >= 12) { Sql.Exec(c, SchemaV12); hook?.Invoke(12); }
            if (current < 13 && TargetVersion >= 13) { Sql.Exec(c, SchemaV13); hook?.Invoke(13); }
            if (current < 14 && TargetVersion >= 14) { Sql.Exec(c, SchemaV14); hook?.Invoke(14); }
            if (current < 15 && TargetVersion >= 15) { Sql.Exec(c, SchemaV15); hook?.Invoke(15); }
            if (current < 16 && TargetVersion >= 16) { Sql.Exec(c, SchemaV16); hook?.Invoke(16); }
            Sql.Exec(c, $"PRAGMA user_version={TargetVersion}");
            return 0;
        });
    }

    void CreateVerifiedSnapshot(SqliteConnection live, long fromVersion)
    {
        Directory.CreateDirectory(PreMigrationDirectory);
        var file = System.IO.Path.Combine(PreMigrationDirectory, $"pos-v{fromVersion}-{DateTime.Now:yyyyMMdd-HHmmss}.db");
        using (var dest = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = file, Pooling = false }.ToString()))
        {
            dest.Open();
            live.BackupDatabase(dest);
            if (Sql.Scalar<string>(dest, "PRAGMA integrity_check") != "ok")
                throw new PosException(PosError.BackupInvalid, "The pre-migration backup failed its integrity check; migration was not attempted.");
        }
    }

    const string SchemaV16 = """
        CREATE INDEX stock_report_balance ON stock_movements(product_id,qty_milli);
        CREATE INDEX sale_lines_report_product ON sale_lines(product_id,sale_id);
        CREATE INDEX return_lines_report_prefix ON return_lines(sale_line_id,id,qty_milli);
        CREATE INDEX applied_promotions_report_line ON applied_promotions(sale_line_id,amount_cents);
        CREATE INDEX sale_lines_report_amounts ON sale_lines(sale_id,product_id,tax_rate_bp,qty_milli,taxable_cents,cogs_cents,line_discount_cents,order_discount_cents,tax_cents);
        """;

    const string SchemaV15 = """
        CREATE TABLE promotions(id INTEGER PRIMARY KEY,name_en TEXT NOT NULL,name_ar TEXT NOT NULL,enabled INTEGER NOT NULL,priority INTEGER NOT NULL);
        CREATE TABLE promotion_rules(promotion_id INTEGER PRIMARY KEY REFERENCES promotions(id),definition_json TEXT NOT NULL);
        CREATE TABLE promotion_operations(operation_id TEXT PRIMARY KEY,kind TEXT NOT NULL,target_id INTEGER NOT NULL);
        CREATE TABLE applied_promotions(id INTEGER PRIMARY KEY,sale_id INTEGER NOT NULL REFERENCES sales(id),sale_line_id INTEGER NOT NULL REFERENCES sale_lines(id),
          promotion_id INTEGER NOT NULL REFERENCES promotions(id),amount_cents INTEGER NOT NULL CHECK(amount_cents>0),name_en TEXT NOT NULL,name_ar TEXT NOT NULL);
        CREATE INDEX applied_promotions_sale ON applied_promotions(sale_id,sale_line_id);
        CREATE TABLE return_promotion_allocations(return_line_id INTEGER NOT NULL REFERENCES return_lines(id),applied_id INTEGER NOT NULL REFERENCES applied_promotions(id),
          amount_cents INTEGER NOT NULL CHECK(amount_cents>=0),PRIMARY KEY(return_line_id,applied_id));
        ALTER TABLE customers ADD COLUMN loyalty_group TEXT NOT NULL DEFAULT '';
        ALTER TABLE sales ADD COLUMN coupon TEXT;
        CREATE TABLE loyalty_config(id INTEGER PRIMARY KEY CHECK(id=1),enabled INTEGER NOT NULL,earn_rate_milli INTEGER NOT NULL,
          value_cents INTEGER NOT NULL,minimum_points INTEGER NOT NULL,expiry_days INTEGER NOT NULL);
        INSERT INTO loyalty_config VALUES(1,0,0,1,1,0);
        CREATE TABLE loyalty_ledger(id INTEGER PRIMARY KEY,operation_key TEXT NOT NULL UNIQUE,customer_id INTEGER NOT NULL REFERENCES customers(id),
          kind TEXT NOT NULL,points INTEGER NOT NULL CHECK(points<>0),source_type TEXT NOT NULL,source_id INTEGER NOT NULL,
          sale_id INTEGER REFERENCES sales(id),return_id INTEGER REFERENCES returns(id),actor_id INTEGER REFERENCES users(id),created_at TEXT NOT NULL,expires_on TEXT);
        CREATE INDEX loyalty_customer ON loyalty_ledger(customer_id,id);
        CREATE INDEX loyalty_sale ON loyalty_ledger(sale_id,kind,source_id);
        CREATE TABLE loyalty_allocations(debit_id INTEGER NOT NULL REFERENCES loyalty_ledger(id),credit_id INTEGER NOT NULL REFERENCES loyalty_ledger(id),
          points INTEGER NOT NULL CHECK(points>0),PRIMARY KEY(debit_id,credit_id));
        CREATE INDEX loyalty_credit_allocations ON loyalty_allocations(credit_id);
        CREATE TABLE loyalty_sale_lines(sale_line_id INTEGER PRIMARY KEY REFERENCES sale_lines(id),earned_points INTEGER NOT NULL);
        CREATE TABLE loyalty_return_points(return_line_id INTEGER PRIMARY KEY REFERENCES return_lines(id),earned_reversed INTEGER NOT NULL);
        CREATE TABLE loyalty_expired_earnings(expire_id INTEGER NOT NULL REFERENCES loyalty_ledger(id),sale_line_id INTEGER NOT NULL REFERENCES sale_lines(id),
          start_points INTEGER NOT NULL,end_points INTEGER NOT NULL CHECK(end_points>start_points),PRIMARY KEY(expire_id,sale_line_id,start_points));
        CREATE INDEX loyalty_expired_line ON loyalty_expired_earnings(sale_line_id,start_points);
        CREATE TABLE loyalty_credit_origins(credit_id INTEGER PRIMARY KEY REFERENCES loyalty_ledger(id),parent_credit_id INTEGER NOT NULL REFERENCES loyalty_ledger(id));
        CREATE TABLE loyalty_compensations(credit_id INTEGER PRIMARY KEY REFERENCES loyalty_ledger(id),debit_id INTEGER NOT NULL REFERENCES loyalty_ledger(id),
          original_credit_id INTEGER REFERENCES loyalty_ledger(id),points INTEGER NOT NULL CHECK(points>0));
        CREATE INDEX loyalty_compensated_debits ON loyalty_compensations(debit_id,original_credit_id);
        ALTER TABLE sales ADD COLUMN earned_points INTEGER NOT NULL DEFAULT 0;
        ALTER TABLE sales ADD COLUMN redeemed_points INTEGER NOT NULL DEFAULT 0;
        ALTER TABLE sales ADD COLUMN redemption_cents INTEGER NOT NULL DEFAULT 0;
        ALTER TABLE sales ADD COLUMN loyalty_balance_after INTEGER NOT NULL DEFAULT 0;
        ALTER TABLE sales ADD COLUMN loyalty_active INTEGER NOT NULL DEFAULT 0;
        INSERT INTO payment_methods VALUES(10,'loyalty','Loyalty points','نقاط الولاء','credit',0,100);
        """;

    const string SchemaV14 = """
        CREATE TABLE payment_methods(id INTEGER PRIMARY KEY,code TEXT NOT NULL UNIQUE,name_en TEXT NOT NULL,name_ar TEXT NOT NULL,
          kind TEXT NOT NULL CHECK(kind IN('cash','card','transfer','credit')),enabled INTEGER NOT NULL,sort_order INTEGER NOT NULL);
        INSERT INTO payment_methods VALUES(1,'cash','Cash','نقدي','cash',1,10),(2,'card','Visa','فيزا','card',1,20),
          (3,'transfer','Instapay','إنستاباي','transfer',1,30),(4,'credit','Customer credit','آجل على العميل','credit',1,40),
          (5,'vodafone_cash','Vodafone Cash','فودافون كاش','transfer',0,50),(6,'orange_cash','Orange Cash','أورنج كاش','transfer',0,60),
          (7,'etisalat_cash','Etisalat Cash','اتصالات كاش','transfer',0,70),(8,'we_pay','WE Pay','وي باي','transfer',0,80),
          (9,'other_electronic','Other electronic','دفع إلكتروني آخر','transfer',0,90);
        ALTER TABLE payments ADD COLUMN method_id INTEGER REFERENCES payment_methods(id);
        ALTER TABLE payments ADD COLUMN method_name_en TEXT;
        ALTER TABLE payments ADD COLUMN method_name_ar TEXT;
        ALTER TABLE payments ADD COLUMN reference TEXT;
        ALTER TABLE payments ADD COLUMN terminal_mode TEXT;
        ALTER TABLE payments ADD COLUMN approval_code TEXT;
        ALTER TABLE payments ADD COLUMN last4 TEXT;
        UPDATE payments SET method_id=(SELECT id FROM payment_methods WHERE code=payments.method),
          method_name_en=(SELECT name_en FROM payment_methods WHERE code=payments.method),method_name_ar=(SELECT name_ar FROM payment_methods WHERE code=payments.method),
          terminal_mode=CASE WHEN method='card' THEN 'manual' END;
        CREATE INDEX payments_method_identity ON payments(method_id,sale_id);
        ALTER TABLE collections ADD COLUMN method_id INTEGER REFERENCES payment_methods(id);
        ALTER TABLE collections ADD COLUMN method_name_en TEXT;
        ALTER TABLE collections ADD COLUMN method_name_ar TEXT;
        ALTER TABLE collections ADD COLUMN terminal_mode TEXT;
        ALTER TABLE collections ADD COLUMN approval_code TEXT;
        ALTER TABLE collections ADD COLUMN last4 TEXT;
        UPDATE collections SET method_id=(SELECT id FROM payment_methods WHERE code=collections.method),
          method_name_en=(SELECT name_en FROM payment_methods WHERE code=collections.method),method_name_ar=(SELECT name_ar FROM payment_methods WHERE code=collections.method),
          terminal_mode=CASE WHEN method='card' THEN 'manual' END;
        ALTER TABLE supplier_payments ADD COLUMN method_id INTEGER REFERENCES payment_methods(id);
        ALTER TABLE supplier_payments ADD COLUMN method_name_en TEXT;
        ALTER TABLE supplier_payments ADD COLUMN method_name_ar TEXT;
        ALTER TABLE supplier_payments ADD COLUMN terminal_mode TEXT;
        ALTER TABLE supplier_payments ADD COLUMN approval_code TEXT;
        ALTER TABLE supplier_payments ADD COLUMN last4 TEXT;
        UPDATE supplier_payments SET method_id=(SELECT id FROM payment_methods WHERE code=supplier_payments.method),
          method_name_en=(SELECT name_en FROM payment_methods WHERE code=supplier_payments.method),method_name_ar=(SELECT name_ar FROM payment_methods WHERE code=supplier_payments.method),
          terminal_mode=CASE WHEN method='card' THEN 'manual' END;
        CREATE TABLE refund_payments(id INTEGER PRIMARY KEY,return_id INTEGER NOT NULL REFERENCES returns(id),method_id INTEGER NOT NULL REFERENCES payment_methods(id),
          kind TEXT NOT NULL,method_name_en TEXT NOT NULL,method_name_ar TEXT NOT NULL,amount_cents INTEGER NOT NULL CHECK(amount_cents>0));
        CREATE INDEX refund_payments_return ON refund_payments(return_id);
        INSERT INTO refund_payments(return_id,method_id,kind,method_name_en,method_name_ar,amount_cents)
          SELECT r.id,m.id,m.kind,m.name_en,m.name_ar,r.refund_cents FROM returns r JOIN payment_methods m ON m.code=r.refund_method WHERE r.refund_cents>0;
        """;

    const string SchemaV13 = """
        CREATE TABLE count_cancellations(id INTEGER PRIMARY KEY,operation_id TEXT NOT NULL UNIQUE,session_id INTEGER NOT NULL UNIQUE REFERENCES count_sessions(id),
          reason TEXT NOT NULL,actor_id INTEGER NOT NULL REFERENCES users(id),created_at TEXT NOT NULL);
        CREATE TABLE stock_locations(id INTEGER PRIMARY KEY,name TEXT NOT NULL UNIQUE,active INTEGER NOT NULL DEFAULT 1);
        INSERT INTO stock_locations(id,name) VALUES(1,'Main store');
        CREATE TABLE stock_lots(id INTEGER PRIMARY KEY,product_id INTEGER NOT NULL REFERENCES products(id),batch TEXT NOT NULL,expiry TEXT NOT NULL,
          UNIQUE(product_id,batch,expiry));
        ALTER TABLE stock_movements ADD COLUMN location_id INTEGER NOT NULL DEFAULT 1;
        ALTER TABLE stock_movements ADD COLUMN lot_id INTEGER REFERENCES stock_lots(id);
        CREATE INDEX stock_location_balance ON stock_movements(product_id,location_id,lot_id,id);
        CREATE INDEX stock_lot_balance ON stock_movements(lot_id,location_id);
        CREATE TRIGGER stock_location_valid BEFORE INSERT ON stock_movements
          WHEN NOT EXISTS(SELECT 1 FROM stock_locations WHERE id=NEW.location_id)
          BEGIN SELECT RAISE(ABORT,'Invalid stock location'); END;
        CREATE TRIGGER stock_lot_valid BEFORE INSERT ON stock_movements
          WHEN NEW.lot_id IS NOT NULL AND NOT EXISTS(SELECT 1 FROM stock_lots WHERE id=NEW.lot_id AND product_id=NEW.product_id)
          BEGIN SELECT RAISE(ABORT,'Invalid product lot'); END;
        ALTER TABLE products ADD COLUMN track_expiry INTEGER NOT NULL DEFAULT 0;
        ALTER TABLE products ADD COLUMN reorder_point_milli INTEGER NOT NULL DEFAULT 0;
        ALTER TABLE products ADD COLUMN reorder_target_milli INTEGER NOT NULL DEFAULT 0;
        ALTER TABLE products ADD COLUMN preferred_supplier_id INTEGER REFERENCES suppliers(id);
        ALTER TABLE supplier_invoice_lines ADD COLUMN location_id INTEGER NOT NULL DEFAULT 1;
        ALTER TABLE supplier_invoice_lines ADD COLUMN lot_id INTEGER REFERENCES stock_lots(id);
        ALTER TABLE supplier_return_lines ADD COLUMN location_id INTEGER NOT NULL DEFAULT 1;
        ALTER TABLE supplier_return_lines ADD COLUMN lot_id INTEGER REFERENCES stock_lots(id);
        CREATE TABLE stock_operations(operation_id TEXT PRIMARY KEY,kind TEXT NOT NULL,target_id INTEGER NOT NULL);
        CREATE TABLE sale_line_stock(id INTEGER PRIMARY KEY,sale_line_id INTEGER NOT NULL REFERENCES sale_lines(id),location_id INTEGER NOT NULL REFERENCES stock_locations(id),
          lot_id INTEGER REFERENCES stock_lots(id),qty_milli INTEGER NOT NULL CHECK(qty_milli>0));
        CREATE INDEX sale_line_stock_line ON sale_line_stock(sale_line_id);
        CREATE TABLE return_stock_allocations(id INTEGER PRIMARY KEY,return_line_id INTEGER NOT NULL REFERENCES return_lines(id),
          sale_allocation_id INTEGER NOT NULL REFERENCES sale_line_stock(id),qty_milli INTEGER NOT NULL CHECK(qty_milli>0));
        CREATE INDEX return_stock_original ON return_stock_allocations(sale_allocation_id);
        CREATE TABLE count_sessions(id INTEGER PRIMARY KEY,operation_id TEXT NOT NULL UNIQUE,location_id INTEGER NOT NULL REFERENCES stock_locations(id),
          state TEXT NOT NULL DEFAULT 'open',movement_cursor INTEGER NOT NULL,actor_id INTEGER NOT NULL REFERENCES users(id),created_at TEXT NOT NULL);
        CREATE TABLE count_lines(id INTEGER PRIMARY KEY,session_id INTEGER NOT NULL REFERENCES count_sessions(id),product_id INTEGER NOT NULL REFERENCES products(id),
          lot_id INTEGER REFERENCES stock_lots(id),expected_milli INTEGER NOT NULL,cost_x10000 INTEGER NOT NULL);
        CREATE INDEX count_lines_session ON count_lines(session_id);
        CREATE TABLE count_entries(id INTEGER PRIMARY KEY,operation_id TEXT NOT NULL UNIQUE,line_id INTEGER NOT NULL REFERENCES count_lines(id),
          counted_milli INTEGER NOT NULL CHECK(counted_milli>=0),movement_cursor INTEGER NOT NULL,actor_id INTEGER NOT NULL REFERENCES users(id),created_at TEXT NOT NULL);
        CREATE INDEX count_entries_line ON count_entries(line_id,id);
        CREATE TABLE count_approvals(id INTEGER PRIMARY KEY,operation_id TEXT NOT NULL UNIQUE,session_id INTEGER NOT NULL UNIQUE REFERENCES count_sessions(id),
          reason TEXT NOT NULL,actor_id INTEGER NOT NULL REFERENCES users(id),created_at TEXT NOT NULL);
        """;

    const string SchemaV12 = """
        CREATE TABLE purchase_orders(
          id INTEGER PRIMARY KEY, operation_id TEXT NOT NULL UNIQUE, number TEXT NOT NULL UNIQUE,
          supplier_id INTEGER NOT NULL REFERENCES suppliers(id), state TEXT NOT NULL CHECK(state IN('draft','open','cancelled')),
          expected_date TEXT, notes TEXT, actor_id INTEGER NOT NULL REFERENCES users(id), created_at TEXT NOT NULL);
        CREATE TABLE purchase_order_lines(
          id INTEGER PRIMARY KEY, order_id INTEGER NOT NULL REFERENCES purchase_orders(id), product_id INTEGER NOT NULL REFERENCES products(id),
          description TEXT NOT NULL, qty_milli INTEGER NOT NULL CHECK(qty_milli>0), unit_cost_x10000 INTEGER NOT NULL CHECK(unit_cost_x10000>=0));
        CREATE INDEX purchase_order_lines_order ON purchase_order_lines(order_id);
        CREATE TABLE purchase_order_receipts(
          id INTEGER PRIMARY KEY, order_line_id INTEGER NOT NULL REFERENCES purchase_order_lines(id),
          invoice_line_id INTEGER NOT NULL UNIQUE REFERENCES supplier_invoice_lines(id), qty_milli INTEGER NOT NULL CHECK(qty_milli>0));
        CREATE INDEX purchase_order_receipts_line ON purchase_order_receipts(order_line_id);
        CREATE TABLE purchase_order_actions(
          id INTEGER PRIMARY KEY, operation_id TEXT NOT NULL UNIQUE, order_id INTEGER NOT NULL REFERENCES purchase_orders(id),
          state TEXT NOT NULL, reason TEXT, actor_id INTEGER NOT NULL REFERENCES users(id), created_at TEXT NOT NULL);
        CREATE TABLE supplier_returns(
          id INTEGER PRIMARY KEY, operation_id TEXT NOT NULL UNIQUE, number TEXT NOT NULL UNIQUE, supplier_id INTEGER NOT NULL REFERENCES suppliers(id),
          invoice_id INTEGER REFERENCES supplier_invoices(id), return_date TEXT NOT NULL, reason TEXT NOT NULL, notes TEXT,
          total_cents INTEGER NOT NULL CHECK(total_cents>=0), actor_id INTEGER NOT NULL REFERENCES users(id), created_at TEXT NOT NULL);
        CREATE TABLE supplier_return_lines(
          id INTEGER PRIMARY KEY, return_id INTEGER NOT NULL REFERENCES supplier_returns(id), product_id INTEGER NOT NULL REFERENCES products(id),
          invoice_line_id INTEGER REFERENCES supplier_invoice_lines(id), description TEXT NOT NULL,
          qty_milli INTEGER NOT NULL CHECK(qty_milli>0), unit_cost_x10000 INTEGER NOT NULL CHECK(unit_cost_x10000>=0),
          stock_cost_x10000 INTEGER NOT NULL CHECK(stock_cost_x10000>=0), line_cents INTEGER NOT NULL CHECK(line_cents>=0));
        CREATE INDEX supplier_return_lines_original ON supplier_return_lines(invoice_line_id);
        CREATE TABLE supplier_return_reversals(
          id INTEGER PRIMARY KEY, operation_id TEXT NOT NULL UNIQUE, return_id INTEGER NOT NULL UNIQUE REFERENCES supplier_returns(id),
          reason TEXT NOT NULL, actor_id INTEGER NOT NULL REFERENCES users(id), created_at TEXT NOT NULL);
        CREATE TABLE supplier_credit_allocations(
          id INTEGER PRIMARY KEY, return_id INTEGER NOT NULL REFERENCES supplier_returns(id), invoice_id INTEGER NOT NULL REFERENCES supplier_invoices(id),
          amount_cents INTEGER NOT NULL CHECK(amount_cents>0), created_at TEXT NOT NULL);
        CREATE INDEX supplier_credit_invoice ON supplier_credit_allocations(invoice_id);
        CREATE INDEX supplier_credit_return ON supplier_credit_allocations(return_id);
        CREATE TABLE supplier_credit_allocation_reversals(
          id INTEGER PRIMARY KEY, allocation_id INTEGER NOT NULL UNIQUE REFERENCES supplier_credit_allocations(id),
          reason TEXT NOT NULL, actor_id INTEGER NOT NULL REFERENCES users(id), created_at TEXT NOT NULL);
        CREATE INDEX supplier_returns_supplier ON supplier_returns(supplier_id);
        """;

    // v11: each category carries a bundled picture name; older built-in categories get a matching one.
    const string SchemaV11 = """
        ALTER TABLE categories ADD COLUMN icon TEXT;
        UPDATE categories SET icon=CASE lower(name)
          WHEN 'tshirts' THEN 'shirt' WHEN 'pants' THEN 'jeans' WHEN 'jackets' THEN 'coat' WHEN 'hoodies' THEN 'coat' WHEN 'shirts' THEN 'shirt'
          WHEN 'others' THEN 'gift' END;
        """;

    // v10: general-purpose catalog. Categories become shop-managed rows (existing category keys are kept as names), and a style
    // can be sold by weight/length (unit + fractional quantities). Variant option names live in settings (option1_name/option2_name).
    const string SchemaV10 = """
        CREATE TABLE categories(
          id INTEGER PRIMARY KEY, name TEXT NOT NULL UNIQUE, name_alt TEXT, sort INTEGER NOT NULL DEFAULT 0, active INTEGER NOT NULL DEFAULT 1);
        INSERT INTO categories(name,sort) SELECT DISTINCT category,0 FROM styles WHERE category IS NOT NULL AND category<>'';
        ALTER TABLE styles ADD COLUMN unit TEXT NOT NULL DEFAULT 'pc';
        ALTER TABLE styles ADD COLUMN allow_decimal INTEGER NOT NULL DEFAULT 0;
        """;

    // v9: controlled CSV imports. One row per applied import; the unique migration reference makes retries harmless.
    const string SchemaV9 = """
        CREATE TABLE import_batches(
          id INTEGER PRIMARY KEY, reference TEXT NOT NULL UNIQUE, kind TEXT NOT NULL, file_hash TEXT NOT NULL,
          row_count INTEGER NOT NULL, actor_id INTEGER NOT NULL REFERENCES users(id), created_at TEXT NOT NULL);
        """;

    // v8: invoice history and credit queries seek by invoice instead of scanning the full transaction history.
    const string SchemaV8 = """
        CREATE INDEX sale_lines_sale ON sale_lines(sale_id);
        CREATE INDEX payments_sale ON payments(sale_id);
        CREATE INDEX returns_sale ON returns(sale_id);
        CREATE INDEX collection_allocations_sale ON collection_allocations(sale_id);
        """;

    // v4: a style ("T-Shirt") groups sellable variants. Each row in products stays the sellable unit (own SKU, barcode,
    // stock, cost), now with size/color. Existing products become one-variant styles, so all earlier behaviour is unchanged.
    const string SchemaV4 = """
        CREATE TABLE styles(
          id INTEGER PRIMARY KEY, name_en TEXT NOT NULL, name_alt TEXT, category TEXT, brand TEXT, description TEXT,
          kind TEXT NOT NULL DEFAULT 'TShirt', base_price_cents INTEGER NOT NULL, cost_cents INTEGER NOT NULL DEFAULT 0,
          tax_rate_bp INTEGER NOT NULL DEFAULT 0, low_stock_milli INTEGER NOT NULL DEFAULT 0,
          track_inventory INTEGER NOT NULL DEFAULT 1, allow_discount INTEGER NOT NULL DEFAULT 1, active INTEGER NOT NULL DEFAULT 1);
        ALTER TABLE products ADD COLUMN style_id INTEGER REFERENCES styles(id);
        ALTER TABLE products ADD COLUMN size TEXT;
        ALTER TABLE products ADD COLUMN color_name_en TEXT;
        ALTER TABLE products ADD COLUMN color_name_alt TEXT;
        ALTER TABLE products ADD COLUMN color_hex TEXT;
        ALTER TABLE sale_lines ADD COLUMN size TEXT;
        ALTER TABLE sale_lines ADD COLUMN color TEXT;
        INSERT INTO styles(id,name_en,name_alt,category,base_price_cents,cost_cents,tax_rate_bp,low_stock_milli,active)
          SELECT id,name,name_alt,category,price_cents,avg_cost_x10000/100,tax_rate_bp,low_stock_milli,active FROM products;
        UPDATE products SET style_id=id;
        CREATE INDEX products_style ON products(style_id);
        """;

    // v5: print job lifecycle (queued -> submitting -> submitted | failed | uncertain), separate from the financial status of the sale.
    const string SchemaV5 = """
        ALTER TABLE print_jobs ADD COLUMN kind TEXT NOT NULL DEFAULT 'receipt80';
        ALTER TABLE print_jobs ADD COLUMN printer TEXT;
        ALTER TABLE print_jobs ADD COLUMN error TEXT;
        ALTER TABLE print_jobs ADD COLUMN updated_at TEXT;
        CREATE INDEX print_jobs_status ON print_jobs(status);
        """;

    // v6: sign-in lockout, offline owner recovery codes, held (draft) carts.
    const string SchemaV6 = """
        ALTER TABLE customers ADD COLUMN email TEXT;
        ALTER TABLE customers ADD COLUMN created_at TEXT;
        ALTER TABLE users ADD COLUMN failed_attempts INTEGER NOT NULL DEFAULT 0;
        ALTER TABLE users ADD COLUMN locked_until TEXT;
        CREATE TABLE recovery_codes(
          id INTEGER PRIMARY KEY, user_id INTEGER NOT NULL REFERENCES users(id), code_hash TEXT NOT NULL, created_at TEXT NOT NULL, used_at TEXT);
        CREATE TABLE held_carts(
          id INTEGER PRIMARY KEY, name TEXT NOT NULL, cashier_id INTEGER NOT NULL REFERENCES users(id),
          customer_id INTEGER REFERENCES customers(id), payload TEXT NOT NULL, created_at TEXT NOT NULL);
        """;

    // v7: product image, supplier accounts (ledger-based), customer installment plans, collection reversals, customer opening balances.
    // Money is integer cents. Posted rows are never edited; corrections are linked reversal rows.
    const string SchemaV7 = """
        ALTER TABLE styles ADD COLUMN image BLOB;
        ALTER TABLE sales ADD COLUMN arrangement TEXT NOT NULL DEFAULT 'none';
        ALTER TABLE sales ADD COLUMN due_date TEXT;
        ALTER TABLE collections ADD COLUMN pay_date TEXT;
        ALTER TABLE collections ADD COLUMN reference TEXT;
        ALTER TABLE collections ADD COLUMN notes TEXT;

        CREATE TABLE customer_opening_balances(
          id INTEGER PRIMARY KEY, operation_id TEXT NOT NULL UNIQUE, customer_id INTEGER NOT NULL REFERENCES customers(id),
          amount_cents INTEGER NOT NULL CHECK(amount_cents>0), entry_date TEXT NOT NULL, note TEXT,
          actor_id INTEGER NOT NULL REFERENCES users(id), created_at TEXT NOT NULL);
        CREATE TABLE collection_opening_allocations(
          id INTEGER PRIMARY KEY, collection_id INTEGER NOT NULL REFERENCES collections(id),
          opening_id INTEGER NOT NULL REFERENCES customer_opening_balances(id), amount_cents INTEGER NOT NULL);
        CREATE TABLE collection_reversals(
          id INTEGER PRIMARY KEY, collection_id INTEGER NOT NULL UNIQUE REFERENCES collections(id), reason TEXT NOT NULL,
          actor_id INTEGER NOT NULL REFERENCES users(id), shift_id INTEGER REFERENCES shifts(id), created_at TEXT NOT NULL);

        CREATE TABLE installment_plans(
          id INTEGER PRIMARY KEY, sale_id INTEGER NOT NULL REFERENCES sales(id), version INTEGER NOT NULL,
          kind TEXT NOT NULL, reason TEXT, actor_id INTEGER, created_at TEXT NOT NULL, superseded_at TEXT);
        CREATE TABLE sale_installments(
          id INTEGER PRIMARY KEY, plan_id INTEGER NOT NULL REFERENCES installment_plans(id), sale_id INTEGER NOT NULL REFERENCES sales(id),
          seq INTEGER NOT NULL, due_date TEXT NOT NULL, amount_cents INTEGER NOT NULL, carried_paid_cents INTEGER NOT NULL DEFAULT 0);
        CREATE INDEX sale_installments_sale ON sale_installments(sale_id);
        CREATE TABLE installment_allocations(
          id INTEGER PRIMARY KEY, collection_id INTEGER NOT NULL REFERENCES collections(id),
          installment_id INTEGER NOT NULL REFERENCES sale_installments(id), amount_cents INTEGER NOT NULL);

        CREATE TABLE suppliers(
          id INTEGER PRIMARY KEY, name TEXT NOT NULL, phone TEXT, address TEXT, notes TEXT,
          active INTEGER NOT NULL DEFAULT 1, created_at TEXT NOT NULL);
        CREATE TABLE supplier_invoices(
          id INTEGER PRIMARY KEY, number TEXT NOT NULL UNIQUE, operation_id TEXT NOT NULL UNIQUE,
          supplier_id INTEGER NOT NULL REFERENCES suppliers(id), kind TEXT NOT NULL CHECK(kind IN('purchase','opening')),
          supplier_ref TEXT, invoice_date TEXT NOT NULL, due_date TEXT, total_cents INTEGER NOT NULL CHECK(total_cents>0),
          notes TEXT, actor_id INTEGER NOT NULL REFERENCES users(id), created_at TEXT NOT NULL);
        CREATE TABLE supplier_invoice_lines(
          id INTEGER PRIMARY KEY, invoice_id INTEGER NOT NULL REFERENCES supplier_invoices(id),
          product_id INTEGER NOT NULL REFERENCES products(id), description TEXT NOT NULL, qty_milli INTEGER NOT NULL,
          unit_cost_x10000 INTEGER NOT NULL, line_cents INTEGER NOT NULL, receipt_id INTEGER REFERENCES stock_receipts(id));
        CREATE TABLE supplier_payments(
          id INTEGER PRIMARY KEY, number TEXT NOT NULL UNIQUE, operation_id TEXT NOT NULL UNIQUE,
          supplier_id INTEGER NOT NULL REFERENCES suppliers(id), pay_date TEXT NOT NULL, amount_cents INTEGER NOT NULL CHECK(amount_cents>0),
          method TEXT NOT NULL, reference TEXT, notes TEXT, shift_id INTEGER REFERENCES shifts(id),
          actor_id INTEGER NOT NULL REFERENCES users(id), created_at TEXT NOT NULL);
        CREATE TABLE supplier_payment_allocations(
          id INTEGER PRIMARY KEY, payment_id INTEGER NOT NULL REFERENCES supplier_payments(id),
          invoice_id INTEGER NOT NULL REFERENCES supplier_invoices(id), amount_cents INTEGER NOT NULL);
        CREATE TABLE supplier_reversals(
          id INTEGER PRIMARY KEY, target_type TEXT NOT NULL CHECK(target_type IN('payment','invoice')), target_id INTEGER NOT NULL,
          reason TEXT NOT NULL, shift_id INTEGER REFERENCES shifts(id), actor_id INTEGER NOT NULL REFERENCES users(id), created_at TEXT NOT NULL,
          UNIQUE(target_type,target_id));
        CREATE TABLE supplier_ledger(
          id INTEGER PRIMARY KEY, supplier_id INTEGER NOT NULL REFERENCES suppliers(id), entry_type TEXT NOT NULL,
          amount_cents INTEGER NOT NULL, source_type TEXT NOT NULL, source_id INTEGER NOT NULL, entry_date TEXT NOT NULL, created_at TEXT NOT NULL);
        CREATE INDEX supplier_ledger_supplier ON supplier_ledger(supplier_id);
        """;

    const string SchemaV3 = """
        CREATE TABLE backup_records(
          id INTEGER PRIMARY KEY, kind TEXT NOT NULL CHECK(kind IN('local','external')), path TEXT,
          created_at TEXT NOT NULL, ok INTEGER NOT NULL, message TEXT);
        """;

    const string SchemaV2 = """
        CREATE TABLE returns(
          id INTEGER PRIMARY KEY, number TEXT NOT NULL UNIQUE, operation_id TEXT NOT NULL UNIQUE,
          sale_id INTEGER NOT NULL REFERENCES sales(id), shift_id INTEGER NOT NULL REFERENCES shifts(id),
          actor_id INTEGER NOT NULL REFERENCES users(id), approver_id INTEGER NOT NULL REFERENCES users(id),
          created_at TEXT NOT NULL, reason TEXT NOT NULL, taxable_cents INTEGER NOT NULL, tax_cents INTEGER NOT NULL,
          total_cents INTEGER NOT NULL, debt_reduction_cents INTEGER NOT NULL, refund_cents INTEGER NOT NULL,
          refund_method TEXT);
        CREATE TABLE return_lines(
          id INTEGER PRIMARY KEY, return_id INTEGER NOT NULL REFERENCES returns(id),
          sale_line_id INTEGER NOT NULL REFERENCES sale_lines(id), qty_milli INTEGER NOT NULL,
          taxable_cents INTEGER NOT NULL, tax_cents INTEGER NOT NULL, total_cents INTEGER NOT NULL,
          cogs_reversed_cents INTEGER NOT NULL, resalable INTEGER NOT NULL);
        CREATE TABLE collections(
          id INTEGER PRIMARY KEY, number TEXT NOT NULL UNIQUE, operation_id TEXT NOT NULL UNIQUE,
          customer_id INTEGER NOT NULL REFERENCES customers(id), shift_id INTEGER NOT NULL REFERENCES shifts(id),
          actor_id INTEGER NOT NULL REFERENCES users(id), method TEXT NOT NULL, amount_cents INTEGER NOT NULL,
          created_at TEXT NOT NULL);
        CREATE TABLE collection_allocations(
          id INTEGER PRIMARY KEY, collection_id INTEGER NOT NULL REFERENCES collections(id),
          sale_id INTEGER NOT NULL REFERENCES sales(id), amount_cents INTEGER NOT NULL);
        CREATE TABLE expenses(
          id INTEGER PRIMARY KEY, expense_date TEXT NOT NULL, category TEXT NOT NULL, amount_cents INTEGER NOT NULL,
          description TEXT, source TEXT NOT NULL CHECK(source IN('register','external')),
          shift_id INTEGER REFERENCES shifts(id), actor_id INTEGER NOT NULL REFERENCES users(id), created_at TEXT NOT NULL);
        CREATE TABLE stock_adjustments(
          id INTEGER PRIMARY KEY, reason TEXT NOT NULL, actor_id INTEGER NOT NULL REFERENCES users(id), created_at TEXT NOT NULL);
        CREATE TABLE stock_losses(
          id INTEGER PRIMARY KEY, product_id INTEGER NOT NULL REFERENCES products(id), qty_milli INTEGER NOT NULL,
          cost_cents INTEGER NOT NULL, source_type TEXT NOT NULL, source_id INTEGER, reason TEXT, created_at TEXT NOT NULL);
        """;

    // Money: integer cents. Quantity: integer thousandths. Unit cost: integer 1/10000.
    const string SchemaV1 = """
        CREATE TABLE settings(key TEXT PRIMARY KEY, value TEXT NOT NULL);
        CREATE TABLE users(
          id INTEGER PRIMARY KEY, username TEXT NOT NULL UNIQUE, display_name TEXT NOT NULL,
          role TEXT NOT NULL CHECK(role IN('owner','cashier')), password_hash TEXT NOT NULL,
          active INTEGER NOT NULL DEFAULT 1);
        CREATE TABLE products(
          id INTEGER PRIMARY KEY, sku TEXT NOT NULL UNIQUE, barcode TEXT UNIQUE, name TEXT NOT NULL, name_alt TEXT,
          category TEXT, unit TEXT NOT NULL DEFAULT 'pc', price_cents INTEGER NOT NULL CHECK(price_cents>=0),
          allow_decimal INTEGER NOT NULL DEFAULT 0, low_stock_milli INTEGER NOT NULL DEFAULT 0,
          tax_rate_bp INTEGER NOT NULL DEFAULT 0, avg_cost_x10000 INTEGER NOT NULL DEFAULT 0,
          active INTEGER NOT NULL DEFAULT 1);
        CREATE TABLE customers(
          id INTEGER PRIMARY KEY, name TEXT NOT NULL, phone TEXT, notes TEXT,
          credit_enabled INTEGER NOT NULL DEFAULT 0, credit_limit_cents INTEGER NOT NULL DEFAULT 0,
          active INTEGER NOT NULL DEFAULT 1);
        CREATE TABLE customer_ledger(
          id INTEGER PRIMARY KEY, customer_id INTEGER NOT NULL REFERENCES customers(id),
          entry_type TEXT NOT NULL, amount_cents INTEGER NOT NULL, source_type TEXT, source_id INTEGER,
          created_at TEXT NOT NULL);
        CREATE TABLE shifts(
          id INTEGER PRIMARY KEY, cashier_id INTEGER NOT NULL REFERENCES users(id), business_date TEXT NOT NULL,
          opened_at TEXT NOT NULL, opening_float_cents INTEGER NOT NULL, status TEXT NOT NULL CHECK(status IN('open','closed')),
          closed_at TEXT, closing_count_cents INTEGER, expected_cash_cents INTEGER, variance_cents INTEGER, variance_reason TEXT);
        CREATE UNIQUE INDEX one_open_shift ON shifts(status) WHERE status='open';
        CREATE TABLE cash_movements(
          id INTEGER PRIMARY KEY, shift_id INTEGER NOT NULL REFERENCES shifts(id), kind TEXT NOT NULL,
          amount_cents INTEGER NOT NULL, source_type TEXT, source_id INTEGER, created_at TEXT NOT NULL);
        CREATE TABLE stock_receipts(
          id INTEGER PRIMARY KEY, product_id INTEGER NOT NULL REFERENCES products(id), qty_milli INTEGER NOT NULL,
          unit_cost_x10000 INTEGER NOT NULL, supplier TEXT, reference TEXT, document_date TEXT,
          posted_at TEXT NOT NULL, actor_id INTEGER NOT NULL REFERENCES users(id), shift_id INTEGER REFERENCES shifts(id));
        CREATE TABLE stock_movements(
          id INTEGER PRIMARY KEY, product_id INTEGER NOT NULL REFERENCES products(id), qty_milli INTEGER NOT NULL,
          unit_cost_x10000 INTEGER NOT NULL, source_type TEXT NOT NULL, source_id INTEGER, reason TEXT,
          actor_id INTEGER NOT NULL REFERENCES users(id), created_at TEXT NOT NULL);
        CREATE INDEX stock_movements_product ON stock_movements(product_id);
        CREATE TABLE document_sequences(series TEXT PRIMARY KEY, next_value INTEGER NOT NULL);
        CREATE TABLE sales(
          id INTEGER PRIMARY KEY, number TEXT NOT NULL UNIQUE, operation_id TEXT NOT NULL UNIQUE,
          shift_id INTEGER NOT NULL REFERENCES shifts(id), cashier_id INTEGER NOT NULL REFERENCES users(id),
          approver_id INTEGER REFERENCES users(id), customer_id INTEGER REFERENCES customers(id),
          created_at TEXT NOT NULL, business_date TEXT NOT NULL, tax_inclusive INTEGER NOT NULL,
          gross_cents INTEGER NOT NULL, discount_cents INTEGER NOT NULL, taxable_cents INTEGER NOT NULL,
          tax_cents INTEGER NOT NULL, total_cents INTEGER NOT NULL, paid_cents INTEGER NOT NULL,
          change_cents INTEGER NOT NULL, debt_cents INTEGER NOT NULL, status TEXT NOT NULL DEFAULT 'posted');
        CREATE TABLE sale_lines(
          id INTEGER PRIMARY KEY, sale_id INTEGER NOT NULL REFERENCES sales(id), line_no INTEGER NOT NULL,
          product_id INTEGER NOT NULL REFERENCES products(id), sku TEXT NOT NULL, name TEXT NOT NULL,
          qty_milli INTEGER NOT NULL, unit_price_cents INTEGER NOT NULL, gross_cents INTEGER NOT NULL,
          line_discount_cents INTEGER NOT NULL, order_discount_cents INTEGER NOT NULL, taxable_cents INTEGER NOT NULL,
          tax_cents INTEGER NOT NULL, total_cents INTEGER NOT NULL, tax_rate_bp INTEGER NOT NULL,
          unit_cost_x10000 INTEGER NOT NULL, cogs_cents INTEGER NOT NULL);
        CREATE TABLE payments(
          id INTEGER PRIMARY KEY, sale_id INTEGER NOT NULL REFERENCES sales(id), method TEXT NOT NULL,
          applied_cents INTEGER NOT NULL, tendered_cents INTEGER NOT NULL);
        CREATE TABLE audit_events(
          id INTEGER PRIMARY KEY, ts TEXT NOT NULL, actor_id INTEGER, approver_id INTEGER, action TEXT NOT NULL,
          target TEXT, reason TEXT, details TEXT);
        CREATE TABLE print_jobs(
          id INTEGER PRIMARY KEY, sale_id INTEGER NOT NULL REFERENCES sales(id), status TEXT NOT NULL,
          is_copy INTEGER NOT NULL DEFAULT 0, created_at TEXT NOT NULL);
        """;
}
