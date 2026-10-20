using System.IO;
using System.Globalization;
using LocalCashier.Domain;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace Dukkan.UI;

public partial class MainWindow : Window
{
    readonly ShellViewModel _vm = new(App.Services);
    readonly DispatcherTimer _idle = new() { Interval = TimeSpan.FromSeconds(20) };
    readonly DispatcherTimer _backup = new() { Interval = TimeSpan.FromHours(1) };
    DateTime _lastInput = DateTime.UtcNow;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _vm;
        if (App.Services.Settings.GetValue("start_fullscreen") == "1") SetFullScreen(true);
        Loaded += (_, _) =>
        {
#if DEBUG
            // Debug-only capture switches use isolated test data; see docs/DEVELOPMENT.md.
            var args = Environment.GetCommandLineArgs().Skip(1).ToList();
            foreach (var a in args) Apply(a);
            if (args.FirstOrDefault(a => a.StartsWith("--shot=")) is { } shot) CaptureAfterDelay(shot["--shot=".Length..], args);
#endif
        };
        _vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ShellViewModel.Dialog) && _vm.HasDialog)
                Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
                {
                    // Focus the first visible input so keyboard users can type immediately; otherwise the first control.
                    if (FindFirst<TextBox>(DialogHost) is { } input) { input.Focus(); input.SelectAll(); }
                    else DialogHost.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
                });
        };

        // Inactivity lock (setting "lock_minutes", default 10): keeps the cart and the open shift, asks for a sign-in again.
        PreviewMouseMove += (_, _) => _lastInput = DateTime.UtcNow;
        PreviewKeyDown += (_, _) => _lastInput = DateTime.UtcNow;
        PreviewMouseDown += (_, _) => _lastInput = DateTime.UtcNow;
        _idle.Tick += (_, _) =>
        {
            if (!_vm.IsMainMode) return;
            var minutes = int.TryParse(App.Services.Settings.GetValue("lock_minutes"), out var m) && m > 0 ? m : 10;
            if (DateTime.UtcNow - _lastInput > TimeSpan.FromMinutes(minutes)) _vm.LockCommand.Execute(null);
        };
        _idle.Start();

        // Hourly local backup while the app runs (needs the recovery secret saved on this PC).
        _backup.Tick += (_, _) => { if (_vm.IsMainMode && App.Services.Secrets.Exists) BackupRunner.Run(_vm, external: false); };
        _backup.Start();
    }
