using Dukkan.UI;
using LocalCashier.Application;
using LocalCashier.Domain;
using LocalCashier.Persistence;

namespace LocalCashier.Tests;

public class ProductEditorTests
{
    [Fact]
    public void Editing_keeps_an_archived_category_and_an_empty_category_when_other_categories_exist()
    {
        Sta.Run(() =>
        {
            using var dir = new TempFolder();
            var s = AppServices.Create(dir.Combine("categories"), new FakePrinter());
            var owner = s.Users.Create("owner", "Owner", Role.Owner, "owner-test-1");
            s.Config.ApplyPreset(owner, "general", false);
            var category = s.Config.AddCategory(owner, "Seasonal");
            var categorized = s.Styles.CreateStyle(owner, new("Coat", null, "Seasonal", "TShirt", 20, 4), [new("", "", "", "")]);
            var uncategorized = s.Styles.CreateStyle(owner, new("Loose item", null, "", "TShirt", 10, 4), [new("", "", "", "")]);
            s.Config.SetCategoryActive(owner, category, false);
            s.Config.AddCategory(owner, "Other stock");
            var shell = new ShellViewModel(s);
            shell.SignedIn(s.Users.Login("owner", "owner-test-1").User!);
            Assert.Equal("Seasonal", new ProductEditViewModel(shell, categorized).Category.Key);
            var editor = new ProductEditViewModel(shell, uncategorized);
            Assert.Equal("", editor.Category.Key);
            editor.SaveCommand.Execute(null);
            Assert.Equal("", s.Styles.Get(uncategorized)!.Input.CategoryKey);
            Assert.Equal("Other stock", new ProductEditViewModel(shell, null).Category.Key);
            return true;
        });
    }

    [Fact]
    public void New_shop_can_open_save_and_reopen_an_uncategorized_product()
    {
        Sta.Run(() =>
        {
            using var dir = new TempFolder();
            var s = AppServices.Create(dir.Combine("empty-catalog"), new FakePrinter());
            var owner = s.Users.Create("owner", "Owner", Role.Owner, "owner-test-1");
            s.Config.ApplyPreset(owner, "general", false);
            s.Settings.ConfigureTax(owner, false);
            var shell = new ShellViewModel(s);
            shell.SignedIn(s.Users.Login("owner", "owner-test-1").User!);
            shell.Nav = "Products";

            Assert.Empty(s.Config.Categories(true));
            Assert.IsType<ProductsViewModel>(shell.Page).AddCommand.Execute(null);
            var editor = Assert.IsType<ProductEditViewModel>(shell.Page);
            Assert.Equal("", editor.Category.Key);
            editor.Name = "First product";
            editor.BasePrice = "12.50";
            editor.CostPrice = "4";
            editor.SaveCommand.Execute(null);

            Assert.False(editor.HasErrors);
            Assert.IsType<ProductsViewModel>(shell.Page);
            var saved = Assert.Single(s.Styles.List());
            Assert.Empty(s.Config.Categories(true));
            shell.OpenProductEditor(saved.Id);
            editor = Assert.IsType<ProductEditViewModel>(shell.Page);
            Assert.Equal("", editor.Category.Key);
            editor.Name = "Updated product";
            editor.SaveCommand.Execute(null);
            Assert.Equal("Updated product", s.Styles.Get(saved.Id)!.Input.NameEn);
            Assert.Equal("", s.Styles.Get(saved.Id)!.Input.CategoryKey);
            return true;
        });
    }
}
