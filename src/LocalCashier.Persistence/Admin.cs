using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using LocalCashier.Application;
using LocalCashier.Domain;
using Microsoft.Data.Sqlite;

namespace LocalCashier.Persistence;

public enum AuthFailure { None, BadCredentials, Locked }

public sealed record AuthResult(UserInfo? User, AuthFailure Failure, int AttemptsLeft = 0, DateTimeOffset? LockedUntil = null);

public sealed record UserRow(long Id, string Username, string DisplayName, Role Role, bool Active, bool Locked);

public sealed partial class UserService
{
    public const int MaxFailedAttempts = 5;
    public static readonly TimeSpan LockDuration = TimeSpan.FromMinutes(5);
    public const int MinPasswordLength = 8;

    static void RequireStrong(string password)
    {
        if (password is null || password.Length < MinPasswordLength)
            throw new PosException(PosError.WeakPassword, $"The password must be at least {MinPasswordLength} characters.");
    }

    /// <summary>
    /// Sign-in with rate limiting: five consecutive failures lock the account for five minutes. The counter is stored, so
    /// restarting the application does not reset it.
    /// </summary>
    public AuthResult Login(string username, string password)
    {
        using var c = db.Open();
        return Db.InTransaction(c, () =>
        {
            var row = Sql.Query(c,
                "SELECT id,username,display_name,role,password_hash,active,failed_attempts,locked_until FROM users WHERE username=@u",
                x => (Id: x.GetInt64(0), Name: x.GetString(1), Display: x.GetString(2), Role: x.GetString(3), Hash: x.GetString(4), Active: x.GetInt64(5) == 1,
                      Failed: x.GetInt64(6), Until: x.IsDBNull(7) ? null : x.GetString(7)), ("@u", username)).FirstOrDefault();
            if (row.Name is null || !row.Active) return new AuthResult(null, AuthFailure.BadCredentials);

            if (row.Until is not null && DateTimeOffset.Parse(row.Until, CultureInfo.InvariantCulture) > clock.Now)
                return new AuthResult(null, AuthFailure.Locked, 0, DateTimeOffset.Parse(row.Until, CultureInfo.InvariantCulture));

            if (PasswordHasher.Verify(password, row.Hash))
            {
                Sql.Exec(c, "UPDATE users SET failed_attempts=0, locked_until=NULL WHERE id=@i", ("@i", row.Id));
                return new AuthResult(new UserInfo(row.Id, row.Name, row.Display, row.Role == "owner" ? Role.Owner : Role.Cashier), AuthFailure.None);
            }

            var failed = row.Failed + 1;
            if (failed >= MaxFailedAttempts)
            {
                var until = clock.Now + LockDuration;
                Sql.Exec(c, "UPDATE users SET failed_attempts=0, locked_until=@t WHERE id=@i", ("@t", until.ToString("o")), ("@i", row.Id));
                Common.Audit(c, clock, row.Id, null, "auth.locked", row.Name);
                return new AuthResult(null, AuthFailure.Locked, 0, until);
            }

            Sql.Exec(c, "UPDATE users SET failed_attempts=@f WHERE id=@i", ("@f", failed), ("@i", row.Id));
            return new AuthResult(null, AuthFailure.BadCredentials, (int)(MaxFailedAttempts - failed));
        });
    }

    /// <summary>Owner approval for one action: succeeds only for an active owner's valid credentials (and counts failures like a sign-in).</summary>
    public long? ApproveAsOwner(string username, string password) =>
        Login(username, password) is { User.Role: Role.Owner } ok ? ok.User.Id : null;

    public IReadOnlyList<UserRow> List()
    {
        using var c = db.Open();
        var now = clock.Now;
        return Sql.Query(c, "SELECT id,username,display_name,role,active,locked_until FROM users ORDER BY role DESC, display_name",
            x => new UserRow(x.GetInt64(0), x.GetString(1), x.GetString(2), x.GetString(3) == "owner" ? Role.Owner : Role.Cashier, x.GetInt64(4) == 1,
                !x.IsDBNull(5) && DateTimeOffset.Parse(x.GetString(5), CultureInfo.InvariantCulture) > now));
    }

    public long CreateByOwner(long ownerId, string username, string displayName, Role role, string password)
    {
        RequireStrong(password);
        using var c = db.Open();
        return Db.InTransaction(c, () =>
        {
            Common.RequireOwner(c, ownerId);
            if (string.IsNullOrWhiteSpace(username)) throw new PosException(PosError.InvalidCredentials, "A username is required.");
            if (Sql.Scalar<long>(c, "SELECT COUNT(*) FROM users WHERE username=@u", ("@u", username)) > 0)
                throw new PosException(PosError.Duplicate, "That username is already used.");
            var id = Sql.Insert(c, "INSERT INTO users(username,display_name,role,password_hash) VALUES(@u,@d,@r,@h)",
                ("@u", username.Trim()), ("@d", displayName.Trim()), ("@r", role == Role.Owner ? "owner" : "cashier"), ("@h", PasswordHasher.Hash(password)));
            Common.Audit(c, clock, ownerId, null, "user.create", username);
            return id;
        });
    }

