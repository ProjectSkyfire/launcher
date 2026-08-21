namespace SkyFireLauncher.Realm;

// Mirrors AuthMigrateResult in SkyFire_548's src/server/authserver/Authentication/AuthCodes.h.
// Keep the two in sync - this is the wire contract for AUTH_MIGRATE_ACCOUNT.
public enum AuthMigrateResult : byte
{
    Ok = 0x00,
    NameNotExist = 0x01,
    PasswordIncorrect = 0x02,
    PasswordTooLong = 0x03,
    EmailTooLong = 0x04,
    EmailInvalid = 0x05,
    EmailAlreadyExists = 0x06,
    AccountBanned = 0x07,
    Failed = 0xFF
}

public static class AuthMigrateResultExtensions
{
    public static string ToDisplayMessage(this AuthMigrateResult result) => result switch
    {
        AuthMigrateResult.Ok => "Account migrated. Use your email and new password next time you log in.",
        AuthMigrateResult.NameNotExist => "That account name was not found.",
        AuthMigrateResult.PasswordIncorrect => "Current password is incorrect.",
        AuthMigrateResult.PasswordTooLong => "New password is too long.",
        AuthMigrateResult.EmailTooLong => "Email address is too long.",
        AuthMigrateResult.EmailInvalid => "That is not a valid email address.",
        AuthMigrateResult.EmailAlreadyExists => "That email address is already assigned to another account.",
        AuthMigrateResult.AccountBanned => "This account is banned.",
        _ => "Migration failed."
    };
}
