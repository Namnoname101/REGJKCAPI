using System.Threading;
using System.Threading.Tasks;
using BAOCODETOOL.Models;

namespace BAOCODETOOL.Services;

public interface IUserService
{
    Task<User> GetUserByChatIdAsync(long chatId, string? username = null, CancellationToken cancellationToken = default);
    Task<bool> AddBalanceAsync(long chatId, decimal amount, string description, string? referenceId = null, CancellationToken cancellationToken = default);
    Task<bool> DeductBalanceAsync(long chatId, decimal amount, string description, string? referenceId = null, CancellationToken cancellationToken = default);
    Task<bool> SetBalanceAsync(long chatId, decimal newBalance, string description, string? referenceId = null, CancellationToken cancellationToken = default);
}
