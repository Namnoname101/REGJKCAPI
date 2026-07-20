using System;
using System.Net;

namespace VibecodeReg.Core;

public static class ProxyConfigurationParser
{
    public static bool TryParse(string? rawProxy, out ProxyConfiguration? configuration, out string? error)
    {
        configuration = null;
        error = null;

        if (string.IsNullOrWhiteSpace(rawProxy))
        {
            return true;
        }

        var value = rawProxy.Trim();
        var scheme = "http";
        var authority = value;

        var schemeSeparatorIndex = value.IndexOf("://", StringComparison.Ordinal);
        if (schemeSeparatorIndex >= 0)
        {
            scheme = value[..schemeSeparatorIndex].Trim().ToLowerInvariant();
            authority = value[(schemeSeparatorIndex + 3)..].Trim();

            if (!ProxyConfiguration.IsSupportedScheme(scheme))
            {
                error = "Proxy scheme must be http, https, or socks5.";
                return false;
            }
        }

        if (string.IsNullOrWhiteSpace(authority))
        {
            error = "Proxy host is required.";
            return false;
        }

        string? username = null;
        string? password = null;
        var hostPortAndMaybeCredentials = authority;

        var userInfoSeparatorIndex = authority.LastIndexOf('@');
        if (userInfoSeparatorIndex >= 0)
        {
            var userInfo = authority[..userInfoSeparatorIndex];
            hostPortAndMaybeCredentials = authority[(userInfoSeparatorIndex + 1)..];

            if (!TryParseUserInfo(userInfo, out username, out password, out error))
            {
                return false;
            }
        }

        if (!TryParseHostPortAndTailCredentials(
                hostPortAndMaybeCredentials,
                userInfoSeparatorIndex >= 0,
                out var host,
                out var port,
                out var tailUsername,
                out var tailPassword,
                out error))
        {
            return false;
        }

        if (username is null && tailUsername is not null)
        {
            username = tailUsername;
            password = tailPassword;
        }

        if (!ValidateHost(host, out error))
        {
            return false;
        }

        if (port is < 1 or > 65535)
        {
            error = "Proxy port must be between 1 and 65535.";
            return false;
        }

        if ((username is null) != (password is null))
        {
            error = "Authenticated proxy must include both username and password.";
            return false;
        }

        if (scheme.Equals("socks5", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(username))
        {
            error = "Authenticated SOCKS5 proxy is not supported. Use a local forwarding proxy or HTTP/HTTPS proxy.";
            return false;
        }

        configuration = new ProxyConfiguration(scheme, host, port, username, password);
        return true;
    }

    private static bool TryParseUserInfo(
        string userInfo,
        out string? username,
        out string? password,
        out string? error)
    {
        username = null;
        password = null;
        error = null;

        var separatorIndex = userInfo.IndexOf(':');
        if (separatorIndex <= 0 || separatorIndex == userInfo.Length - 1)
        {
            error = "Authenticated proxy must include both username and password.";
            return false;
        }

        username = UnescapeUserInfo(userInfo[..separatorIndex]);
        password = UnescapeUserInfo(userInfo[(separatorIndex + 1)..]);

        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
        {
            error = "Authenticated proxy must include both username and password.";
            return false;
        }

        return true;
    }

    private static bool TryParseHostPortAndTailCredentials(
        string value,
        bool userInfoAlreadyParsed,
        out string host,
        out int port,
        out string? username,
        out string? password,
        out string? error)
    {
        host = string.Empty;
        port = 0;
        username = null;
        password = null;
        error = null;

        if (value.StartsWith("[", StringComparison.Ordinal))
        {
            var closingBracketIndex = value.IndexOf(']');
            if (closingBracketIndex <= 1)
            {
                error = "IPv6 proxy host must use [host]:port format.";
                return false;
            }

            host = value[1..closingBracketIndex];
            var tail = value[(closingBracketIndex + 1)..];
            if (!tail.StartsWith(":", StringComparison.Ordinal))
            {
                error = "Proxy port is required.";
                return false;
            }

            return TryParsePortAndTailCredentials(tail[1..], userInfoAlreadyParsed, out port, out username, out password, out error);
        }

        var parts = value.Split(new[] { ':' }, userInfoAlreadyParsed ? 2 : 4, StringSplitOptions.TrimEntries);
        if (parts.Length < 2)
        {
            error = "Proxy must use host:port or include credentials in a supported format.";
            return false;
        }

        host = parts[0];

        if (!int.TryParse(parts[1], out port))
        {
            error = "Proxy port must be a number.";
            return false;
        }

        if (userInfoAlreadyParsed)
        {
            return true;
        }

        if (parts.Length == 3)
        {
            error = "Authenticated proxy must include both username and password.";
            return false;
        }

        if (parts.Length == 4)
        {
            username = parts[2];
            password = parts[3];

            if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
            {
                error = "Authenticated proxy must include both username and password.";
                return false;
            }
        }

        return true;
    }

    private static bool TryParsePortAndTailCredentials(
        string value,
        bool userInfoAlreadyParsed,
        out int port,
        out string? username,
        out string? password,
        out string? error)
    {
        port = 0;
        username = null;
        password = null;
        error = null;

        var parts = value.Split(new[] { ':' }, userInfoAlreadyParsed ? 2 : 3, StringSplitOptions.TrimEntries);
        if (parts.Length == 0 || !int.TryParse(parts[0], out port))
        {
            error = "Proxy port must be a number.";
            return false;
        }

        if (userInfoAlreadyParsed)
        {
            return true;
        }

        if (parts.Length == 2)
        {
            error = "Authenticated proxy must include both username and password.";
            return false;
        }

        if (parts.Length == 3)
        {
            username = parts[1];
            password = parts[2];
        }

        return true;
    }

    private static bool ValidateHost(string host, out string? error)
    {
        error = null;

        if (string.IsNullOrWhiteSpace(host))
        {
            error = "Proxy host is required.";
            return false;
        }

        if (host.AsSpan().IndexOfAny([' ', '\t', '\r', '\n']) >= 0)
        {
            error = "Proxy host cannot contain whitespace.";
            return false;
        }

        if (Uri.CheckHostName(host) == UriHostNameType.Unknown && !IPAddress.TryParse(host, out _))
        {
            error = "Proxy host must be an IPv4 address, hostname, or bracketed IPv6 address.";
            return false;
        }

        return true;
    }

    private static string UnescapeUserInfo(string value)
    {
        try
        {
            return Uri.UnescapeDataString(value);
        }
        catch (UriFormatException)
        {
            return value;
        }
    }
}
