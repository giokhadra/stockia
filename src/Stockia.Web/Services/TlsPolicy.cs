using System.Security.Authentication;

namespace Stockia.Web.Services;

/// <summary>SR-34: HTTPS connections use TLS 1.2 or later, never SSL 3, TLS 1.0 or TLS 1.1.</summary>
public static class TlsPolicy
{
    /// <summary>TLS 1.3 is added where the operating system supports it as a server
    /// (Windows 11 / Server 2022 and later, Linux, macOS); older Windows 10 builds get TLS 1.2 only.</summary>
    public static SslProtocols Allowed =>
        !OperatingSystem.IsWindows() || OperatingSystem.IsWindowsVersionAtLeast(10, 0, 20348)
            ? SslProtocols.Tls12 | SslProtocols.Tls13
            : SslProtocols.Tls12;
}
