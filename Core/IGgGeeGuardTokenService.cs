namespace BAOCODETOOL.Core;

public interface IGgGeeGuardTokenService
{
    Task<string?> GetGeeGuardTokenAsync(
        ProxyConfiguration? proxy,
        string registrationUrl,
        string appId,
        CancellationToken cancellationToken);
}
