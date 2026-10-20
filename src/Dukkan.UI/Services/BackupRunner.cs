using LocalCashier.Domain;

namespace Dukkan.UI;

/// <summary>Runs the scheduled and shift-close backups. A failure never blocks trading: it becomes a persistent warning.</summary>
public static class BackupRunner
{
    /// <summary>Local package always (when a recovery secret exists); external too when requested and configured.</summary>
    public static void Run(ShellViewModel shell, bool external)
    {
        var s = shell.S;
        var secret = s.Secrets.Load();
        if (secret is not null)
        {
            try { s.Backup.CreatePackage(s.LocalBackupDir, secret, external: false); }
            catch (PosException) { /* recorded by BackupService; shown as a warning */ }
            var dir = s.Settings.GetValue("backup_external_dir");
            if (external && !string.IsNullOrWhiteSpace(dir))
            {
                try { s.Backup.CreatePackage(dir, secret, external: true); }
                catch (PosException) { /* recorded by BackupService; shown as a warning */ }
            }
        }

        shell.RefreshNotifications();
    }

    public static void RunAfterShiftClose(ShellViewModel shell) => Run(shell, external: true);
}
