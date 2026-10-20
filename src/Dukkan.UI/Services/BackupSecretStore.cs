using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace Dukkan.UI;

/// <summary>
/// Keeps the recovery secret for unattended backups, protected with Windows DPAPI for the current user.
/// This copy only enables automatic backups on this PC. Restoring elsewhere always needs the secret the owner
/// wrote down, because the backup key is derived from the secret alone, not from this machine.
/// </summary>
public sealed class BackupSecretStore(string directory)
{
    string FilePath => Path.Combine(directory, "backup.secret");

    public bool Exists => File.Exists(FilePath);

    public void Save(string secret) =>
        File.WriteAllBytes(FilePath, ProtectedData.Protect(Encoding.UTF8.GetBytes(secret), null, DataProtectionScope.CurrentUser));

    public string? Load()
    {
        if (!Exists) return null;
        try { return Encoding.UTF8.GetString(ProtectedData.Unprotect(File.ReadAllBytes(FilePath), null, DataProtectionScope.CurrentUser)); }
        catch (CryptographicException) { return null; }
    }
}
