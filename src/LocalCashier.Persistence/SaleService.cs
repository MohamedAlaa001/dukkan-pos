using LocalCashier.Application;
using LocalCashier.Domain;
using Microsoft.Data.Sqlite;

namespace LocalCashier.Persistence;

public sealed record ReceiptLine(string Name, decimal Quantity, decimal UnitPrice, decimal Discount, decimal Total, string? Size = null, string? Color = null);

public sealed record ReceiptPayment(string Method, decimal Applied, decimal Tendered, string? MethodCode=null,string? NameEn=null,string? NameAr=null,
    string? Reference=null,string? ApprovalCode=null,string? Last4=null);
public sealed record ReceiptPromotion(string NameEn,string NameAr,decimal Amount);

public sealed record ReceiptModel(
    string Number, string CreatedAt, string Cashier, IReadOnlyList<ReceiptLine> Lines, decimal Discount, decimal Tax,
    decimal Total, IReadOnlyList<ReceiptPayment> Payments, decimal Change, decimal Debt,IReadOnlyList<ReceiptPromotion>? Promotions=null,
    long EarnedPoints=0,long PointsBalance=0,long RedeemedPoints=0,bool HasLoyalty=false,string? Customer=null);

public sealed class SaleService(PosDatabase db, IClock clock)
{
    public sealed record ReturnableLine(long SaleLineId, string Name, decimal Sold, decimal AlreadyReturned, string? Size = null, string? Color = null, long ProductId=0);

    public sealed record ReturnableSale(long SaleId, string Number, IReadOnlyList<ReturnableLine> Lines);

    /// <summary>Looks up a posted sale by receipt number for the returns screen; null when unknown.</summary>
    public ReturnableSale? FindForReturn(string number)
    {
        using var c = db.Open();
        var id = Sql.Scalar<long?>(c, "SELECT id FROM sales WHERE number=@n AND status='posted'", ("@n", number.Trim()));
        if (id is null) return null;
        var lines = Sql.Query(c,
            """
            SELECT l.id,l.name,l.qty_milli,COALESCE((SELECT SUM(qty_milli) FROM return_lines WHERE sale_line_id=l.id),0),l.size,l.color,l.product_id
            FROM sale_lines l WHERE l.sale_id=@s ORDER BY l.line_no
            """,
            x => new ReturnableLine(x.GetInt64(0), x.GetString(1), Money.FromMilli(x.GetInt64(2)), Money.FromMilli(x.GetInt64(3)), x.IsDBNull(4) ? null : x.GetString(4), x.IsDBNull(5) ? null : x.GetString(5),x.GetInt64(6)), ("@s", id.Value));
        return new ReturnableSale(id.Value, number.Trim(), lines);
    }

