# Dukkan user guide

Version 1.0.0 · Single-PC Windows retail POS · English and Arabic

## Install, update and uninstall

Download the Windows installer from GitHub Releases and run it while Dukkan is closed. Installation is per Windows account, with no administrator rights or separate .NET installation required. It creates Desktop and Start menu shortcuts and an entry in Windows Installed apps.

On first launch, set your shop details, create an owner account, select how prices include tax, and set a backup recovery secret. Save the account recovery codes and backup secret away from the shop PC. Create separate practice data when trying unfamiliar workflows.

Updates replace program files in `%LocalAppData%\Programs\Dukkan`. Business data stays in `%LocalAppData%\Dukkan`. Database upgrades take verified snapshots first. Older binaries cannot open newer schemas; version 1.0.0 uses schema 16. Uninstall through Windows Installed apps; keep a verified external backup before replacing a PC or removing data.

Version 1.0.0 starts the public release numbering. The installer accepts an upgrade from the earlier private 1.4.2 and 1.4.3 builds, which use the same database schema. Other downgrades remain blocked. Keep an external backup before moving from a private build.

## Daily checkout

1. Sign in, open a shift and enter the counted opening cash.
2. Scan a barcode or select a product and its size/color. F3 focuses the scan box.
3. Apply any permitted discount, or hold the cart with F6 to resume it later.
4. Press Pay or F5. Choose a payment method, or split the total across methods. Change is given against cash only.
5. Print a receipt, A4 invoice or PDF. If printing fails, the saved sale can be reopened and reprinted.
6. Close the shift by counting cash before reviewing the expected balance. Explain any difference and save the daily summary.

Visa, Instapay and wallet methods record payments confirmed outside Dukkan. They do not charge a card or initiate a transfer. Card-terminal confirmation is manual.

Returns are recorded against invoices with quantities and resalable status. Review the stock effect, debt reduction and any refund before posting. Posted transactions are corrected through reversals, not by editing history. Owner approval is required for restricted actions.

## Products and inventory

Products can have categories, images, sizes, colors, prices, costs and tax settings. Each size/color variant has a SKU and barcode. Print product labels on supported label media or an A4 sheet.

Use owner-only CSV import for product/opening-stock and customer/opening-balance setup. Download the template, review row errors and supply a unique migration reference. Imports are all-or-nothing and do not overwrite existing records. Closing onboarding permanently blocks further opening-stock/balance imports.

Receive a supplier delivery through a purchase invoice, including any batch/expiry details. Receiving against a purchase order supports partial delivery. Review quantity, cost, payment and amount owed before posting each delivery once.

Inventory controls support locations, paired transfers and stock counts. Record the physical observation, review variances and have an owner approve the count. Expiry tracking is enabled per product; tracked sales consume eligible lots in expiry order. Expired-stock exceptions require owner approval and a reason. Reorder suggestions can create draft purchase orders and never place orders automatically.

## Customers, suppliers and offers

Customer accounts include invoices, installments, collections, statements and loyalty. Credit requires a named customer with credit enabled; the payment dialog shows the credit effect. Installment schedules must equal the unpaid balance. Collections settle existing debt and do not create a second sale. Returns reduce unpaid invoice debt before refunding any excess.

Supplier accounts include orders, purchases, returns, statements and allocated payments. Returns reduce stock and the amount owed; unused supplier credit can settle later purchases. Use reversal actions with a reason for corrections.

Owners configure enabled payment methods, promotions, coupons, customer groups and loyalty settings. Review the cart's offer explanation and redemption totals before payment. Loyalty uses an auditable points ledger; returns reverse the corresponding benefits.

## Reports and settings

Sales, shifts and Business reports cover profit, sellers, current inventory valuation, dead stock, tax, payment methods and daily summaries. Apply date/cashier filters where offered. CSV exports use localized headers. Stock valuation is current stock, not a historical reconstruction. Historical product/category labels use the current catalog. Whole-history reports may take longer than short date ranges.

Settings includes shop details, users/roles, printers, payment methods, offers/loyalty, backups and activity. Cashiers cannot access owner-only cost, profit and administrative operations. F11 toggles full screen; startup/full-screen preferences are configurable.

## Back up and recover

Configure an external backup folder and use Back up now. Automatic hourly backups run while Dukkan is open; plug in the backup drive regularly. Backups are encrypted and require the recovery secret to restore. There is no vendor reset for a lost secret.

Restore from Settings > Backup, choose the package and enter the secret. Existing data is saved aside first and the application closes. Reconcile stock, cash and transactions made after the restored backup. Practise restoring on a spare PC before relying on backups.

For a forgotten owner password, use Forgot password with the owner username and an unused recovery code. Each recovery code works once; save its replacement. For update/startup trouble, retain the pre-migration snapshot in the data folder and use a compatible version with the matching database.

## Hardware and deployment checks

- Put the scanner in keyboard/HID mode with Enter as the suffix. Check a known and unknown barcode.
- Install the printer's Windows driver. Test an 80 mm receipt, Arabic text, a label and an A4 document on actual hardware.
- Confirm printed label barcodes scan back into the POS.
- Test a printer disconnect: the sale must remain saved and reprintable.
- Restore an encrypted backup on a spare PC and reconcile totals.
- Run a five-day shop pilot and check stock/cash differences and daily backups.

Printing uses Windows drivers. A successful spool submission does not confirm paper output; inspect jams and uncertain jobs. Dukkan does not automatically kick the cash drawer, synchronize devices or submit ETA fiscal e-receipts. The installer is unsigned. Physical hardware and recovery acceptance are operator responsibilities.

If a problem occurs, report the version shown on the home screen, the exact steps and relevant Windows/printer details. Remove customer records, shop data and secrets from attachments.
