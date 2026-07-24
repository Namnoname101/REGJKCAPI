using System;
using System.Linq;

namespace BAOCODETOOL.Core;

public sealed record ProxyConfiguration(
    string Scheme,
    string Host,
    int Port,
    string? Username,
    string? Password)
{
    private static readonly string[] SupportedSchemes = ["http", "https", "socks5"];

    public bool RequiresAuthentication => !string.IsNullOrWhiteSpace(Username);

    public bool IsSocks => Scheme.Equals("socks5", StringComparison.OrdinalIgnoreCase);

    public string Server => $"{FormatHost(Host)}:{Port}";

    public string ProxyServerArgument => $"{Scheme}://{Server}";

    public string Masked => RequiresAuthentication
        ? $"{Scheme}://{Username}:***@{Server}"
        : ProxyServerArgument;

    public override string ToString() => Masked;

    internal static bool IsSupportedScheme(string scheme) =>
        SupportedSchemes.Contains(scheme, StringComparer.OrdinalIgnoreCase);

    private static string FormatHost(string host) =>
        host.Contains(":", StringComparison.Ordinal) && !host.StartsWith("[", StringComparison.Ordinal)
            ? $"[{host}]"
            : host;
}
