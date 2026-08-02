using BAOCODETOOL.Core;

var banks = VietnamBankCatalog.All;
if (banks.Count < 50)
{
    throw new InvalidOperationException($"Expected the live bank catalog to contain at least 50 entries, got {banks.Count}.");
}

if (banks.Select(bank => bank.Code).Distinct(StringComparer.OrdinalIgnoreCase).Count() != banks.Count)
{
    throw new InvalidOperationException("Bank provider codes must be unique.");
}

AssertBank("VCB", "Vietcombank", "VIETCOMB");
AssertBank("Vietcom Bank", "Vietcombank", "VIETCOMB");
AssertBank("MBBANK", "MB Bank", "MBBANK");
AssertBank("MSB", "MSB", "MARIBANK");
AssertBank("LPB", "LPBank", "LVPBANK");
AssertBank("DAB", "DongA Bank", "DAB");
AssertBank("DONGABANK", "Vikki Bank", "DONGABANK");
AssertBank("DONGA", "DongA Bank", "DAB");

foreach (var bank in banks)
{
    AssertBank(bank.Code, bank.DisplayName, bank.ProviderCode);
}

Console.WriteLine($"BANK CATALOG DIAGNOSTICS: PASS ({banks.Count} provider entries)");
return 0;

static void AssertBank(string input, string expectedName, string expectedCode)
{
    if (!VietnamBankCatalog.TryNormalize(input, out var name, out var code))
    {
        throw new InvalidOperationException($"'{input}' was not resolved.");
    }

    if (!string.Equals(name, expectedName, StringComparison.Ordinal)
        || !string.Equals(code, expectedCode, StringComparison.OrdinalIgnoreCase))
    {
        throw new InvalidOperationException(
            $"'{input}' resolved to '{name}/{code}', expected '{expectedName}/{expectedCode}'.");
    }
}
