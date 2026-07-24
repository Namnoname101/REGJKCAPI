using System;
using System.Linq;

namespace BAOCODETOOL.Core;

public static class PhoneNumberFormatter
{
    public static string FormatTenDigitWithLeadingZero(string? phoneNumber)
    {
        var subscriberDigits = ExtractDigits(phoneNumber).TrimStart('0');

        if (subscriberDigits.Length != 9)
        {
            throw new FormatException("Phone number must resolve to exactly 10 digits starting with 0.");
        }

        return $"0{subscriberDigits}";
    }

    public static string FormatNineDigitWithoutLeadingZero(string? phoneNumber)
    {
        var subscriberDigits = ExtractDigits(phoneNumber).TrimStart('0');

        if (subscriberDigits.Length != 9)
        {
            throw new FormatException("Phone number must resolve to exactly 9 digits without the leading 0.");
        }

        return subscriberDigits;
    }

    private static string ExtractDigits(string? value)
    {
        return new string((value ?? string.Empty).Where(char.IsDigit).ToArray());
    }
}
