using System.IO;
using System.Net.Sockets;
using System.Text;

namespace SkyFireLauncher.Realm;

// Talks AUTH_MIGRATE_ACCOUNT directly to the authserver's classic auth port
// (see AuthSocket.cpp) - a launcher-only pre-login command, never sent by
// the game client itself. Reuses the same account-identity/verifier logic
// as the in-game ".account convert email" command.
public static class AccountMigrationClient
{
    public const ushort DefaultPort = 3724;
    private const byte MigrateAccountCommand = 0x40;

    public static async Task<AuthMigrateResult> MigrateAsync(
        string host, string username, string oldPassword, string email, string newPassword,
        TimeSpan? timeout = null)
    {
        var request = BuildRequest(username, oldPassword, email, newPassword);

        using var client = new TcpClient();
        using var cts = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(10));

        await client.ConnectAsync(host, DefaultPort, cts.Token);
        await using var stream = client.GetStream();

        await stream.WriteAsync(request, cts.Token);

        var response = new byte[2];
        await ReadExactAsync(stream, response, cts.Token);

        if (response[0] != MigrateAccountCommand)
            throw new InvalidOperationException("Unexpected response from server.");

        return (AuthMigrateResult)response[1];
    }

    private static byte[] BuildRequest(string username, string oldPassword, string email, string newPassword)
    {
        var usernameBytes = Encoding.UTF8.GetBytes(username);
        var oldPasswordBytes = Encoding.UTF8.GetBytes(oldPassword);
        var emailBytes = Encoding.UTF8.GetBytes(email);
        var newPasswordBytes = Encoding.UTF8.GetBytes(newPassword);

        ValidateFieldLength(usernameBytes, nameof(username));
        ValidateFieldLength(oldPasswordBytes, nameof(oldPassword));
        ValidateFieldLength(emailBytes, nameof(email));
        ValidateFieldLength(newPasswordBytes, nameof(newPassword));

        var body = new byte[4 + usernameBytes.Length + oldPasswordBytes.Length + emailBytes.Length + newPasswordBytes.Length];
        var offset = 0;
        offset = WriteField(body, offset, usernameBytes);
        offset = WriteField(body, offset, oldPasswordBytes);
        offset = WriteField(body, offset, emailBytes);
        WriteField(body, offset, newPasswordBytes);

        var packet = new byte[3 + body.Length];
        packet[0] = MigrateAccountCommand;
        packet[1] = (byte)(body.Length & 0xFF);
        packet[2] = (byte)((body.Length >> 8) & 0xFF);
        Array.Copy(body, 0, packet, 3, body.Length);
        return packet;
    }

    private static int WriteField(byte[] buffer, int offset, byte[] fieldBytes)
    {
        buffer[offset++] = (byte)fieldBytes.Length;
        Array.Copy(fieldBytes, 0, buffer, offset, fieldBytes.Length);
        return offset + fieldBytes.Length;
    }

    private static void ValidateFieldLength(byte[] fieldBytes, string fieldName)
    {
        if (fieldBytes.Length > byte.MaxValue)
            throw new ArgumentException($"{fieldName} is too long.", fieldName);
    }

    private static async Task ReadExactAsync(NetworkStream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        var totalRead = 0;
        while (totalRead < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(totalRead, buffer.Length - totalRead), cancellationToken);
            if (read == 0)
                throw new IOException("Connection closed before a full response was received.");

            totalRead += read;
        }
    }
}
