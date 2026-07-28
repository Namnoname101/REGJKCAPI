using BAOCODETOOL.Core;

var cases = new[]
{
    new ParserCase("empty", string.Empty, true, false),
    new ParserCase("host:port", "127.0.0.1:8080", true, false),
    new ParserCase("host:port:user:pass", "127.0.0.1:8080:user:pass", true, true),
    new ParserCase("userinfo host", "user:pass@proxy.example.com:3128", true, true),
    new ParserCase("http userinfo", "http://user:pass@proxy.example.com:3128", true, true),
    new ParserCase("password with specials", "http://user:p%40ss:w%25rd@proxy.example.com:3128", true, true),
    new ParserCase("bad port", "127.0.0.1:70000", false, false),
    new ParserCase("empty host", ":8080", false, false),
    new ParserCase("authenticated socks5", "socks5://user:pass@proxy.example.com:1080", false, false),
    new ParserCase("socks5 no auth", "socks5://proxy.example.com:1080", true, false)
};

var failures = 0;

foreach (var parserCase in cases)
{
    var parsed = ProxyConfigurationParser.TryParse(parserCase.Input, out var configuration, out var error);
    var success = parsed && error is null;
    var requiresAuthentication = configuration?.RequiresAuthentication == true;
    var passed = success == parserCase.ShouldParse
        && (!success || requiresAuthentication == parserCase.ShouldAuthenticate);

    if (passed && configuration is not null)
    {
        var roundTripParsed = ProxyConfigurationParser.TryParse(
            configuration.ConnectionString,
            out var roundTrip,
            out var roundTripError);
        passed = roundTripParsed
            && roundTripError is null
            && roundTrip == configuration;
    }

    Console.WriteLine($"{(passed ? "PASS" : "FAIL")} {parserCase.Name}: {configuration?.Masked ?? error ?? "empty"}");

    if (!passed)
    {
        failures++;
    }
}

if (failures > 0)
{
    Environment.ExitCode = 1;
}

sealed record ParserCase(string Name, string Input, bool ShouldParse, bool ShouldAuthenticate);

