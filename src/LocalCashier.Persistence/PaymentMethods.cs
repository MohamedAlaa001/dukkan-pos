using LocalCashier.Application;
using LocalCashier.Domain;
using Microsoft.Data.Sqlite;

namespace LocalCashier.Persistence;

public sealed record PaymentMethodRow(long Id,string Code,string NameEn,string NameAr,string Kind,bool Enabled,int SortOrder);
public sealed class PaymentMethodService(PosDatabase db,IClock clock)
{
    public IReadOnlyList<PaymentMethodRow> List(bool enabledOnly=true)
    {
        using var c=db.Open();return Sql.Query(c,"SELECT id,code,name_en,name_ar,kind,enabled,sort_order FROM payment_methods WHERE code<>'loyalty' AND (@all=1 OR enabled=1) ORDER BY sort_order,id",Read,("@all",enabledOnly?0:1));
    }
    static PaymentMethodRow Read(SqliteDataReader x)=>new(x.GetInt64(0),x.GetString(1),x.GetString(2),x.GetString(3),x.GetString(4),x.GetInt64(5)==1,x.GetInt32(6));
    public void Update(long ownerId,long methodId,string nameEn,string nameAr,bool enabled,int sortOrder)
    {
        using var c=db.Open();Db.InOwnerTransaction(c,()=>
        {
            if(methodId==10)throw new PosException(PosError.PaymentMismatch,"Loyalty is configured through its own settings.");
            if(string.IsNullOrWhiteSpace(nameEn)||string.IsNullOrWhiteSpace(nameAr)||nameEn.Trim().Length>80||nameAr.Trim().Length>80||sortOrder<0)
                throw new PosException(PosError.InvalidPrice,"Enter bilingual method names up to 80 characters and a nonnegative order.");
            if(Sql.Exec(c,"UPDATE payment_methods SET name_en=@e,name_ar=@a,enabled=@on,sort_order=@s WHERE id=@i",("@e",nameEn.Trim()),("@a",nameAr.Trim()),("@on",enabled?1:0),("@s",sortOrder),("@i",methodId))!=1)
                throw new PosException(PosError.NotFound,"Payment method not found.");
            Common.Audit(c,clock,ownerId,null,"payment_method.configure",methodId.ToString(),null,$"enabled={enabled};sort={sortOrder};en={nameEn.Trim()};ar={nameAr.Trim()}");return 0;
        },ownerId);
    }
    internal static PaymentMethodRow Get(SqliteConnection c,long id,bool requireEnabled=false)
    {
        var row=Sql.Query(c,"SELECT id,code,name_en,name_ar,kind,enabled,sort_order FROM payment_methods WHERE id=@i",Read,("@i",id)).FirstOrDefault();
        if(row is null || requireEnabled&&!row.Enabled)throw new PosException(PosError.PaymentMismatch,"Payment method is unavailable or disabled.");return row;
    }
    internal static PaymentMethodRow Resolve(SqliteConnection c,PaymentMethod kind,long? methodId)
    {
        var id=methodId??(kind switch{PaymentMethod.Cash=>1,PaymentMethod.Card=>2,PaymentMethod.Transfer=>3,_=>throw new PosException(PosError.PaymentMismatch,"Unknown payment kind.")});
        var row=Get(c,id,true);if(row.Kind!=kind.ToString().ToLowerInvariant())throw new PosException(PosError.PaymentMismatch,"Payment identity and kind do not match.");return row;
    }
    internal static CardConfirmation? Confirm(PaymentMethod method,decimal amount,CardConfirmation? card)
    {
        if(method!=PaymentMethod.Card)
        {
            if(card is not null)throw new PosException(PosError.PaymentMismatch,"Card confirmation belongs to a card payment.");return null;
        }
        var result=new ManualCardTerminal().Confirm(new(amount,card?.Approved??true,card?.ApprovalCode,card?.Last4));
        if(!result.Approved)throw new PosException(PosError.PaymentMismatch,"Confirm the payment on the terminal before posting.");return result;
    }
    internal static void Snapshot(SqliteConnection c,string table,long id,PaymentMethodRow method,CardConfirmation? card)
    {
        // Table names are a closed internal set, never user input.
        if(table is not("payments" or "collections" or "supplier_payments"))throw new ArgumentException(nameof(table));
        Sql.Exec(c,$"UPDATE {table} SET method_id=@m,method_name_en=@e,method_name_ar=@a,terminal_mode=@tm,approval_code=@ac,last4=@l WHERE id=@i",
            ("@m",method.Id),("@e",method.NameEn),("@a",method.NameAr),("@tm",card is null?null:"manual"),("@ac",card?.ApprovalCode),("@l",card?.Last4),("@i",id));
    }
}