    public void SetActive(long ownerId, long userId, bool active)
    {
        using var c = db.Open();
        Db.InTransaction(c, () =>
        {
            Common.RequireOwner(c, ownerId);
            if (!active)
            {
                if (userId == ownerId) throw new PosException(PosError.PermissionDenied, "You cannot deactivate your own account.");
                var role = Sql.Scalar<string>(c, "SELECT role FROM users WHERE id=@i", ("@i", userId));
                if (role == "owner" && Sql.Scalar<long>(c, "SELECT COUNT(*) FROM users WHERE role='owner' AND active=1 AND id<>@i", ("@i", userId)) == 0)
                    throw new PosException(PosError.PermissionDenied, "At least one active owner is required.");
            }

            Sql.Exec(c, "UPDATE users SET active=@a, failed_attempts=0, locked_until=NULL WHERE id=@i", ("@a", active ? 1 : 0), ("@i", userId));
            Common.Audit(c, clock, ownerId, null, active ? "user.activate" : "user.deactivate", userId.ToString());
            return 0;
        });
    }

    public void ResetPassword(long ownerId, long userId, string newPassword)
    {
        RequireStrong(newPassword);
        using var c = db.Open();
        Db.InTransaction(c, () =>
        {
            Common.RequireOwner(c, ownerId);
            Sql.Exec(c, "UPDATE users SET password_hash=@h, failed_attempts=0, locked_until=NULL WHERE id=@i", ("@h", PasswordHasher.Hash(newPassword)), ("@i", userId));
            Common.Audit(c, clock, ownerId, null, "user.reset_password", userId.ToString());
            return 0;
        });
    }

    public void ChangePassword(long userId, string current, string newPassword)
    {
        RequireStrong(newPassword);
        using var c = db.Open();
        Db.InTransaction(c, () =>
        {
            var hash = Sql.Scalar<string>(c, "SELECT password_hash FROM users WHERE id=@i AND active=1", ("@i", userId));
            if (hash is null || !PasswordHasher.Verify(current, hash)) throw new PosException(PosError.InvalidCredentials, "The current password is wrong.");
            Sql.Exec(c, "UPDATE users SET password_hash=@h WHERE id=@i", ("@h", PasswordHasher.Hash(newPassword)), ("@i", userId));
            Common.Audit(c, clock, userId, null, "user.change_password", userId.ToString());
            return 0;
        });
    }

    // ---- offline owner recovery codes (no internet reset exists) ----
    const string CodeAlphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";

    static string NewCode()
    {
        var chars = Enumerable.Range(0, 12).Select(_ => CodeAlphabet[RandomNumberGenerator.GetInt32(CodeAlphabet.Length)]).ToArray();
        var s = new string(chars);
        return $"{s[..4]}-{s[4..8]}-{s[8..]}";
    }

    /// <summary>Creates a fresh set of eight single-use codes for an owner, invalidating the previous set. Shown once; only hashes are stored.</summary>
    public IReadOnlyList<string> GenerateRecoveryCodes(long ownerId, int count = 8)
    {
        using var c = db.Open();
        return Db.InTransaction(c, () =>
        {
            Common.RequireOwner(c, ownerId);
            Sql.Exec(c, "UPDATE recovery_codes SET used_at=@t WHERE user_id=@u AND used_at IS NULL", ("@t", Common.Stamp(clock)), ("@u", ownerId));
            var codes = Enumerable.Range(0, count).Select(_ => NewCode()).ToList();
            foreach (var code in codes)
                Sql.Exec(c, "INSERT INTO recovery_codes(user_id,code_hash,created_at) VALUES(@u,@h,@t)", ("@u", ownerId), ("@h", PasswordHasher.Hash(code)), ("@t", Common.Stamp(clock)));
            Common.Audit(c, clock, ownerId, null, "recovery.generate", ownerId.ToString());
            return (IReadOnlyList<string>)codes;
        });
    }

    /// <summary>
    /// Resets an owner's password with a recovery code. The code is consumed and a replacement is issued (returned once).
    /// </summary>
    public (bool Ok, string? ReplacementCode) ResetWithRecoveryCode(string username, string code, string newPassword)
    {
        RequireStrong(newPassword);
        using var c = db.Open();
        return Db.InTransaction(c, () =>
        {
            var owner = Sql.Query(c, "SELECT id FROM users WHERE username=@u AND role='owner' AND active=1", x => x.GetInt64(0), ("@u", username)).FirstOrDefault();
            if (owner == 0) return (false, (string?)null);
            var candidates = Sql.Query(c, "SELECT id,code_hash FROM recovery_codes WHERE user_id=@u AND used_at IS NULL", x => (Id: x.GetInt64(0), Hash: x.GetString(1)), ("@u", owner));
            var normalized = code.Trim().ToUpperInvariant();
            var match = candidates.FirstOrDefault(k => PasswordHasher.Verify(normalized, k.Hash));
            if (match.Hash is null)
            {
                Common.Audit(c, clock, owner, null, "recovery.failed", username);
                return (false, null);
            }

            Sql.Exec(c, "UPDATE recovery_codes SET used_at=@t WHERE id=@i", ("@t", Common.Stamp(clock)), ("@i", match.Id));
            Sql.Exec(c, "UPDATE users SET password_hash=@h, failed_attempts=0, locked_until=NULL WHERE id=@i", ("@h", PasswordHasher.Hash(newPassword)), ("@i", owner));
            var replacement = NewCode();
            Sql.Exec(c, "INSERT INTO recovery_codes(user_id,code_hash,created_at) VALUES(@u,@h,@t)", ("@u", owner), ("@h", PasswordHasher.Hash(replacement)), ("@t", Common.Stamp(clock)));
            Common.Audit(c, clock, owner, null, "recovery.used", username);
            return (true, replacement);
        });
    }