#if DEBUG

    void Apply(string arg)
    {
        var s = App.Services;
        if (arg == "--lang=ar") _vm.SetArabic(true);
        else if (arg == "--lang=en") _vm.SetArabic(false);
        else if (arg == "--fullscreen") SetFullScreen(true);
        else if (arg.StartsWith("--login=") && arg["--login=".Length..].Split(':') is [var u, var p])
        {
            if (s.Users.Login(u, p) is { User: { } user }) _vm.SignedIn(user);
        }
        else if (arg == "--open-shift" && _vm.User is { } cashier && _vm.Shift is null)
        {
            s.Shifts.Open(cashier.Id, 500m);
            _vm.Refresh();
        }
        else if (arg.StartsWith("--page=")) _vm.Nav = arg["--page=".Length..];
        else if (arg == "--dialog=payment" && _vm.Page is PosViewModel pos)
        {
            foreach (var st in pos.Products.Take(2)) pos.AddStyleCommand.Execute(st);
            if (_vm.HasDialog) _vm.CloseDialog();
            if (pos.Cart.Count == 0 && pos.Products.Count > 0 && pos.VariantsOf(pos.Products[0]).FirstOrDefault(v => v.Stock > 0) is { } v) pos.AddVariant(v);
            pos.PayCommand.Execute(null);
        }
        else if (arg is "--dialog=credit" or "--dialog=installments" && _vm.Page is PosViewModel cp)
        {
            foreach (var st in cp.Products.Take(4))
                if (cp.VariantsOf(st).FirstOrDefault(v => v.Stock > 0) is { } v) cp.AddVariant(v);
            cp.PayCommand.Execute(null);
            if (_vm.Dialog is PaymentViewModel pv && pv.Customers.FirstOrDefault(x => x.CreditEnabled) is { } cu)
            {
                pv.SelectedCustomer = cu;
                pv.Method = "Credit";
                pv.CashText = "500";
                pv.Credit.Installments = arg == "--dialog=installments";
            }
        }
        else if (arg == "--supplier-details" && _vm.Page is SuppliersViewModel sv && sv.Pager.Items.Count > 0) sv.OpenCommand.Execute(sv.Pager.Items.First(x => x.Balance > 0));
        else if (arg == "--dialog=purchase" && _vm.Page is SuppliersViewModel sp && sp.Details is { } sd) sd.PurchaseCommand.Execute(null);
        else if (arg == "--dialog=supplierpay" && _vm.Page is SuppliersViewModel sq && sq.Details is { } se) se.PayCommand.Execute(null);
        else if (arg.StartsWith("--supplier-v2=")) SupplierV2Shot(arg["--supplier-v2=".Length..]);
        else if(arg.StartsWith("--stock-v2=")) StockV2Shot(arg["--stock-v2=".Length..]);
        else if(arg.StartsWith("--payment-v2=")) PaymentV2Shot(arg["--payment-v2=".Length..]);
        else if(arg.StartsWith("--promotion-v2=")) PromotionV2Shot(arg["--promotion-v2=".Length..]);
        else if(arg.StartsWith("--report-v2=")) ReportV2Shot(arg["--report-v2=".Length..]);
        else if(arg=="--label-dialog") _vm.ShowDialog(new LabelPrintViewModel(_vm,[s.Styles.List().First().Id]));
        else if (arg == "--dialog=import" && _vm.Page is ProductsViewModel ip) ip.ImportCommand.Execute(null);
        else if (arg.StartsWith("--preset=") && _vm.User is { } po)
        {
            s.Config.ApplyPreset(po.Id, arg["--preset=".Length..], Localizer.Instance.IsArabic);
            CategoryNames.Refresh(s.Config.Categories(true));

        }
        else if (arg == "--fill-cart" && _vm.Page is PosViewModel fill)
        {
            foreach (var st in fill.Products.Take(4))
                if (fill.VariantsOf(st).FirstOrDefault(v => v.Stock > 0) is { } v) fill.AddVariant(v);
        }
        else if (arg == "--edit-product" && s.Styles.List().FirstOrDefault() is { } first) _vm.OpenProductEditor(first.Id);
        else if (arg == "--edit-new") _vm.OpenProductEditor(null);
        else if (arg.StartsWith("--size=") && arg["--size=".Length..].Split('x') is [var w, var h]) { Width = double.Parse(w); Height = double.Parse(h); }
        else if (arg == "--customer-details" && _vm.Page is CustomersViewModel c && c.Pager.Items.Count > 0) c.OpenCommand.Execute(c.Pager.Items.FirstOrDefault(x => x.HasOutstanding) ?? c.Pager.Items[0]);
        else if (arg == "--empty-search" && _vm.Page is ProductsViewModel pr) pr.Search = "zzz";
        else if (arg == "--validate" && _vm.Page is ProductEditViewModel ed) ed.SaveCommand.Execute(null);
        else if (arg.StartsWith("--tab=") && int.TryParse(arg["--tab=".Length..], out var tab))
        {
            switch (_vm.Page)
            {
                case ReportsViewModel rp: rp.Tab = tab; break;
                case ProductEditViewModel pe: pe.Tab = tab; break;
                case SettingsViewModel st: st.Tab = tab; break;
                case CustomersViewModel { Details: { } cd }: cd.Tab = tab; break;
                case InventoryViewModel: break;
                case StockWorkspaceViewModel stock: stock.Tab=tab; break;
            }
        }
        else if (arg == "--invoice-dialog" && _vm.Page is ReportsViewModel rv && rv.Pager.Items.Count > 0) rv.ViewInvoiceCommand.Execute(rv.Pager.Items[0]);
        else if (arg == "--close-shift") _vm.CloseShiftCommand.Execute(null);
        else if (arg == "--open-shift-dialog") _vm.OpenShiftCommand.Execute(null);
        else if (arg == "--variant-picker" && _vm.Page is PosViewModel vp) vp.AddStyleCommand.Execute(vp.Products.First(x => x.VariantCount > 1));
        else if (arg == "--notifications") { _vm.RefreshNotifications(); _vm.NotificationsOpen = true; }
        else if (arg == "--toast") _vm.Toast(Localizer.Instance["Toast_ProductSaved"]);
    }

    void ReportV2Shot(string screen)
    {
        if(screen=="Dead")
        {
            var s=App.Services;var owner=_vm.User!.Id;var p=s.Catalog.AddProduct(owner,"QA-UNSOLD","QA-UNSOLD","Unsold sample","صنف لم يُبع",20);s.Inventory.Receive(owner,p,5,12);
        }
        _vm.Nav="Reports";var reports=new ReportsViewModel(_vm){Tab=4};_vm.ShowPage(reports);
        if(screen=="daily")reports.Smart.DailyCommand.Execute(null);
        else reports.Smart.Selected=reports.Smart.Options.Single(o=>o.Key=="Smart_"+screen);
        _vm.ToastVisible=false;
    }

    void PromotionV2Shot(string screen)
    {
        var s=App.Services;var owner=_vm.User!.Id;var loyalty=new LocalCashier.Persistence.LoyaltyService(s.Db,s.Clock);loyalty.Configure(owner,new(true,1,10,10,365));
        var memberName="QA Member "+Guid.NewGuid().ToString("N")[..5];
        var customer=s.Customers.Add(owner,memberName,"01012345678",creditEnabled:true,creditLimit:5000);loyalty.Adjust(Guid.NewGuid(),owner,customer,200,"QA opening points");
        var promotions=new LocalCashier.Persistence.PromotionService(s.Db,s.Clock);promotions.SetCustomerGroup(Guid.NewGuid(),owner,customer,"VIP");
        var pos=new PosViewModel(_vm);foreach(var style in pos.Products.Take(2))if(pos.VariantsOf(style).FirstOrDefault(v=>v.Stock>0) is {} variant)pos.AddVariant(variant);
        var products=pos.Cart.Select(c=>c.Variant.ProductId).ToList();
        var id=promotions.Save(Guid.NewGuid(),owner,new(0,"Welcome member · 10%","ترحيب بالعميل · ١٠٪",PromotionKind.Percentage){Percent=10,Coupon="WELCOME",CustomerGroup="VIP",Priority=10,Stackable=true});
        var bundle=promotions.Save(Guid.NewGuid(),owner,new(0,"Weekend bundle","باقة نهاية الأسبوع",PromotionKind.Bundle){AmountCents=50000,Enabled=false,Bundle=products.Select(p=>new BundleRequirement(p,1)).ToList(),ProductIds=products});
        var buy=promotions.Save(Guid.NewGuid(),owner,new(0,"Buy 2 get 1","اشترِ ٢ واحصل على ١",PromotionKind.BuyGet){Percent=100,Enabled=false,BuyQuantity=2,GetQuantity=1});
        if(screen is "list" or "config")
        {
            _vm.Nav="Settings";var settings=new SettingsViewModel(_vm){Tab=8};settings.Promotions.Tab=screen=="config"?1:0;_vm.ShowPage(settings);
        }
        else if(screen is "editor" or "bundle" or "buyget")
        {
            _vm.Nav="Settings";_vm.ShowPage(new SettingsViewModel(_vm){Tab=8});
            _vm.ShowDialog(new PromotionEditViewModel(_vm,promotions.List().Single(p=>p.Id==(screen=="bundle"?bundle:screen=="buyget"?buy:id)),()=>{}));
        }
        else
        {
            pos.SelectedCustomer=pos.Customers.Single(c=>c.Id==customer);pos.Coupon="WELCOME";_vm.Nav="Pos";_vm.ShowPage(pos);
            if(screen is "redeem" or "receipt" or "customer")
            {
                var payment=new PaymentViewModel(_vm,pos);payment.RedeemPointsText="50";_vm.ShowDialog(payment);
                if(screen is "receipt" or "customer")
                {
                    s.Settings.SetValue(owner,"receipt_language",Localizer.Instance.IsArabic?"ar":"en");payment.CompleteCommand.Execute(null);
                    if(_vm.Dialog is not SaleCompleteViewModel)throw new InvalidOperationException("QA checkout did not reach its receipt.");
                    if(screen=="customer")
                    {
                        _vm.CloseDialog();_vm.Nav="Customers";if(_vm.Page is CustomersViewModel customers){customers.Search=memberName;customers.OpenCommand.Execute(customers.Pager.Items.Single(c=>c.Id==customer));if(customers.Details is {} details)details.Tab=4;}
                    }
                }
            }
        }
        _vm.ToastVisible=false;
    }

    void PaymentV2Shot(string screen)
    {
        var s=App.Services;var owner=_vm.User!.Id;
        var wallet=s.PaymentMethods.List(false).First(m=>m.Code=="vodafone_cash");s.PaymentMethods.Update(owner,wallet.Id,wallet.NameEn,wallet.NameAr,true,wallet.SortOrder);
        if(screen=="settings"){_vm.Nav="Settings";if(_vm.Page is SettingsViewModel settings)settings.Tab=7;_vm.ToastVisible=false;return;}
        var pos=new PosViewModel(_vm);
        foreach(var style in pos.Products.Take(2))if(pos.VariantsOf(style).FirstOrDefault(v=>v.Stock>0) is {} variant)pos.AddVariant(variant);
        _vm.ShowPage(pos);var payment=new PaymentViewModel(_vm,pos){IsSplit=true};payment.PaymentLines.Clear();
        payment.AddPaymentCommand.Execute(null);payment.PaymentLines[0].Amount=Money.Round(pos.Total*0.3m).ToString(CultureInfo.InvariantCulture);
        payment.AddPaymentCommand.Execute(null);payment.PaymentLines[1].Method=payment.TenderMethods.Single(m=>m.Key=="Card");payment.PaymentLines[1].Amount=Money.Round(pos.Total*0.4m).ToString(CultureInfo.InvariantCulture);
        payment.PaymentLines[1].ApprovalCode="QA-APPROVED";payment.PaymentLines[1].Last4="1234";payment.PaymentLines[1].ConfirmedOnTerminal=true;
        payment.AddPaymentCommand.Execute(null);payment.PaymentLines[2].Method=payment.TenderMethods.Single(m=>m.Id==wallet.Id);payment.PaymentLines[2].Amount=(pos.Total-payment.PaymentLines[0].Value-payment.PaymentLines[1].Value).ToString(CultureInfo.InvariantCulture);payment.PaymentLines[2].Reference="QA-WALLET-123";
        _vm.ShowDialog(payment);
        if(screen=="card"){payment.IsSplit=false;payment.Method="Card";payment.ElectronicText=pos.Total.ToString(CultureInfo.InvariantCulture);payment.ApprovalCode="QA-APPROVED";payment.Last4="1234";payment.ConfirmedOnTerminal=true;}
        else if(screen=="credit")
        {
            payment.PaymentLines.RemoveAt(2);payment.SelectedCustomer=payment.Customers.First(c=>c.CreditEnabled);payment.Credit.Installments=true;
        }
        else if(screen is "receipt" or "refund" or "report" or "shift")
        {
            payment.CompleteCommand.Execute(null);
            if(screen=="refund")
            {
                var row=s.Queries.Invoices(s.Queries.Today,s.Queries.Today).OrderByDescending(i=>i.Id).First();var returned=new ReturnViewModel(_vm,new InvoiceItem(row));
                foreach(var line in returned.Rows)line.Qty="1";returned.Reason="QA split refund";_vm.ShowDialog(returned);
            }
            else if(screen=="report"){_vm.CloseDialog();_vm.Nav="Reports";var report=new ReportsViewModel(_vm){Tab=3};_vm.ShowPage(report);}
            else if(screen=="shift"){_vm.CloseDialog();_vm.CloseShiftCommand.Execute(null);if(_vm.Dialog is CloseShiftViewModel close){close.Counted=s.Shifts.ExpectedCash(_vm.Shift!.Id).ToString(CultureInfo.InvariantCulture);close.CountCommand.Execute(null);}}
        }
        _vm.ToastVisible=false;
    }

    void StockV2Shot(string screen)
    {
        var s=App.Services;var owner=_vm.User!.Id;
        var product=s.Catalog.ListStock().First(p=>p.OnHand>5);
        var supplier=s.Suppliers.List().First();
        var back=s.Stock.Locations().FirstOrDefault(l=>l.Name=="Back stock")?.Id??s.Stock.CreateLocation(owner,"Back stock");
        var expiry=DateOnly.Parse(s.Queries.Today).AddDays(20).ToString("yyyy-MM-dd");
        s.Stock.ConfigureProduct(owner,product.ProductId,true,100,150,supplier.Id,"OPEN-2026",expiry);
        if(s.Stock.OnHand(product.ProductId,back)==0)s.Stock.Transfer(Guid.NewGuid(),owner,product.ProductId,1,back,2);
        if(s.Stock.CountSessions().Count==0)
        {
            var count=s.Stock.CreateCount(Guid.NewGuid(),owner,1,[product.ProductId]);
            var line=s.Stock.CountLines(count).Single();s.Stock.RecordCount(Guid.NewGuid(),owner,line.Id,Math.Max(0,line.Expected-1));
        }
        var vm=new StockWorkspaceViewModel(_vm);_vm.ShowPage(vm);
        vm.Product=vm.Products.First(p=>p.Id==product.ProductId);vm.Destination=vm.Locations.First(l=>l.Id==back);
        vm.Lot=vm.Lots.First();vm.Quantity="1.5";vm.Batch="DELIVERY-02";vm.Expiry=expiry;vm.Reason="Stock checked";
        vm.Tab=screen switch {"levels"=>1,"transfer"=>2,"rules"=>3,"expiry"=>4,"reorder"=>5,"locations"=>6,_=>0};
        if(screen=="receive")
        {
            var invoice=new PurchaseInvoiceViewModel(_vm,new SupplierItem(s.Suppliers.Get(supplier.Id)!),()=>{});
            invoice.PickCommand.Execute(SupplierVariantLookup.Get(_vm,product.ProductId));invoice.Lines[0].QtyText="5";invoice.Lines[0].Batch="DELIVERY-02";invoice.Lines[0].Expiry=expiry;
            _vm.ShowDialog(invoice);
        }
        else if(screen=="reverse")
        {
            var oldProduct=s.Catalog.ListStock().First(p=>p.ProductId!=product.ProductId && p.OnHand>2 && !s.Stock.ProductConfig(p.ProductId).TrackExpiry);
            var returned=s.Suppliers.PostReturn(new(Guid.NewGuid(),owner,supplier.Id,s.Queries.Today,"other","QA historical return",null,[new(oldProduct.ProductId,1,oldProduct.AvgCost)]));
            s.Stock.ConfigureProduct(owner,oldProduct.ProductId,true,0,0,null,"OPEN-OLD",expiry);
            _vm.ShowDialog(new SupplierReturnReverseViewModel(_vm,returned.Id,returned.Number,()=>{}));
        }
        else if(screen=="supplierreturn")
        {
            var received=s.Suppliers.PostPurchase(new(Guid.NewGuid(),owner,supplier.Id,"QA",s.Queries.Today,null,[new(product.ProductId,2,product.AvgCost,Batch:"QA-RETURN",Expiry:expiry)]));
            var returned=new SupplierReturnViewModel(_vm,new SupplierItem(s.Suppliers.Get(supplier.Id)!),()=>{});
            returned.Invoice=returned.Invoices.Single(i=>i.Id==received.InvoiceId);returned.Lines[0].Quantity="1";_vm.ShowDialog(returned);
        }
        else if(screen is "expiredsale" or "customerreturn")
        {
            var id=product.ProductId;
            if(screen=="expiredsale")
            {
                id=s.Catalog.ListStock().First(p=>p.ProductId!=product.ProductId && p.OnHand>2 && !s.Stock.ProductConfig(p.ProductId).TrackExpiry).ProductId;
                s.Stock.ConfigureProduct(owner,id,true,0,0,null,"EXPIRED-QA",DateOnly.Parse(s.Queries.Today).AddDays(-1).ToString("yyyy-MM-dd"));
            }
            var pos=new PosViewModel(_vm);pos.AddVariant(SupplierVariantLookup.Get(_vm,id)!);_vm.ShowPage(pos);
            var payment=new PaymentViewModel(_vm,pos);_vm.ShowDialog(payment);payment.CompleteCommand.Execute(null);
            if(screen=="customerreturn")
            {
                var row=s.Queries.Invoices(s.Queries.Today,s.Queries.Today).OrderByDescending(i=>i.Id).First();
                var returned=new ReturnViewModel(_vm,new InvoiceItem(row));returned.Rows[0].Qty="1";_vm.ShowDialog(returned);
            }
        }
        else if(screen=="cashier")
        {
            _vm.SignedIn(s.Users.Login("mona","cashier-demo-1").User!);_vm.Nav="Inventory";
        }
        else if(screen=="countopen")vm.CreateCountCommand.Execute(null);
        _vm.ToastVisible=false;
    }

    void SupplierV2Shot(string screen)
    {
        var s = App.Services;
        var owner = _vm.User!.Id;
        var supplier = s.Suppliers.List().First();
        var product = s.Catalog.ListStock().First(p => p.OnHand > 0);
        var orders = s.Suppliers.OrderList(supplier.Id);
        if (orders.Count == 0)
        {
            var id = s.Suppliers.CreateOrder(Guid.NewGuid(), owner, supplier.Id,
                [new(product.ProductId, 12, product.AvgCost)], s.Queries.Today, "QA purchase order");
            s.Suppliers.TransitionOrder(Guid.NewGuid(), owner, id, "open");
            var line = s.Suppliers.OrderLines(id).Single();
            var receipt = s.Suppliers.PostPurchase(new(Guid.NewGuid(), owner, supplier.Id, "QA delivery", s.Queries.Today, null,
                [new(product.ProductId, 3, product.AvgCost, OrderLineId: line.Id)], OrderId: id));
            var original = s.Suppliers.PurchaseLines(receipt.InvoiceId).Single();
            s.Suppliers.PostReturn(new(Guid.NewGuid(), owner, supplier.Id, s.Queries.Today, "damaged", "QA return", receipt.InvoiceId,
                [new(product.ProductId, 1, original.UnitCost, original.Id)]));
        }
        var row = s.Suppliers.OrderList(supplier.Id).First();
        var item = new SupplierItem(s.Suppliers.Get(supplier.Id)!);
        if (screen == "order") _vm.ShowDialog(new PurchaseOrderDetailsViewModel(_vm, row, () => { }));
        else if (screen == "receive") _vm.ShowDialog(new PurchaseInvoiceViewModel(_vm, item, () => { }, order: row));
        else if (screen == "edit")
        {
            var editor = new PurchaseInvoiceViewModel(_vm, item, () => { }, orderMode: true) { DueDate = s.Queries.Today, Notes = "QA purchase order" };
            if (SupplierVariantLookup.Get(_vm, product.ProductId) is { } variant) editor.PickCommand.Execute(variant);
            _vm.ShowDialog(editor);
        }
        else if (screen == "return")
        {
            var editor = new SupplierReturnViewModel(_vm, item, () => { });
            if (editor.Lines.Count > 0) editor.Lines[0].Quantity = "1";
            _vm.ShowDialog(editor);
        }
        else _vm.ShowDialog(new SupplierOperationsViewModel(_vm, supplier.Id) { Tab = screen switch { "returns" => 1, "trading" => 2, "statement" => 3, _ => 0 } });
    }

    /// <summary>Debug helper: renders the window content to a PNG and exits.</summary>
    void CaptureAfterDelay(string path, List<string> args)
    {
        var delay = int.TryParse(args.FirstOrDefault(a => a.StartsWith("--delay="))?["--delay=".Length..], out var d) ? d : 1600;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(delay) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            UpdateLayout();
            if(args.Contains("--scroll-end") && FindFirst<ScrollViewer>(DialogHost) is {} scroll){scroll.ScrollToEnd();UpdateLayout();}
            if(args.Contains("--page-scroll-end") && FindFirst<ScrollViewer>(PageHost) is {} pageScroll){pageScroll.ScrollToEnd();UpdateLayout();}
            var root = (FrameworkElement)Content;
            var dpi = VisualTreeHelper.GetDpi(root);
            var w = (int)Math.Round(root.ActualWidth * dpi.DpiScaleX);
            var h = (int)Math.Round(root.ActualHeight * dpi.DpiScaleY);
            var bmp = new System.Windows.Media.Imaging.RenderTargetBitmap(w, h, dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
            var back = new DrawingVisual();
            using (var dc = back.RenderOpen()) dc.DrawRectangle(Background, null, new Rect(0, 0, root.ActualWidth, root.ActualHeight));
            bmp.Render(back);
            if (root.FlowDirection == FlowDirection.RightToLeft)
            {
                // RenderTargetBitmap draws an RTL element mirrored; mirror it back so the file matches the on-screen window.
                var flipped = new DrawingVisual();
                using (var dc = flipped.RenderOpen())
                {
                    dc.PushTransform(new ScaleTransform(-1, 1, root.ActualWidth / 2, 0));
                    var brush = new VisualBrush(root)
                    {
                        Stretch = Stretch.Fill, ViewboxUnits = BrushMappingMode.Absolute, Viewbox = new Rect(0, 0, root.ActualWidth, root.ActualHeight),
                        ViewportUnits = BrushMappingMode.Absolute, Viewport = new Rect(0, 0, root.ActualWidth, root.ActualHeight),
                    };
                    dc.DrawRectangle(brush, null, new Rect(0, 0, root.ActualWidth, root.ActualHeight));
                }

                bmp.Render(flipped);
            }
            else bmp.Render(root);

            var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
            enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using (var fs = File.Create(path)) enc.Save(fs);
            Application.Current.Shutdown();
        };
        timer.Start();
    }
