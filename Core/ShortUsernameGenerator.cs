using System;

namespace BAOCODETOOL.Core;

internal static class ShortUsernameGenerator
{
    private const char Prefix = 'u';

    public static string Next()
    {
        // One lowercase letter plus seven digits keeps the identifier short
        // and compatible with the target registration forms.
        return $"{Prefix}{Random.Shared.Next(0, 10_000_000):D7}";
    }
}
