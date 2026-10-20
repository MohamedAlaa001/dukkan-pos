using LocalCashier.Application;
using LocalCashier.Domain;

namespace LocalCashier.Persistence;

public sealed record ReturnLineRequest(long SaleLineId, decimal Quantity, bool Resalable, long? LotId = null);

public sealed record PostReturnRequest(
    Guid OperationId, long SaleId, long ShiftId, long ActorId, long? ApproverId, string Reason,
    IReadOnlyList<ReturnLineRequest> Lines, PaymentMethod? RefundMethod,long? RefundMethodId=null);

public sealed record PostReturnResult(long ReturnId, string Number, decimal Total, decimal DebtReduction, decimal Refund, bool AlreadyPosted);

public sealed record ReturnPreview(decimal Total, decimal DebtReduction, decimal Refund,IReadOnlyList<RefundPaymentRow>? Payments=null);

public sealed partial class ReturnService(PosDatabase db, IClock clock)
{
    public Action<string>? FaultInjector { get; set; }

    /// <summary>What a return would do (value, debt cleared first, refund only for the rest) without posting anything.</summary>
    public ReturnPreview Preview(long saleId, IReadOnlyList<ReturnLineRequest> lines,PaymentMethod? refundMethod=null,long? refundMethodId=null)
    {
        using var c = db.Open();
        long total = 0;
        foreach (var l in lines.Where(l => l.Quantity > 0))
        {
            var sl = Sql.Query(c, "SELECT qty_milli,taxable_cents,tax_cents FROM sale_lines WHERE id=@i AND sale_id=@s",
                x => (Qty: x.GetInt64(0), Taxable: x.GetInt64(1), Tax: x.GetInt64(2)), ("@i", l.SaleLineId), ("@s", saleId)).FirstOrDefault();
            if (sl.Qty == 0) throw new PosException(PosError.NotFound, "Sale line not found on this sale.");
            var prior = Sql.Query(c, "SELECT COALESCE(SUM(qty_milli),0),COALESCE(SUM(taxable_cents),0),COALESCE(SUM(tax_cents),0) FROM return_lines WHERE sale_line_id=@i",
                x => (Qty: x.GetInt64(0), Taxable: x.GetInt64(1), Tax: x.GetInt64(2)), ("@i", l.SaleLineId))[0];
            var amt = ReturnCalculator.Calculate(Money.FromMilli(sl.Qty), Money.FromCents(sl.Taxable), Money.FromCents(sl.Tax),
                Money.FromMilli(prior.Qty), Money.FromCents(prior.Taxable), Money.FromCents(prior.Tax), l.Quantity);
            total += Money.ToCents(amt.Total);
        }
        var debt = Math.Min(total, Math.Max(0, Common.SaleOutstandingCents(c, saleId)));
        return new ReturnPreview(Money.FromCents(total), Money.FromCents(debt), Money.FromCents(total - debt),PlanRefund(c,saleId,total-debt,refundMethodId??LegacyRefundMethod(refundMethod)));
    }

