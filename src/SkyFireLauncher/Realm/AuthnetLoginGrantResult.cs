namespace SkyFireLauncher.Realm;

public enum AuthnetLoginGrantResult : byte
{
    Ok = 0x00,
    NameNotExist = 0x01,
    PasswordIncorrect = 0x02,
    PasswordTooLong = 0x03,
    IdentityInvalid = 0x04,
    AccountBanned = 0x05,
    Failed = 0xFF
}

public static class AuthnetLoginGrantResultExtensions
{
    public static string ToDisplayMessage(this AuthnetLoginGrantResult result) => result switch
    {
        AuthnetLoginGrantResult.Ok => "Authnet login authorized.",
        AuthnetLoginGrantResult.NameNotExist => "That account was not found.",
        AuthnetLoginGrantResult.PasswordIncorrect => "Password is incorrect.",
        AuthnetLoginGrantResult.PasswordTooLong => "Password is too long.",
        AuthnetLoginGrantResult.IdentityInvalid => "Enter a valid account name or email address.",
        AuthnetLoginGrantResult.AccountBanned => "This account is banned.",
        _ => "Authnet login authorization failed."
    };
}