    /// <summary>Receipt content comes only from the immutable posted snapshots.</summary>
    public ReceiptModel GetReceipt(long saleId)
    {
        using var c = db.Open();
        var h = Sql.Query(c,
            "SELECT s.number,s.created_at,u.display_name,s.discount_cents,s.tax_cents,s.total_cents,s.change_cents,s.debt_cents,s.earned_points,s.loyalty_balance_after,s.redeemed_points,s.loyalty_active FROM sales s JOIN users u ON u.id=s.cashier_id WHERE s.id=@i",
            x => (Number: x.GetString(0), At: x.GetString(1), Cashier: x.GetString(2), Disc: x.GetInt64(3), Tax: x.GetInt64(4), Total: x.GetInt64(5), Change: x.GetInt64(6), Debt: x.GetInt64(7),Earned:x.GetInt64(8),Balance:x.GetInt64(9),Redeemed:x.GetInt64(10),Loyalty:x.GetInt64(11)==1),
            ("@i", saleId));
        if (h.Count == 0) throw new PosException(PosError.NotFound, "Sale not found.");
        var lines = Sql.Query(c,
            "SELECT name,qty_milli,unit_price_cents,line_discount_cents+order_discount_cents,total_cents,size,color FROM sale_lines WHERE sale_id=@i ORDER BY line_no",
            x => new ReceiptLine(x.GetString(0), Money.FromMilli(x.GetInt64(1)), Money.FromCents(x.GetInt64(2)), Money.FromCents(x.GetInt64(3)), Money.FromCents(x.GetInt64(4)), x.IsDBNull(5) ? null : x.GetString(5), x.IsDBNull(6) ? null : x.GetString(6)),
            ("@i", saleId));
        var pays = Sql.Query(c, "SELECT p.method,p.applied_cents,p.tendered_cents,COALESCE(m.code,p.method),p.method_name_en,p.method_name_ar,p.reference,p.approval_code,p.last4 FROM payments p LEFT JOIN payment_methods m ON m.id=p.method_id WHERE p.sale_id=@i ORDER BY p.id",
            x => new ReceiptPayment(x.GetString(0), Money.FromCents(x.GetInt64(1)), Money.FromCents(x.GetInt64(2)),x.GetString(3),x.IsDBNull(4)?null:x.GetString(4),x.IsDBNull(5)?null:x.GetString(5),x.IsDBNull(6)?null:x.GetString(6),x.IsDBNull(7)?null:x.GetString(7),x.IsDBNull(8)?null:x.GetString(8)), ("@i", saleId));
        var s = h[0];
        var offers=Sql.Query(c,"SELECT name_en,name_ar,SUM(amount_cents) FROM applied_promotions WHERE sale_id=@i GROUP BY promotion_id,name_en,name_ar ORDER BY MIN(id)",
            x=>new ReceiptPromotion(x.GetString(0),x.GetString(1),Money.FromCents(x.GetInt64(2))),("@i",saleId));
        return new ReceiptModel(s.Number, s.At, s.Cashier, lines, Money.FromCents(s.Disc), Money.FromCents(s.Tax),
            Money.FromCents(s.Total), pays, Money.FromCents(s.Change), Money.FromCents(s.Debt),offers,s.Earned,s.Balance,s.Redeemed,s.Loyalty,
            Sql.Scalar<string>(c, "SELECT cu.name FROM sales s JOIN customers cu ON cu.id=s.customer_id WHERE s.id=@i", ("@i", saleId)));
    }

    /// <summary>Test hook: invoked at named steps inside the posting transaction to simulate crashes.</summary>
    public Action<string>? FaultInjector { get; set; }

    sealed record ProductRow(long Id, string Sku, string Name, decimal Price, decimal TaxRate, long AvgCost4, bool Active, bool AllowDecimal, string? Size, string? Color);

    public PostSaleResult Post(PostSaleRequest r)
    {
        using var c = db.Open();
        return Db.InTransaction(c, () => PostInTransaction(c, r));
    }

