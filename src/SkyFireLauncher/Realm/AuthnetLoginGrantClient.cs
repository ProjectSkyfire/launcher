using System.IO;
using System.Net.Sockets;
using System.Text;

namespace SkyFireLauncher.Realm;

public static class AuthnetLoginGrantClient
{
    public const ushort DefaultPort = 3724;
    private const byte LoginGrantCommand = 0x41;

    public static async Task<(AuthnetLoginGrantResult Result, uint TtlSeconds)> RequestAsync(
        string host, string identity, string password, TimeSpan? timeout = null)
    {
        var request = BuildRequest(identity, password);

        using var client = new TcpClient();
        using var cts = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(10));

        await client.ConnectAsync(host, DefaultPort, cts.Token);
        await using var stream = client.GetStream();

        await stream.WriteAsync(request, cts.Token);

        var response = new byte[6];
        await ReadExactAsync(stream, response, cts.Token);

        if (response[0] != LoginGrantCommand)
            throw new InvalidOperationException("Unexpected response from server.");

        var ttlSeconds = (uint)response[2] |
            ((uint)response[3] << 8) |
            ((uint)response[4] << 16) |
            ((uint)response[5] << 24);

        return ((AuthnetLoginGrantResult)response[1], ttlSeconds);
    }

    private static byte[] BuildRequest(string identity, string password)
    {
        var identityBytes = Encoding.UTF8.GetBytes(identity);
        var passwordBytes = Encoding.UTF8.GetBytes(password);

        ValidateFieldLength(identityBytes, nameof(identity));
        ValidateFieldLength(passwordBytes, nameof(password));

        var body = new byte[2 + identityBytes.Length + passwordBytes.Length];
        var offset = 0;
        offset = WriteField(body, offset, identityBytes);
        WriteField(body, offset, passwordBytes);

        var packet = new byte[3 + body.Length];
        packet[0] = LoginGrantCommand;
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
