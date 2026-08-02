using System.Globalization;
using System.Text;

namespace BAOCODETOOL.Core;

public sealed record VietnamBankOption(
    string Code,
    string DisplayName,
    string ProviderCode,
    IReadOnlyList<string> Aliases);

public static class VietnamBankCatalog
{
    private static readonly VietnamBankOption[] BankOptions =
    [
        Bank("KBANK", "Kasikornbank", "KBank"),
        Bank("VIETCOMB", "Vietcombank", "VCB", "Vietcom Bank", "Ngân hàng Ngoại thương Việt Nam"),
        Bank("MBBANK", "MB Bank", "MB", "MBB", "Ngân hàng Quân đội"),
        Bank("ACB", "ACB", "Ngân hàng Á Châu"),
        Bank("TECHCOMB", "Techcombank", "TCB", "Ngân hàng Kỹ thương Việt Nam"),
        Bank("VPBANK", "VPBank", "VPB"),
        Bank("AGRIBANK", "Agribank", "VBA", "Ngân hàng Nông nghiệp và Phát triển Nông thôn"),
        Bank("BIDV", "BIDV", "Ngân hàng Đầu tư và Phát triển Việt Nam"),
        Bank("KOOKMIN", "KB Kookmin Bank", "Kookmin"),
        Bank("ABB", "ABBank", "ABB"),
        Bank("OBANK", "OceanBank", "OCEANBANK", "Ocean Bank"),
        Bank("NCB", "NCB", "National Citizen Bank", "Ngân hàng Quốc Dân"),
        Bank("BVB", "BaoViet Bank", "BAOVIETBANK", "Bao Viet Bank"),
        Bank("VBSP", "VBSP", "Ngân hàng Chính sách Xã hội"),
        Bank("EXIM", "Eximbank", "EIB"),
        Bank("SCBBANK", "SCB", "Saigon Commercial Bank"),
        Bank("HSBCBANK", "HSBC", "HSBC Vietnam"),
        Bank("NABANK", "Nam A Bank", "NAB", "NamABank"),
        Bank("OCBANK", "OCB", "Orient Commercial Bank"),
        Bank("VCBANK", "Viet Capital Bank", "BANVIET", "VietCapitalBank", "Ngân hàng Bản Việt"),
        Bank("SHANBANK", "Shinhan Bank", "SHINHAN", "SHBVN"),
        Bank("GPBANK", "GPBank", "Global Petro Bank"),
        Bank("DBSVND", "DBS Bank", "DBS", "DBS HCM"),
        Bank("UOB", "UOB", "United Overseas Bank"),
        Bank("UBANK", "Ubank by VPBank", "Ubank"),
        Bank("VRB", "Vietnam-Russia Bank", "Viet Nga Bank", "Ngân hàng Liên doanh Việt Nga"),
        Bank("MARIBANK", "MSB", "Maritime Bank", "MaritimeBank"),
        Bank("PGBA", "PGBank", "PGB"),
        Bank("DAB", "DongA Bank", "DONGA", "EAB", "Ngân hàng Đông Á"),
        Bank("SHB", "SHB", "Saigon Hanoi Bank"),
        Bank("BVBBANK", "BVBank", "BVBANK", "Ngân hàng Bản Việt mới"),
        Bank("HLBANK", "Hong Leong Bank", "HLB", "HLBVN"),
        Bank("MBV", "MBV", "Modern Bank of Vietnam", "Ngân hàng Việt Nam Hiện Đại"),
        Bank("TIMOBANK", "Timo Bank", "TIMO"),
        Bank("VNDCBANK", "CB", "CBBANK", "Construction Bank"),
        Bank("VABANK", "VietABank", "VAB", "Viet A Bank"),
        Bank("VIB", "VIB", "Vietnam International Bank"),
        Bank("COB", "Co-opBank", "COB", "Ngân hàng Hợp tác"),
        Bank("VIETBANK", "VietBank", "VBB"),
        Bank("COOPBANK", "Co-opBank", "Coop Bank"),
        Bank("PVBANK", "PVcomBank", "PVCB", "PVCom Bank"),
        Bank("VIETINBA", "VietinBank", "CTG", "VTB", "Ngân hàng Công thương Việt Nam"),
        Bank("NAMABA", "Nam A Bank", "NamABank"),
        Bank("TPBANK", "TPBank", "TPB"),
        Bank("HDBANK", "HDBank", "HDB"),
        Bank("CAKE", "Cake by VPBank", "Cake"),
        Bank("SEABANK", "SeABank", "SSB"),
        Bank("BAB", "Bac A Bank", "BACABANK", "Ngân hàng Bắc Á"),
        Bank("CIMBVND", "CIMB Vietnam", "CIMB"),
        Bank("LVPBANK", "LPBank", "LPB", "LienVietPostBank"),
        Bank("INDOBANK", "Indovina Bank", "IVB"),
        Bank("LIOBANK", "LioBank", "LIO"),
        Bank("KLBANK", "KienlongBank", "KLB", "Kiên Long Bank"),
        Bank("DONGABANK", "Vikki Bank", "VIKKI", "Vikki Digital Bank"),
        Bank("SHINHB", "Shinhan Bank", "SHINHANBANK"),
        Bank("WRBANK", "Woori Bank", "WOORI"),
        Bank("SBANK", "Sacombank", "STB"),
        Bank("KTBANK", "Kasikornbank", "KBank Vietnam"),
        Bank("SABANK", "Saigonbank", "SGB", "Saigon Bank")
    ];