    PostSaleResult PostInTransaction(SqliteConnection c, PostSaleRequest r)
    {
        // Idempotency: the same operation id always yields the same sale.
        var existing = Sql.Query(c, "SELECT id,number,total_cents,change_cents,debt_cents FROM sales WHERE operation_id=@o",
            x => new PostSaleResult(x.GetInt64(0), x.GetString(1), Money.FromCents(x.GetInt64(2)), Money.FromCents(x.GetInt64(3)), Money.FromCents(x.GetInt64(4)), true),
            ("@o", r.OperationId.ToString("N"))).FirstOrDefault();
        if (existing is not null) return existing;

        var cashierRole = Common.RequireActive(c, r.CashierId);

        var shift = Sql.Query(c, "SELECT cashier_id,status,business_date FROM shifts WHERE id=@s",
            x => (Cashier: x.GetInt64(0), Status: x.GetString(1), Date: x.GetString(2)), ("@s", r.ShiftId)).FirstOrDefault();
        if (shift.Status != "open" || shift.Cashier != r.CashierId)
            throw new PosException(PosError.ShiftNotOpen, "No open shift for this cashier.");

        var mode = Sql.Scalar<string>(c, "SELECT value FROM settings WHERE key='tax_price_mode'")
            ?? throw new PosException(PosError.TaxNotConfigured, "Tax configuration has not been set by the owner.");
        var inclusive = mode == "inclusive";

        // Discounts and price overrides need an owner (the cashier is the owner, or a named owner approves).
        var needsApproval = !(r.OrderDiscount?.IsNone ?? true)
            || r.Lines.Any(l => !(l.Discount?.IsNone ?? true) || l.PriceOverride is not null) || r.SuppressedPromotionIds is {Count:>0};
        long? approver = null;
        if (needsApproval)
        {
            if (cashierRole == Role.Owner) approver = r.CashierId;
            else if (r.ApproverId is long a)
            {
                Common.RequireOwner(c, a);
                approver = a;
            }
            else throw new PosException(PosError.ApprovalRequired, "Owner approval is required for discounts and price overrides.");
        }

        // Load products, price the cart.
        var products = new List<ProductRow>();
        var inputs = new List<PricingLineInput>();
        foreach (var l in r.Lines)
        {
            var p = Sql.Query(c, "SELECT id,sku,name,price_cents,tax_rate_bp,avg_cost_x10000,active,allow_decimal,size,color_name_en FROM products WHERE id=@i",
                x => new ProductRow(x.GetInt64(0), x.GetString(1), x.GetString(2), Money.FromCents(x.GetInt64(3)),
                    x.GetInt64(4) / 10000m, x.GetInt64(5), x.GetInt64(6) == 1, x.GetInt64(7) == 1,
                    x.IsDBNull(8) ? null : x.GetString(8), x.IsDBNull(9) ? null : x.GetString(9)), ("@i", l.ProductId)).FirstOrDefault();
            if (p is null || !p.Active)
                throw new PosException(PosError.ProductUnavailable, "Product is unavailable for sale.");
            if (!p.AllowDecimal && l.Quantity != decimal.Truncate(l.Quantity))
                throw new PosException(PosError.InvalidQuantity, "This product is sold in whole units.");
            Money.ToMilli(l.Quantity);
            products.Add(p);
            inputs.Add(new PricingLineInput(l.Quantity, l.PriceOverride ?? p.Price, l.Discount ?? Discount.None, p.TaxRate));
        }

        var doc = PricingEngine.Price(inputs, r.OrderDiscount, inclusive);
        if(r.SuppressedPromotionIds is {Count:>0} && string.IsNullOrWhiteSpace(r.PromotionOverrideReason))
            throw new PosException(PosError.InvalidDiscount,"A reason is required to remove an automatic promotion.");
        var promotions=new PromotionService(db,clock).Quote(c,doc,products.Select(p=>p.Id).ToList(),r.CustomerId,r.Coupon,r.SuppressedPromotionIds,inclusive);
        doc=promotions.Document;
        if(r.ExpectedTotal is decimal expected && expected!=doc.Total)throw new PosException(PosError.PaymentMismatch,"The cart price changed. Review the updated offers and payments.");

        // Negative stock is blocked, aggregated per product.
        foreach (var g in r.Lines.Select((l, i) => (l, i)).GroupBy(x => x.l.ProductId))
        {
            var wanted = g.Sum(x => x.l.Quantity);
            if (Money.FromMilli(StockTracking.OnHand(c, g.Key, StockTracking.SalesLocation(c))) < wanted)
                throw new PosException(PosError.InsufficientStock, $"Insufficient stock for {products[g.First().i].Name}.");
        }

        // Payments: change only from cash; no electronic overpayment.
        if (r.Payments.Any(p => p.Amount <= 0 || Money.FromCents(Money.ToCents(p.Amount))!=p.Amount)) throw new PosException(PosError.PaymentMismatch, "Payment amounts must be positive piastres.");
        var resolved=r.Payments.Select(p=>(Request:p,Method:PaymentMethodService.Resolve(c,p.Method,p.MethodId),Card:PaymentMethodService.Confirm(p.Method,p.Amount,p.CardConfirmation))).ToList();
        var total = Money.ToCents(doc.Total);
        var loyalty=new LoyaltyService(db,clock);
        var redemption=loyalty.ValidateRedemption(c,r.CustomerId,r.RedeemPoints,total);
        if(r.ExpectedRedemptionValue is decimal expectedRedemption && expectedRedemption!=Money.FromCents(redemption))
            throw new PosException(PosError.PaymentMismatch,"The point value changed. Review the updated redemption and payments.");
        var nonCash = r.Payments.Where(p => p.Method != PaymentMethod.Cash).Sum(p => Money.ToCents(p.Amount))+redemption;
        var tendered = r.Payments.Where(p => p.Method == PaymentMethod.Cash).Sum(p => Money.ToCents(p.Amount));
        if (nonCash > total) throw new PosException(PosError.PaymentMismatch, "Electronic payments exceed the total.");
        var cashApplied = Math.Min(tendered, total - nonCash);
        var change = tendered - cashApplied;
        var debt = total - nonCash - cashApplied;
        var limitOverridden = false;
        IReadOnlyList<(string Due, long Cents)>? plan = null;

        if (debt > 0)
        {
            if(!PaymentMethodService.Get(c,4).Enabled)throw new PosException(PosError.CreditNotAllowed,"Customer credit is disabled in payment methods.");
            if (r.CustomerId is not long cid)
                throw new PosException(PosError.CreditNotAllowed, "An unpaid balance requires an identified customer.");
            var cust = Sql.Query(c, "SELECT credit_enabled,credit_limit_cents,active FROM customers WHERE id=@c",
                x => (Enabled: x.GetInt64(0) == 1, Limit: x.GetInt64(1), Active: x.GetInt64(2) == 1), ("@c", cid)).FirstOrDefault();
            if (!cust.Enabled || !cust.Active)
                throw new PosException(PosError.CreditNotAllowed, "This customer is not enabled for credit.");
            if (Common.CustomerBalanceCents(c, cid) + debt > cust.Limit)
            {
                // Exceeding the limit is possible only with an owner (the owner cashier, or a named approving owner).
                long? overrider = cashierRole == Role.Owner ? r.CashierId : r.ApproverId;
                if (overrider is not long ov)
                    throw new PosException(PosError.CreditLimitExceeded, "The sale exceeds the customer's credit limit.");
                Common.RequireOwner(c, ov);
                approver ??= ov;
                limitOverridden = true;
            }
            plan = InstallmentPlans.Validate(r.Arrangement, debt, Common.Today(clock));
        }
        else if (r.Arrangement is { Installments.Count: > 0 })
            throw new PosException(PosError.InvalidPrice, "There is no unpaid balance to schedule.");

        // ---- write everything ----
        var stamp = Common.Stamp(clock);
        var expiredApproved = !string.IsNullOrWhiteSpace(r.ExpiredStockReason);
        if (expiredApproved)
        {
            Common.RequireOwner(c, cashierRole == Role.Owner ? r.CashierId : r.ApproverId ?? throw new PosException(PosError.ApprovalRequired, "Owner approval is required for expired stock."));
            approver ??= cashierRole == Role.Owner ? r.CashierId : r.ApproverId;
        }
        var number = Common.NextNumber(c, "S");

        var saleId = Sql.Insert(c,
            """
            INSERT INTO sales(number,operation_id,shift_id,cashier_id,approver_id,customer_id,created_at,business_date,tax_inclusive,
              gross_cents,discount_cents,taxable_cents,tax_cents,total_cents,paid_cents,change_cents,debt_cents,arrangement,due_date)
            VALUES(@n,@o,@sh,@ca,@ap,@cu,@t,@bd,@ti,@g,@d,@tx,@tt,@tot,@pd,@ch,@db,@ar,@dd)
            """,
            ("@n", number), ("@o", r.OperationId.ToString("N")), ("@sh", r.ShiftId), ("@ca", r.CashierId), ("@ap", approver),
            ("@cu", r.CustomerId), ("@t", stamp), ("@bd", shift.Date), ("@ti", inclusive ? 1 : 0),
            ("@g", Money.ToCents(doc.Gross)), ("@d", Money.ToCents(doc.Discount)), ("@tx", Money.ToCents(doc.Taxable)),
            ("@tt", Money.ToCents(doc.Tax)), ("@tot", total), ("@pd", nonCash + cashApplied), ("@ch", change), ("@db", debt),
            ("@ar", debt == 0 ? "none" : plan is { Count: > 0 } ? "installment" : "credit"), ("@dd", debt > 0 && plan is not { Count: > 0 } ? r.Arrangement?.DueDate : null));
        FaultInjector?.Invoke("after_sale_insert");
        Sql.Exec(c,"UPDATE sales SET coupon=@c WHERE id=@i",("@c",r.Coupon?.Trim()),("@i",saleId));

        var saleLineIds=new List<long>();
        for (var i = 0; i < doc.Lines.Count; i++)
        {
            var l = doc.Lines[i];
            var p = products[i];
            var cogs = Money.ToCents(l.Quantity * Money.FromCost4(p.AvgCost4));
            var saleLineId = Sql.Insert(c,
                """
                INSERT INTO sale_lines(sale_id,line_no,product_id,sku,name,qty_milli,unit_price_cents,gross_cents,line_discount_cents,
                  order_discount_cents,taxable_cents,tax_cents,total_cents,tax_rate_bp,unit_cost_x10000,cogs_cents,size,color)
                VALUES(@s,@n,@p,@sku,@nm,@q,@up,@g,@ld,@od,@tx,@tt,@tot,@bp,@uc,@cg,@sz,@co)
                """,
                ("@s", saleId), ("@n", i + 1), ("@p", p.Id), ("@sku", p.Sku), ("@nm", p.Name), ("@q", Money.ToMilli(l.Quantity)),
                ("@up", Money.ToCents(l.UnitPrice)), ("@g", Money.ToCents(l.Gross)), ("@ld", Money.ToCents(l.LineDiscount)),
                ("@od", Money.ToCents(l.OrderDiscount)), ("@tx", Money.ToCents(l.Taxable)), ("@tt", Money.ToCents(l.Tax)),
                ("@tot", Money.ToCents(l.Total)), ("@bp", decimal.ToInt64(l.TaxRate * 10000m)), ("@uc", p.AvgCost4), ("@cg", cogs), ("@sz", p.Size), ("@co", p.Color));
            saleLineIds.Add(saleLineId);
            StockTracking.ConsumeSale(c, p.Id, saleId, saleLineId, Money.ToMilli(l.Quantity), p.AvgCost4, r.CashierId, stamp, Common.Today(clock), expiredApproved);
            foreach(var applied in promotions.Evaluation.Applied.Where(a=>a.LineIndex==i))
                Sql.Exec(c,"INSERT INTO applied_promotions(sale_id,sale_line_id,promotion_id,amount_cents,name_en,name_ar) VALUES(@s,@l,@p,@a,@e,@ar)",
                    ("@s",saleId),("@l",saleLineId),("@p",applied.PromotionId),("@a",applied.AmountCents),("@e",applied.NameEn),("@ar",applied.NameAr));
        }
        if(r.SuppressedPromotionIds is {Count:>0})Common.Audit(c,clock,r.CashierId,approver,"promotion.override",number,r.PromotionOverrideReason,string.Join(",",r.SuppressedPromotionIds));
        if (expiredApproved) Common.Audit(c, clock, r.CashierId, approver, "stock.expiry_override", number, r.ExpiredStockReason);
        FaultInjector?.Invoke("after_stock");

        var cashLeft=cashApplied;
        foreach(var p in resolved)
        {
            var amount=Money.ToCents(p.Request.Amount);var applied=p.Method.Kind=="cash"?Math.Min(cashLeft,amount):amount;
            if(p.Method.Kind=="cash")cashLeft-=applied;
            var paymentId=Sql.Insert(c,"INSERT INTO payments(sale_id,method,applied_cents,tendered_cents,reference) VALUES(@s,@m,@a,@t,@r)",
                ("@s",saleId),("@m",p.Method.Kind),("@a",applied),("@t",amount),("@r",p.Request.Reference));
            PaymentMethodService.Snapshot(c,"payments",paymentId,p.Method,p.Card);
        }
        loyalty.RecordSale(c,saleId,r.CustomerId,r.CashierId,r.RedeemPoints,redemption,doc,saleLineIds);

        if (cashApplied > 0)
            Sql.Exec(c, "INSERT INTO cash_movements(shift_id,kind,amount_cents,source_type,source_id,created_at) VALUES(@sh,'sale_cash',@a,'sale',@s,@t)",
                ("@sh", r.ShiftId), ("@a", cashApplied), ("@s", saleId), ("@t", stamp));
        if (debt > 0)
            Sql.Exec(c, "INSERT INTO customer_ledger(customer_id,entry_type,amount_cents,source_type,source_id,created_at) VALUES(@c,'credit_sale',@a,'sale',@s,@t)",
                ("@c", r.CustomerId), ("@a", debt), ("@s", saleId), ("@t", stamp));
        if (plan is { Count: > 0 })
            InstallmentPlans.Create(c, stamp, saleId, "initial", null, r.CashierId, plan.Select(x => (x.Due, x.Cents, 0L)).ToList());
        FaultInjector?.Invoke("after_money");

        Common.Audit(c, clock, r.CashierId, approver, "sale.post", number, null, $"total={Money.FromCents(total)}");
        if (limitOverridden) Common.Audit(c, clock, r.CashierId, approver, "credit.limit_override", number, null, $"customer={r.CustomerId};debt={Money.FromCents(debt)}");
        Sql.Exec(c, "INSERT INTO print_jobs(sale_id,status,created_at) VALUES(@s,'queued',@t)", ("@s", saleId), ("@t", stamp));

        return new PostSaleResult(saleId, number, Money.FromCents(total), Money.FromCents(change), Money.FromCents(debt), false);
    }
}

