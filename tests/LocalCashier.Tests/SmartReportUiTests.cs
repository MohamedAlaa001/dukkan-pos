using Dukkan.UI;
using LocalCashier.Application;
using LocalCashier.Printing;

namespace LocalCashier.Tests;

public class SmartReportUiTests
{
    [Fact] public void Closing_a_shift_offers_the_cashiers_own_daily_summary()
    {
        Sta.Run(()=>
        {
            using var dir=new TempFolder();var s=AppServices.Create(dir.Combine("close"),new FakePrinter());var owner=s.Users.Create("owner","Owner",Role.Owner,"owner-test-1");s.Settings.ConfigureTax(owner,false);var cashier=s.Users.Create("cash","Cashier",Role.Cashier,"cash-test-1");s.Shifts.Open(cashier,100);
            var shell=new ShellViewModel(s);shell.SignedIn(s.Users.Login("cash","cash-test-1").User!);var close=new CloseShiftViewModel(shell){Counted="100"};close.CountCommand.Execute(null);close.CloseShiftCommand.Execute(null);
            var summary=Assert.IsType<DailySummaryViewModel>(shell.Dialog);Assert.Equal(cashier,summary.Report.CashierId);Assert.Equal(100,summary.Report.CountedCash);Assert.DoesNotContain(summary.Metrics,m=>m.Name==Strings.Get("Smart_Cost",Localizer.Instance.IsArabic));return true;
        });
    }
    [Fact] public void Dashboard_compares_today_with_yesterday_and_zero_baseline_is_neutral()
    {
        Sta.Run(()=>
        {
            using var dir=new TempFolder();var s=AppServices.Create(dir.Combine("comparison"),new FakePrinter());var owner=s.Users.Create("owner","Owner",Role.Owner,"owner-test-1");s.Settings.ConfigureTax(owner,false);var p=s.Catalog.AddProduct(owner,"W","W","Widget",null,10);s.Inventory.Receive(owner,p,10,4);var sh=s.Shifts.Open(owner,100);
            var shell=new ShellViewModel(s);shell.SignedIn(s.Users.Login("owner","owner-test-1").User!);Assert.Equal(BadgeKind.Neutral,new DashboardViewModel(shell).SalesDelta.Kind);
            var oldSale=s.Sales.Post(new(Guid.NewGuid(),sh,owner,null,[new(p,1)],null,[new(PaymentMethod.Cash,10)]));using(var c=s.Db.Open()){using var q=c.CreateCommand();q.CommandText="UPDATE sales SET business_date=$d WHERE id=$s";q.Parameters.AddWithValue("$d",DateTime.Parse(s.Queries.Today).AddDays(-1).ToString("yyyy-MM-dd"));q.Parameters.AddWithValue("$s",oldSale.SaleId);q.ExecuteNonQuery();}
            s.Sales.Post(new(Guid.NewGuid(),sh,owner,null,[new(p,2)],null,[new(PaymentMethod.Cash,20)]));var dashboard=new DashboardViewModel(shell);Assert.Equal(20,dashboard.TotalSales);Assert.Equal(BadgeKind.Success,dashboard.SalesDelta.Kind);Assert.Contains("100",dashboard.SalesDelta.Text);return true;
        });
    }
    [Fact] public void Reports_keep_filters_and_export_localized_BOM_csv()
    {
        Sta.Run(()=>
        {
            using var dir=new TempFolder();var s=AppServices.Create(dir.Combine("reports"),new FakePrinter());var owner=s.Users.Create("owner","Owner",Role.Owner,"owner-test-1");s.Settings.ConfigureTax(owner,false);
            var p=s.Catalog.AddProduct(owner,"W","W","Widget",null,10);s.Inventory.Receive(owner,p,10,4);var shift=s.Shifts.Open(owner,500);s.Sales.Post(new(Guid.NewGuid(),shift,owner,null,[new(p,2)],null,[new(PaymentMethod.Cash,20)]));
            var shell=new ShellViewModel(s);shell.SignedIn(s.Users.Login("owner","owner-test-1").User!);var vm=new SmartReportsViewModel(shell);vm.Reload(s.Queries.Today,s.Queries.Today,owner);Assert.Equal(20,vm.Rows.Sum(r=>r.Revenue));
            var old=Localizer.Instance.IsArabic;try{Localizer.Instance.IsArabic=true;var path=dir.Combine("report.csv");vm.ExportTo(path);var bytes=File.ReadAllBytes(path);Assert.Equal(new byte[]{239,187,191},bytes.Take(3));Assert.Contains(Strings.Get("Smart_Revenue",true),File.ReadAllText(path));}finally{Localizer.Instance.IsArabic=old;}
            vm.Reload("2000-01-01","2000-01-01",owner);Assert.Empty(vm.Rows);return true;
        });
    }
    [Theory][InlineData(false)][InlineData(true)]
    public void Daily_summary_documents_are_one_page_and_keep_cash_and_tax_labels(bool arabic)
    {
        Sta.Run(()=>
        {
            var model=new LocalCashier.Persistence.DailySummaryReport("2026-10-19",null,100,10,80,10,5,500,585,null,1,[],[]);
            var labels=SmartReportsViewModel.DocumentLabels(arabic);
            foreach(var a4 in new[]{false,true})
            {
                var pages=DailyDocuments.Build(model,"Test shop",arabic,a4,labels);Assert.Single(pages);Assert.True(pages[0].ActualHeight>200);Assert.True(pages[0].ActualWidth>200);
                using var dir=new TempFolder();var pdf=dir.Combine("daily.pdf");PdfWriter.Save(pdf,pages);Assert.True(new FileInfo(pdf).Length>1000);
            }
            return true;
        });
    }
}
