using BAOCODETOOL.Models;

namespace BAOCODETOOL.Services;

public interface IProfileRepository
{
    IReadOnlyList<AccountModel> GetAll();

    void Add(AccountModel account);

    void AddRange(IEnumerable<AccountModel> accounts);

    void Save();
}