    public int RemainingRecoveryCodes(long ownerId)
    {
        using var c = db.Open();
        return (int)Sql.Scalar<long>(c, "SELECT COUNT(*) FROM recovery_codes WHERE user_id=@u AND used_at IS NULL", ("@u", ownerId));
    }
}

public sealed record AuditRow(long Id, string Timestamp, string? Actor, string? Approver, string Action, string? Target, string? Reason);

public sealed class AuditService(PosDatabase db)
{
    public IReadOnlyList<AuditRow> List(string? action = null, string? fromDate = null, string? toDate = null, int limit = 500)
    {
        using var c = db.Open();
        return Sql.Query(c,
            """
            SELECT a.id,a.ts,u.display_name,v.display_name,a.action,a.target,a.reason FROM audit_events a
            LEFT JOIN users u ON u.id=a.actor_id LEFT JOIN users v ON v.id=a.approver_id
            WHERE (@act IS NULL OR a.action LIKE @act) AND (@f IS NULL OR substr(a.ts,1,10)>=@f) AND (@t IS NULL OR substr(a.ts,1,10)<=@t)
            ORDER BY a.id DESC LIMIT @l
            """,
            x => new AuditRow(x.GetInt64(0), x.GetString(1), x.IsDBNull(2) ? null : x.GetString(2), x.IsDBNull(3) ? null : x.GetString(3), x.GetString(4),
                x.IsDBNull(5) ? null : x.GetString(5), x.IsDBNull(6) ? null : x.GetString(6)),
            ("@act", action is null ? null : action + "%"), ("@f", fromDate), ("@t", toDate), ("@l", limit));
    }
}

public sealed record HeldLine(long ProductId, decimal Quantity, decimal PriceAtHold = 0m);

public sealed record HeldCartRow(long Id, string Name, long CashierId, long? CustomerId, int ItemCount, string CreatedAt);

/// <summary>Held carts are drafts only: no sale, payment or stock reservation exists until the cart is paid.</summary>
public sealed class HeldCartService(PosDatabase db, IClock clock)
{
    public long Save(long cashierId, long? customerId, string name, IReadOnlyList<HeldLine> lines)
    {
        if (lines.Count == 0) throw new PosException(PosError.EmptyCart, "Nothing to hold.");
        using var c = db.Open();
        return Db.InTransaction(c, () =>
        {
            Common.RequireActive(c, cashierId);
            var id = Sql.Insert(c, "INSERT INTO held_carts(name,cashier_id,customer_id,payload,created_at) VALUES(@n,@c,@cu,@p,@t)",
                ("@n", string.IsNullOrWhiteSpace(name) ? "Held cart" : name.Trim()), ("@c", cashierId), ("@cu", customerId),
                ("@p", JsonSerializer.Serialize(lines)), ("@t", Common.Stamp(clock)));
            Common.Audit(c, clock, cashierId, null, "cart.hold", id.ToString());
            return id;
        });
    }

    public IReadOnlyList<HeldCartRow> List()
    {
        using var c = db.Open();
        return Sql.Query(c, "SELECT id,name,cashier_id,customer_id,payload,created_at FROM held_carts ORDER BY id",
            x => new HeldCartRow(x.GetInt64(0), x.GetString(1), x.GetInt64(2), x.IsDBNull(3) ? null : x.GetInt64(3),
                JsonSerializer.Deserialize<List<HeldLine>>(x.GetString(4))!.Count, x.GetString(5)));
    }

    /// <summary>Returns the lines and removes the draft (resume). The caller must revalidate stock and prices.</summary>
    public IReadOnlyList<HeldLine> Resume(long id)
    {
        using var c = db.Open();
        return Db.InTransaction(c, () =>
        {
            var json = Sql.Scalar<string>(c, "SELECT payload FROM held_carts WHERE id=@i", ("@i", id))
                ?? throw new PosException(PosError.NotFound, "Held cart not found.");
            Sql.Exec(c, "DELETE FROM held_carts WHERE id=@i", ("@i", id));
            return (IReadOnlyList<HeldLine>)JsonSerializer.Deserialize<List<HeldLine>>(json)!;
        });
    }

    public void Discard(long id)
    {
        using var c = db.Open();
        Db.InTransaction(c, () => Sql.Exec(c, "DELETE FROM held_carts WHERE id=@i", ("@i", id)));
    }
}