#endif

    public bool IsFullScreen { get; private set; }

    /// <summary>Borderless maximized window that also covers the taskbar; off restores the normal maximized window.</summary>
    public void SetFullScreen(bool on)
    {
        IsFullScreen = on;
        if (on)
        {
            WindowState = WindowState.Normal; // re-maximize so the borderless window covers the taskbar
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            WindowState = WindowState.Maximized;
        }
        else
        {
            WindowStyle = WindowStyle.SingleBorderWindow;
            ResizeMode = ResizeMode.CanResize;
            WindowState = WindowState.Maximized;
        }
    }

    void CloseUserMenu(object sender, RoutedEventArgs e) => UserMenuToggle.IsChecked = false;

    void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.F11:
                SetFullScreen(!IsFullScreen); e.Handled = true; break;
            case Key.Escape when _vm.HasDialog:
                _vm.CloseDialog(); e.Handled = true; break;
            case Key.Escape when _vm.NotificationsOpen:
                _vm.NotificationsOpen = false; e.Handled = true; break;
            case Key.F5 when _vm.Dialog is PaymentViewModel pay && pay.CompleteCommand.CanExecute(null):
                pay.CompleteCommand.Execute(null); e.Handled = true; break;
            case Key.F5 when _vm.Page is PosViewModel pos && !_vm.HasDialog && pos.PayCommand.CanExecute(null):
                pos.PayCommand.Execute(null); e.Handled = true; break;
            case Key.F6 when _vm.Page is PosViewModel pos && !_vm.HasDialog && pos.HoldCommand.CanExecute(null):
                pos.HoldCommand.Execute(null); e.Handled = true; break;
            case Key.F3 when _vm.Page is PosViewModel && !_vm.HasDialog:
                if (FindDescendant<TextBox>(this, "PosSearch") is { } box) { box.Focus(); box.SelectAll(); }
                e.Handled = true; break;
        }
    }

    static T? FindFirst<T>(DependencyObject root) where T : UIElement
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T { IsVisible: true, IsEnabled: true, Focusable: true } t) return t;
            if (FindFirst<T>(child) is { } found) return found;
        }

        return null;
    }

    static T? FindDescendant<T>(DependencyObject root, string name) where T : FrameworkElement
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T fe && fe.Name == name) return fe;
            if (FindDescendant<T>(child, name) is { } found) return found;
        }

        return null;
    }
}