    public PostReturnResult Post(PostReturnRequest r)
    {
        using var c = db.Open();
        return Db.InTransaction(c, () =>
        {
            var existing = Sql.Query(c, "SELECT id,number,total_cents,debt_reduction_cents,refund_cents FROM returns WHERE operation_id=@o",
                x => new PostReturnResult(x.GetInt64(0), x.GetString(1), Money.FromCents(x.GetInt64(2)), Money.FromCents(x.GetInt64(3)), Money.FromCents(x.GetInt64(4)), true),
                ("@o", r.OperationId.ToString("N"))).FirstOrDefault();
            if (existing is not null) return existing;

            // Every return needs an owner: the actor, or a named approving owner.
            var role = Common.RequireActive(c, r.ActorId);
            long approver;
            if (role == Role.Owner) approver = r.ActorId;
            else if (r.ApproverId is long a) { Common.RequireOwner(c, a); approver = a; }
            else throw new PosException(PosError.ApprovalRequired, "Owner approval is required for returns.");

            if (string.IsNullOrWhiteSpace(r.Reason)) throw new PosException(PosError.InvalidDiscount, "A return reason is required.");
            if (r.Lines.Count == 0) throw new PosException(PosError.EmptyCart, "Nothing to return.");
            if (r.Lines.Select(l => l.SaleLineId).Distinct().Count() != r.Lines.Count)
                throw new PosException(PosError.Duplicate, "A sale line appears more than once.");
            Common.RequireOpenShift(c, r.ShiftId);

            var sale = Sql.Query(c, "SELECT customer_id,paid_cents,status FROM sales WHERE id=@s",
                x => (Customer: x.IsDBNull(0) ? (long?)null : x.GetInt64(0), Paid: x.GetInt64(1), Status: x.GetString(2)), ("@s", r.SaleId)).FirstOrDefault();
            if (sale.Status != "posted") throw new PosException(PosError.NotFound, "Original sale not found.");

            var stamp = Common.Stamp(clock);
            var parts = new List<(ReturnLineRequest Req, long ProductId, ReturnAmounts Amt, long CogsCents, long Cost4)>();
            foreach (var l in r.Lines)
            {
                var sl = Sql.Query(c, "SELECT product_id,qty_milli,taxable_cents,tax_cents,unit_cost_x10000,cogs_cents FROM sale_lines WHERE id=@i AND sale_id=@s",
                    x => (Product: x.GetInt64(0), Qty: x.GetInt64(1), Taxable: x.GetInt64(2), Tax: x.GetInt64(3), Cost4: x.GetInt64(4), Cogs:x.GetInt64(5)),
                    ("@i", l.SaleLineId), ("@s", r.SaleId)).FirstOrDefault();
                if (sl.Qty == 0) throw new PosException(PosError.NotFound, "Sale line not found on this sale.");
                var prior = Sql.Query(c, "SELECT COALESCE(SUM(qty_milli),0),COALESCE(SUM(taxable_cents),0),COALESCE(SUM(tax_cents),0) FROM return_lines WHERE sale_line_id=@i",
                    x => (Qty: x.GetInt64(0), Taxable: x.GetInt64(1), Tax: x.GetInt64(2)), ("@i", l.SaleLineId))[0];

                var amt = ReturnCalculator.Calculate(
                    Money.FromMilli(sl.Qty), Money.FromCents(sl.Taxable), Money.FromCents(sl.Tax),
                    Money.FromMilli(prior.Qty), Money.FromCents(prior.Taxable), Money.FromCents(prior.Tax), l.Quantity);
                var priorCogs=Sql.Scalar<long>(c,"SELECT COALESCE(SUM(cogs_reversed_cents),0) FROM return_lines WHERE sale_line_id=@i",("@i",l.SaleLineId));
                var cumulative=(long)decimal.Round((decimal)sl.Cogs*(prior.Qty+Money.ToMilli(l.Quantity))/sl.Qty,0,MidpointRounding.AwayFromZero);
                var cogs=Math.Max(0,cumulative-priorCogs);
                parts.Add((l, sl.Product, amt, cogs, sl.Cost4));
            }

            var totalCents = parts.Sum(p => Money.ToCents(p.Amt.Total));
            var outstanding = Common.SaleOutstandingCents(c, r.SaleId);
            var debtReduction = Math.Min(totalCents, Math.Max(0, outstanding));
            var refund = totalCents - debtReduction;
            IReadOnlyList<RefundPaymentRow> refundPlan=[];

            if (refund > 0)
            {
                var collected = Sql.Scalar<long>(c, "SELECT COALESCE(SUM(amount_cents),0) FROM collection_allocations WHERE sale_id=@s AND collection_id NOT IN(SELECT collection_id FROM collection_reversals)", ("@s", r.SaleId));
                var refunded = Sql.Scalar<long>(c, "SELECT COALESCE(SUM(refund_cents),0) FROM returns WHERE sale_id=@s", ("@s", r.SaleId));
                if (refund > sale.Paid + collected - refunded)
                    throw new PosException(PosError.PaymentMismatch, "Refund exceeds the amount actually paid.");
                refundPlan=PlanRefund(c,r.SaleId,refund,r.RefundMethodId??LegacyRefundMethod(r.RefundMethod));
                if (refundPlan.Where(p=>p.Kind=="cash").Sum(p=>Money.ToCents(p.Amount)) > Common.ExpectedCashCents(c, r.ShiftId))
                    throw new PosException(PosError.PaymentMismatch, "The drawer does not hold enough recorded cash for this refund.");
            }

            var number = Common.NextNumber(c, "R");
            var returnId = Sql.Insert(c,
                """
                INSERT INTO returns(number,operation_id,sale_id,shift_id,actor_id,approver_id,created_at,reason,taxable_cents,tax_cents,
                  total_cents,debt_reduction_cents,refund_cents,refund_method)
                VALUES(@n,@o,@s,@sh,@a,@ap,@t,@r,@tx,@tt,@tot,@dr,@rf,@rm)
                """,
                ("@n", number), ("@o", r.OperationId.ToString("N")), ("@s", r.SaleId), ("@sh", r.ShiftId), ("@a", r.ActorId), ("@ap", approver),
                ("@t", stamp), ("@r", r.Reason), ("@tx", parts.Sum(p => Money.ToCents(p.Amt.Taxable))), ("@tt", parts.Sum(p => Money.ToCents(p.Amt.Tax))),
                ("@tot", totalCents), ("@dr", debtReduction), ("@rf", refund),
                ("@rm", refundPlan.Count>1?"split":refundPlan.FirstOrDefault()?.Kind));
            FaultInjector?.Invoke("after_return_insert");
            foreach(var pay in refundPlan)Sql.Exec(c,"INSERT INTO refund_payments(return_id,method_id,kind,method_name_en,method_name_ar,amount_cents) VALUES(@r,@m,@k,@e,@a,@c)",
                ("@r",returnId),("@m",pay.MethodId),("@k",pay.Kind),("@e",pay.NameEn),("@a",pay.NameAr),("@c",Money.ToCents(pay.Amount)));

            foreach (var p in parts)
            {
                var returnLineId = Sql.Insert(c,
                    """
                    INSERT INTO return_lines(return_id,sale_line_id,qty_milli,taxable_cents,tax_cents,total_cents,cogs_reversed_cents,resalable)
                    VALUES(@r,@sl,@q,@tx,@tt,@tot,@cg,@rs)
                    """,
                    ("@r", returnId), ("@sl", p.Req.SaleLineId), ("@q", Money.ToMilli(p.Req.Quantity)), ("@tx", Money.ToCents(p.Amt.Taxable)),
                    ("@tt", Money.ToCents(p.Amt.Tax)), ("@tot", Money.ToCents(p.Amt.Total)), ("@cg", p.CogsCents), ("@rs", p.Req.Resalable ? 1 : 0));

                PromotionService.RecordReturn(c,p.Req.SaleLineId,returnLineId,p.Req.Quantity);
                new LoyaltyService(db,clock).RecordReturnLine(c,r.SaleId,returnId,returnLineId,p.Req.SaleLineId,p.Req.Quantity,r.ActorId);

                StockTracking.ReturnSale(c, p.ProductId, p.Req.SaleLineId, returnLineId, returnId, Money.ToMilli(p.Req.Quantity), p.Cost4,
                    p.Req.Resalable, p.Req.LotId, r.ActorId, stamp);
                if (p.Req.Resalable)
                {
                    // Back into stock at the original sale cost; the moving average is updated accordingly.
                    var onHand = Common.OnHand(c, p.ProductId) - p.Req.Quantity;
                    var avg = Money.FromCost4(Sql.Scalar<long>(c, "SELECT avg_cost_x10000 FROM products WHERE id=@p", ("@p", p.ProductId)));
                    var newAvg = CostCalculator.WeightedAverage(onHand, avg, p.Req.Quantity, Money.FromCost4(p.Cost4));
                    Sql.Exec(c, "UPDATE products SET avg_cost_x10000=@v WHERE id=@p", ("@v", Money.ToCost4(newAvg)), ("@p", p.ProductId));
                }
                else
                {
                    // Not resalable: no sellable stock; the loss is recorded once (COGS is reversed by the return line).
                    Sql.Exec(c,
                        "INSERT INTO stock_losses(product_id,qty_milli,cost_cents,source_type,source_id,reason,created_at) VALUES(@p,@q,@c,'return',@s,@r,@t)",
                        ("@p", p.ProductId), ("@q", Money.ToMilli(p.Req.Quantity)), ("@c", p.CogsCents), ("@s", returnId), ("@r", r.Reason), ("@t", stamp));
                }
            }
            FaultInjector?.Invoke("after_return_stock");

            if (debtReduction > 0 && sale.Customer is long cust)
                Sql.Exec(c, "INSERT INTO customer_ledger(customer_id,entry_type,amount_cents,source_type,source_id,created_at) VALUES(@c,'return_credit',@a,'return',@s,@t)",
                    ("@c", cust), ("@a", -debtReduction), ("@s", returnId), ("@t", stamp));
            var cashRefund=refundPlan.Where(p=>p.Kind=="cash").Sum(p=>Money.ToCents(p.Amount));
            if (cashRefund > 0)
                Sql.Exec(c, "INSERT INTO cash_movements(shift_id,kind,amount_cents,source_type,source_id,created_at) VALUES(@sh,'refund_cash',@a,'return',@s,@t)",
                    ("@sh", r.ShiftId), ("@a", -cashRefund), ("@s", returnId), ("@t", stamp));

            if (debtReduction > 0) InstallmentPlans.ReduceLatestUnpaid(c, Common.Stamp(clock), r.SaleId, debtReduction, r.ActorId, $"Return {number}");
            new LoyaltyService(db,clock).RestoreRedemption(c,r.SaleId,returnId,r.ActorId);
            Common.Audit(c, clock, r.ActorId, approver, "return.post", number, r.Reason);
            if(refund>0 && (r.RefundMethod is not null || r.RefundMethodId is not null))Common.Audit(c,clock,r.ActorId,approver,"refund.method_override",number,r.Reason,$"methods={string.Join(",",refundPlan.Select(p=>p.Code))};amount={Money.FromCents(refund)}");
            return new PostReturnResult(returnId, number, Money.FromCents(totalCents), Money.FromCents(debtReduction), Money.FromCents(refund), false);
        });
    }
    static long? LegacyRefundMethod(PaymentMethod? method)=>method switch{PaymentMethod.Cash=>1,PaymentMethod.Card=>2,PaymentMethod.Transfer=>3,null=>null,_=>throw new PosException(PosError.PaymentMismatch,"Unknown refund method.")};
}


