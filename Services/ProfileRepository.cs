using BAOCODETOOL.Core;
using BAOCODETOOL.Models;
using Microsoft.Extensions.Logging;

namespace BAOCODETOOL.Services;

public sealed class ProfileRepository : IProfileRepository
{
    private readonly object _sync = new();
    private readonly List<AccountModel> _profiles;
    private readonly ILogger<ProfileRepository> _logger;

    public ProfileRepository(ILogger<ProfileRepository> logger)
    {
        _logger = logger;
        _profiles = ProfileStore.LoadProfiles();
        _logger.LogInformation("Loaded {ProfileCount} saved profiles.", _profiles.Count);
    }

    public IReadOnlyList<AccountModel> GetAll()
    {
        lock (_sync)
        {
            return _profiles.ToList();
        }
    }

    public void Add(AccountModel account)
    {
        ArgumentNullException.ThrowIfNull(account);

        AddRange(new[] { account });
    }

    public void AddRange(IEnumerable<AccountModel> accounts)
    {
        ArgumentNullException.ThrowIfNull(accounts);

        var materialized = accounts.ToList();
        if (materialized.Count == 0)
        {
            return;
        }

        if (materialized.Any(account => account is null))
        {
            throw new ArgumentException("Profile collection contains a null account.", nameof(accounts));
        }

        lock (_sync)
        {
            _profiles.AddRange(materialized);
            ProfileStore.SaveProfiles(_profiles);
        }
    }

    public void Save()
    {
        lock (_sync)
        {
            ProfileStore.SaveProfiles(_profiles);
        }
    }
}
