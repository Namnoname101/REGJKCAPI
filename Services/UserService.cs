using System;
using System.Threading;
using System.Threading.Tasks;
using BAOCODETOOL.Data;
using BAOCODETOOL.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace BAOCODETOOL.Services;

public sealed class UserService : IUserService
{
    private readonly AppDbContext _dbContext;
    private readonly ILogger<UserService> _logger;

    public UserService(AppDbContext dbContext, ILogger<UserService> logger)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<User> GetUserByChatIdAsync(long chatId, string? username = null, CancellationToken cancellationToken = default)
    {
        var user = await _dbContext.Users.FirstOrDefaultAsync(u => u.ChatId == chatId, cancellationToken);

        if (user == null)
        {
            user = new User
            {
                ChatId = chatId,
                Username = username,
                Balance = 0,
                TotalSpent = 0,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };
            _dbContext.Users.Add(user);
            await _dbContext.SaveChangesAsync(cancellationToken);
            _logger.LogInformation("Created new user with ChatId: {ChatId}, Username: {Username}", chatId, username);
        }
        else if (username != null && !string.Equals(user.Username, username, StringComparison.Ordinal))
        {
            user.Username = username;
            user.UpdatedAt = DateTime.UtcNow;
            await _dbContext.SaveChangesAsync(cancellationToken);
        }

        return user;
    }

    public async Task<bool> AddBalanceAsync(long chatId, decimal amount, string description, string? referenceId = null, CancellationToken cancellationToken = default)
    {
        if (amount <= 0)
        {
            _logger.LogWarning("Attempted to add non-positive balance ({Amount}) for ChatId: {ChatId}", amount, chatId);
            return false;
        }

        var user = await GetUserByChatIdAsync(chatId, null, cancellationToken);

        user.Balance += amount;
        user.UpdatedAt = DateTime.UtcNow;

        var transaction = new Transaction
        {
            UserId = user.Id,
            Amount = amount,
            Type = "Deposit",
            Description = description,
            ReferenceId = referenceId,
            CreatedAt = DateTime.UtcNow
        };

        _dbContext.Transactions.Add(transaction);
        await _dbContext.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("Added balance {Amount} to ChatId: {ChatId}. New Balance: {Balance}", amount, chatId, user.Balance);
        return true;
    }

    public async Task<bool> DeductBalanceAsync(long chatId, decimal amount, string description, string? referenceId = null, CancellationToken cancellationToken = default)
    {
        if (amount <= 0)
        {
            _logger.LogWarning("Attempted to deduct non-positive balance ({Amount}) for ChatId: {ChatId}", amount, chatId);
            return false;
        }

        var user = await GetUserByChatIdAsync(chatId, null, cancellationToken);

        if (user.Balance < amount)
        {
            _logger.LogWarning("Insufficient balance for ChatId: {ChatId}. Required: {Amount}, Available: {Balance}", chatId, amount, user.Balance);
            return false;
        }

        user.Balance -= amount;
        user.TotalSpent += amount;
        user.UpdatedAt = DateTime.UtcNow;

        var transaction = new Transaction
        {
            UserId = user.Id,
            Amount = -amount,
            Type = "Withdraw",
            Description = description,
            ReferenceId = referenceId,
            CreatedAt = DateTime.UtcNow
        };

        _dbContext.Transactions.Add(transaction);
        await _dbContext.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("Deducted balance {Amount} from ChatId: {ChatId}. New Balance: {Balance}", amount, chatId, user.Balance);
        return true;
    }

    public async Task<bool> SetBalanceAsync(long chatId, decimal newBalance, string description, string? referenceId = null, CancellationToken cancellationToken = default)
    {
        if (newBalance < 0)
        {
            _logger.LogWarning("Attempted to set negative balance ({Amount}) for ChatId: {ChatId}", newBalance, chatId);
            return false;
        }

        var user = await GetUserByChatIdAsync(chatId, null, cancellationToken);
        var diff = newBalance - user.Balance;
        if (diff == 0) return true;

        user.Balance = newBalance;
        user.UpdatedAt = DateTime.UtcNow;

        var transaction = new Transaction
        {
            UserId = user.Id,
            Amount = diff,
            Type = diff > 0 ? "Deposit" : "Withdraw",
            Description = description,
            ReferenceId = referenceId ?? "ADMIN_SET",
            CreatedAt = DateTime.UtcNow
        };

        _dbContext.Transactions.Add(transaction);
        await _dbContext.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("Set balance to {NewBalance} (Diff: {Diff}) for ChatId: {ChatId}", newBalance, diff, chatId);
        return true;
    }
}
