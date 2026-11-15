using System.Net;
using System.Net.Sockets;

namespace SkyFireLauncher.Realm;

internal static class LoginAddressResolver
{
    internal static string ResolveIpv4(string address, Func<string, IPAddress[]>? lookup = null)
    {
        var host = (address ?? string.Empty).Trim();
        if (IPAddress.TryParse(host, out var literal))
        {
            if (literal.AddressFamily != AddressFamily.InterNetwork)
                throw new NotSupportedException("The game client requires an IPv4 login address.");
            return literal.ToString();
        }

        if (Uri.CheckHostName(host) != UriHostNameType.Dns)
            throw new ArgumentException("Enter a login hostname or IPv4 address without a URL or port.");

        IPAddress[] addresses;
        try
        {
            addresses = lookup != null ? lookup(host) : Dns.GetHostAddressesAsync(host)
                .WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
        }
        catch (Exception ex) when (ex is SocketException or TimeoutException)
        {
            throw new InvalidOperationException($"Could not resolve login server '{host}'. Check the hostname and your network connection.", ex);
        }

        return addresses.FirstOrDefault(ip => ip.AddressFamily == AddressFamily.InterNetwork)?.ToString()
            ?? throw new InvalidOperationException($"Login server '{host}' has no IPv4 address.");
    }
}