    private static readonly IReadOnlyDictionary<string, VietnamBankOption> Lookup = BuildLookup();

    public static IReadOnlyList<VietnamBankOption> All => BankOptions;

    public static bool TryResolve(string? value, out VietnamBankOption bank)
    {
        var key = Compact(value);
        return Lookup.TryGetValue(key, out bank!);
    }

    public static string? FindProviderCode(string? value) =>
        TryResolve(value, out var bank) ? bank.ProviderCode : null;

    public static bool TryNormalize(string? value, out string bankName, out string bankCode)
    {
        if (TryResolve(value, out var bank))
        {
            bankName = bank.DisplayName;
            bankCode = bank.Code;
            return true;
        }

        bankName = string.Empty;
        bankCode = string.Empty;
        return false;
    }

    private static VietnamBankOption Bank(string providerCode, string displayName, params string[] aliases) =>
        new(providerCode, displayName, providerCode, aliases);

    private static IReadOnlyDictionary<string, VietnamBankOption> BuildLookup()
    {
        var lookup = new Dictionary<string, VietnamBankOption>(StringComparer.Ordinal);

        // Provider codes win over aliases. GG/LL expose both DAB (DongA) and
        // DONGABANK (Vikki), so reserving codes first avoids an ambiguous alias.
        foreach (var bank in BankOptions)
        {
            lookup[Compact(bank.Code)] = bank;
            lookup[Compact(bank.ProviderCode)] = bank;
        }

        foreach (var bank in BankOptions)
        {
            AddAlias(lookup, bank.DisplayName, bank);
            foreach (var alias in bank.Aliases)
            {
                AddAlias(lookup, alias, bank);
            }
        }

        return lookup;
    }

    private static void AddAlias(
        IDictionary<string, VietnamBankOption> lookup,
        string alias,
        VietnamBankOption bank)
    {
        var key = Compact(alias);
        if (!string.IsNullOrWhiteSpace(key))
        {
            lookup.TryAdd(key, bank);
        }
    }

    private static string Compact(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var normalized = value.Trim()
            .Replace('Đ', 'D')
            .Replace('đ', 'd')
            .Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(normalized.Length);
        foreach (var character in normalized)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            if (char.IsLetterOrDigit(character))
            {
                builder.Append(char.ToUpperInvariant(character));
            }
        }

        return builder.ToString();
    }
}
