using Dukkan.UI;
using LocalCashier.Application;
using LocalCashier.Domain;

namespace LocalCashier.Tests;

public class InventoryWorkspaceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Opening_count_supports_a_category_or_an_explicit_multi_product_selection(bool category)
    {
        Sta.Run(()=>
        {
            using var dir=new TempFolder();var s=AppServices.Create(dir.Combine("scopes"),new FakePrinter());
            var owner=s.Users.Create("owner","Owner",Role.Owner,"owner-test-1");
            var a=s.Styles.CreateStyle(owner,new("Food A",null,"Food","TShirt",10,4),[new("","","","")]);
            var b=s.Styles.CreateStyle(owner,new("Food B",null,"Food","TShirt",10,4),[new("","","","")]);
            var c=s.Styles.CreateStyle(owner,new("Other",null,"Other","TShirt",10,4),[new("","","","")]);
            var ids=new[]{s.Styles.Variants(a).Single().ProductId,s.Styles.Variants(b).Single().ProductId};
            var shell=new ShellViewModel(s);shell.SignedIn(s.Users.Login("owner","owner-test-1").User!);
            long? opened=null;var vm=new CountOpenViewModel(shell,1,null,id=>opened=id);
            vm.Scope=vm.Scopes.Single(o=>o.Key==(category?"Stock_Scope_Category":"Stock_Scope_Selected"));
            if(category)vm.Category=vm.Categories.Single(x=>x.Key=="Food");else foreach(var row in vm.Products.Where(p=>ids.Contains(p.Product.Id)))row.Selected=true;
            vm.SaveCommand.Execute(null);Assert.NotNull(opened);
            Assert.Equal(ids.Order(),s.Stock.CountLines(opened.Value).Select(l=>l.ProductId).Order());
            return true;
        });
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Store_save_persists_fullscreen_and_calls_startup_and_window_controls(bool enabled)
    {
        Sta.Run(()=>
        {
            using var dir=new TempFolder();var s=AppServices.Create(dir.Combine("settings"),new FakePrinter());
            var owner=s.Users.Create("owner","Owner",Role.Owner,"owner-test-1");
            var shell=new ShellViewModel(s);shell.SignedIn(s.Users.Login("owner","owner-test-1").User!);
            bool? startup=null,fullscreen=null;
            var vm=new SettingsViewModel(shell,value=>startup=value,value=>fullscreen=value) { StoreName="Test store",StartFullScreen=enabled,StartWithWindows=enabled };
            vm.SaveStoreCommand.Execute(null);
            Assert.Equal(enabled?"1":"0",s.Settings.GetValue("start_fullscreen"));Assert.Equal(enabled,startup);Assert.Equal(enabled,fullscreen);
            var cashier=s.Users.Create("cashier","Cashier",Role.Cashier,"cashier-test-1");
            Assert.Throws<PosException>(()=>s.Settings.SetValue(cashier,"start_fullscreen","1"));
            return true;
        });
    }

    [Fact]
    public void Cashier_can_open_count_entry_but_cannot_approve_and_resaving_unchanged_count_preserves_entry_time()
    {
        Sta.Run(()=>
        {
            using var dir=new TempFolder();var s=AppServices.Create(dir.Combine("stock"),new FakePrinter());
            var owner=s.Users.Create("owner","Owner",Role.Owner,"owner-test-1");var cashier=s.Users.Create("cashier","Cashier",Role.Cashier,"cashier-test-1");
            var product=s.Catalog.AddProduct(owner,"W","W","Widget",null,10);s.Inventory.Receive(owner,product,10,4);
            var count=s.Stock.CreateCount(Guid.NewGuid(),owner,1,[product]);
            var shell=new ShellViewModel(s);shell.SignedIn(s.Users.Login("cashier","cashier-test-1").User!);shell.Nav="Inventory";
            var vm=Assert.IsType<StockWorkspaceViewModel>(shell.Page);Assert.False(vm.IsOwner);
            vm.CountLines.Single().Count="9";vm.SaveCountCommand.Execute(null);
            s.Stock.AdjustStock(Guid.NewGuid(),owner,product,1,null,-1,null,"Later movement");
            vm.SaveCountCommand.Execute(null);
            Assert.Equal(-1m,s.Stock.CountLines(count).Single().Variance);
            vm.RecountCommand.Execute(vm.CountLines.Single());
            Assert.Equal(0m,s.Stock.CountLines(count).Single().Variance);
            vm.Reason="Forbidden";vm.ApproveCountCommand.Execute(null);
            Assert.Equal("open",s.Stock.CountSessions().Single().State);
            Assert.NotEmpty(vm.Error);
            return true;
        });
    }
    [Fact]
    public void Owner_cannot_approve_visible_count_edits_until_saved_and_reviewed()
    {
        Sta.Run(()=>
        {
            using var dir=new TempFolder();var s=AppServices.Create(dir.Combine("stock"),new FakePrinter());
            var owner=s.Users.Create("owner","Owner",Role.Owner,"owner-test-1");var product=s.Catalog.AddProduct(owner,"W","W","Widget",null,10);s.Inventory.Receive(owner,product,10,4);
            var count=s.Stock.CreateCount(Guid.NewGuid(),owner,1,[product]);s.Stock.RecordCount(Guid.NewGuid(),owner,s.Stock.CountLines(count).Single().Id,10);
            var shell=new ShellViewModel(s);shell.SignedIn(s.Users.Login("owner","owner-test-1").User!);
            var vm=new StockWorkspaceViewModel(shell);vm.CountLines.Single().Count="8";vm.Reason="Checked";vm.ApproveCountCommand.Execute(null);
            Assert.Equal("open",s.Stock.CountSessions().Single().State);Assert.Equal(10m,s.Catalog.OnHand(product));Assert.NotEmpty(vm.Error);
            vm.SaveCountCommand.Execute(null);vm.ApproveCountCommand.Execute(null);Assert.Equal(8m,s.Catalog.OnHand(product));
            return true;
        });
    }
}
