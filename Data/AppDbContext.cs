using BAOCODETOOL.Models;
using BAOCODETOOL.Core;
using Microsoft.EntityFrameworkCore;

namespace BAOCODETOOL.Data;

public sealed class AppDbContext : DbContext
{
    public AppDbContext()
    {
        Database.EnsureCreated();
        EnsureTablesCreated();
    }

    public AppDbContext(DbContextOptions<AppDbContext> options)
        : base(options)
    {
        Database.EnsureCreated();
        EnsureTablesCreated();
    }

    private void EnsureTablesCreated()
    {
        try
        {
            Database.ExecuteSqlRaw(@"
                CREATE TABLE IF NOT EXISTS ""Jobs"" (
                    ""Id"" INTEGER NOT NULL CONSTRAINT ""PK_Jobs"" PRIMARY KEY AUTOINCREMENT,
                    ""UserId"" INTEGER NOT NULL,
                    ""Provider"" TEXT NOT NULL,
                    ""Status"" TEXT NOT NULL,
                    ""StartTime"" TEXT NOT NULL,
                    ""EndTime"" TEXT NULL,
                    ""Logs"" TEXT NULL,
                    CONSTRAINT ""FK_Jobs_Users_UserId"" FOREIGN KEY (""UserId"") REFERENCES ""Users"" (""Id"") ON DELETE CASCADE
                );
                CREATE INDEX IF NOT EXISTS ""IX_Jobs_UserId"" ON ""Jobs"" (""UserId"");

                CREATE TABLE IF NOT EXISTS ""SePayWebhookTransactions"" (
                    ""Id"" INTEGER NOT NULL CONSTRAINT ""PK_SePayWebhookTransactions"" PRIMARY KEY AUTOINCREMENT,
                    ""SePayTransactionId"" INTEGER NOT NULL,
                    ""Gateway"" TEXT NOT NULL,
                    ""AccountNumber"" TEXT NOT NULL,
                    ""TransferType"" TEXT NOT NULL,
                    ""TransferAmount"" TEXT NOT NULL,
                    ""Content"" TEXT NOT NULL,
                    ""ReferenceCode"" TEXT NOT NULL,
                    ""ChatId"" INTEGER NULL,
                    ""Status"" TEXT NOT NULL,
                    ""StatusMessage"" TEXT NULL,
                    ""TransactionDate"" TEXT NOT NULL,
                    ""ReceivedAt"" TEXT NOT NULL
                );
                CREATE UNIQUE INDEX IF NOT EXISTS ""IX_SePayWebhookTransactions_SePayTransactionId""
                    ON ""SePayWebhookTransactions"" (""SePayTransactionId"");

                CREATE TABLE IF NOT EXISTS ""SePayPaymentRequests"" (
                    ""Id"" INTEGER NOT NULL CONSTRAINT ""PK_SePayPaymentRequests"" PRIMARY KEY AUTOINCREMENT,
                    ""ChatId"" INTEGER NOT NULL,
                    ""TransferCode"" TEXT NOT NULL,
                    ""ExpectedAmount"" TEXT NOT NULL,
                    ""Status"" TEXT NOT NULL,
                    ""CreatedAt"" TEXT NOT NULL,
                    ""ExpiresAt"" TEXT NOT NULL,
                    ""PaidAt"" TEXT NULL,
                    ""SePayTransactionId"" INTEGER NULL
                );
                CREATE UNIQUE INDEX IF NOT EXISTS ""IX_SePayPaymentRequests_TransferCode""
                    ON ""SePayPaymentRequests"" (""TransferCode"");
                CREATE UNIQUE INDEX IF NOT EXISTS ""IX_SePayPaymentRequests_SePayTransactionId""
                    ON ""SePayPaymentRequests"" (""SePayTransactionId"");

                CREATE TABLE IF NOT EXISTS ""ProxyPackages"" (
                    ""Id"" INTEGER NOT NULL CONSTRAINT ""PK_ProxyPackages"" PRIMARY KEY AUTOINCREMENT,
                    ""Name"" TEXT NOT NULL,
                    ""Category"" TEXT NOT NULL,
                    ""ProxyType"" INTEGER NOT NULL,
                    ""PeriodDays"" INTEGER NOT NULL,
                    ""OriginalPrice"" TEXT NOT NULL,
                    ""SellingPrice"" TEXT NOT NULL,
                    ""IsActive"" INTEGER NOT NULL,
                    ""Description"" TEXT NULL,
                    ""CreatedAt"" TEXT NOT NULL,
                    ""UpdatedAt"" TEXT NOT NULL
                );

                UPDATE ""ProxyPackages"" SET ""Name"" = 'Proxy HTTP VNPT - 1 Ngày' WHERE ""Name"" = 'Proxy Tĩnh VNPT - 1 Ngày';
                UPDATE ""ProxyPackages"" SET ""Name"" = 'Proxy HTTP Viettel - 1 Ngày' WHERE ""Name"" = 'Proxy Tĩnh Viettel - 1 Ngày';
                UPDATE ""ProxyPackages"" SET ""Name"" = 'Proxy HTTP FPT - 1 Ngày' WHERE ""Name"" = 'Proxy Tĩnh FPT - 1 Ngày';
                UPDATE ""ProxyPackages"" SET ""Name"" = 'Proxy HTTP VNPT - 3 Ngày' WHERE ""Name"" = 'Proxy Tĩnh VNPT - 3 Ngày';
                UPDATE ""ProxyPackages"" SET ""Name"" = 'Proxy HTTP Đa Mạng - 7 Ngày' WHERE ""Name"" = 'Proxy Tĩnh VIP Đa Mạng - 7 Ngày';

                INSERT INTO ""ProxyPackages"" (""Name"", ""Category"", ""ProxyType"", ""PeriodDays"", ""OriginalPrice"", ""SellingPrice"", ""IsActive"", ""Description"", ""CreatedAt"", ""UpdatedAt"")
                SELECT 'Proxy HTTP VNPT - 1 Ngày', 'VNPT', 0, 1, '800.0000', '2000.0000', 1, 'Proxy HTTP tĩnh tốc độ cao nhà mạng VNPT, hạn 1 ngày.', datetime('now'), datetime('now')
                WHERE NOT EXISTS (SELECT 1 FROM ""ProxyPackages"" WHERE ""Name"" = 'Proxy HTTP VNPT - 1 Ngày');

                INSERT INTO ""ProxyPackages"" (""Name"", ""Category"", ""ProxyType"", ""PeriodDays"", ""OriginalPrice"", ""SellingPrice"", ""IsActive"", ""Description"", ""CreatedAt"", ""UpdatedAt"")
                SELECT 'Proxy HTTP Viettel - 1 Ngày', 'Viettel', 0, 1, '1000.0000', '2500.0000', 1, 'Proxy HTTP tĩnh băng thông lớn Viettel, hạn 1 ngày.', datetime('now'), datetime('now')
                WHERE NOT EXISTS (SELECT 1 FROM ""ProxyPackages"" WHERE ""Name"" = 'Proxy HTTP Viettel - 1 Ngày');

                INSERT INTO ""ProxyPackages"" (""Name"", ""Category"", ""ProxyType"", ""PeriodDays"", ""OriginalPrice"", ""SellingPrice"", ""IsActive"", ""Description"", ""CreatedAt"", ""UpdatedAt"")
                SELECT 'Proxy HTTP FPT - 1 Ngày', 'FPT', 0, 1, '900.0000', '2200.0000', 1, 'Proxy HTTP tĩnh ổn định nhà mạng FPT, hạn 1 ngày.', datetime('now'), datetime('now')
                WHERE NOT EXISTS (SELECT 1 FROM ""ProxyPackages"" WHERE ""Name"" = 'Proxy HTTP FPT - 1 Ngày');

                INSERT INTO ""ProxyPackages"" (""Name"", ""Category"", ""ProxyType"", ""PeriodDays"", ""OriginalPrice"", ""SellingPrice"", ""IsActive"", ""Description"", ""CreatedAt"", ""UpdatedAt"")
                SELECT 'Proxy HTTP Mobifone - 1 Ngày', 'Mobifone', 0, 1, '1000.0000', '2500.0000', 1, 'Proxy HTTP tĩnh di động Mobifone, hạn 1 ngày.', datetime('now'), datetime('now')
                WHERE NOT EXISTS (SELECT 1 FROM ""ProxyPackages"" WHERE ""Name"" = 'Proxy HTTP Mobifone - 1 Ngày');

                INSERT INTO ""ProxyPackages"" (""Name"", ""Category"", ""ProxyType"", ""PeriodDays"", ""OriginalPrice"", ""SellingPrice"", ""IsActive"", ""Description"", ""CreatedAt"", ""UpdatedAt"")
                SELECT 'Proxy HTTP Đa Mạng - 1 Ngày', 'ALL', 0, 1, '800.0000', '2000.0000', 1, 'Proxy HTTP tĩnh ngẫu nhiên nhà mạng, hạn 1 ngày.', datetime('now'), datetime('now')
                WHERE NOT EXISTS (SELECT 1 FROM ""ProxyPackages"" WHERE ""Name"" = 'Proxy HTTP Đa Mạng - 1 Ngày');

                INSERT INTO ""ProxyPackages"" (""Name"", ""Category"", ""ProxyType"", ""PeriodDays"", ""OriginalPrice"", ""SellingPrice"", ""IsActive"", ""Description"", ""CreatedAt"", ""UpdatedAt"")
                SELECT 'Proxy HTTP VNPT - 3 Ngày', 'VNPT', 0, 3, '2200.0000', '5000.0000', 1, 'Proxy HTTP tĩnh VNPT tiết kiệm, hạn 3 ngày.', datetime('now'), datetime('now')
                WHERE NOT EXISTS (SELECT 1 FROM ""ProxyPackages"" WHERE ""Name"" = 'Proxy HTTP VNPT - 3 Ngày');

                INSERT INTO ""ProxyPackages"" (""Name"", ""Category"", ""ProxyType"", ""PeriodDays"", ""OriginalPrice"", ""SellingPrice"", ""IsActive"", ""Description"", ""CreatedAt"", ""UpdatedAt"")
                SELECT 'Proxy HTTP Viettel - 3 Ngày', 'Viettel', 0, 3, '2500.0000', '6000.0000', 1, 'Proxy HTTP tĩnh Viettel, hạn 3 ngày.', datetime('now'), datetime('now')
                WHERE NOT EXISTS (SELECT 1 FROM ""ProxyPackages"" WHERE ""Name"" = 'Proxy HTTP Viettel - 3 Ngày');

                INSERT INTO ""ProxyPackages"" (""Name"", ""Category"", ""ProxyType"", ""PeriodDays"", ""OriginalPrice"", ""SellingPrice"", ""IsActive"", ""Description"", ""CreatedAt"", ""UpdatedAt"")
                SELECT 'Proxy HTTP FPT - 3 Ngày', 'FPT', 0, 3, '2400.0000', '5500.0000', 1, 'Proxy HTTP tĩnh FPT, hạn 3 ngày.', datetime('now'), datetime('now')
                WHERE NOT EXISTS (SELECT 1 FROM ""ProxyPackages"" WHERE ""Name"" = 'Proxy HTTP FPT - 3 Ngày');

                INSERT INTO ""ProxyPackages"" (""Name"", ""Category"", ""ProxyType"", ""PeriodDays"", ""OriginalPrice"", ""SellingPrice"", ""IsActive"", ""Description"", ""CreatedAt"", ""UpdatedAt"")
                SELECT 'Proxy HTTP Đa Mạng - 3 Ngày', 'ALL', 0, 3, '2200.0000', '5000.0000', 1, 'Proxy HTTP tĩnh đa mạng, hạn 3 ngày.', datetime('now'), datetime('now')
                WHERE NOT EXISTS (SELECT 1 FROM ""ProxyPackages"" WHERE ""Name"" = 'Proxy HTTP Đa Mạng - 3 Ngày');

                INSERT INTO ""ProxyPackages"" (""Name"", ""Category"", ""ProxyType"", ""PeriodDays"", ""OriginalPrice"", ""SellingPrice"", ""IsActive"", ""Description"", ""CreatedAt"", ""UpdatedAt"")
                SELECT 'Proxy HTTP VNPT - 7 Ngày', 'VNPT', 0, 7, '5000.0000', '12000.0000', 1, 'Proxy HTTP tĩnh VNPT, hạn 7 ngày.', datetime('now'), datetime('now')
                WHERE NOT EXISTS (SELECT 1 FROM ""ProxyPackages"" WHERE ""Name"" = 'Proxy HTTP VNPT - 7 Ngày');

                INSERT INTO ""ProxyPackages"" (""Name"", ""Category"", ""ProxyType"", ""PeriodDays"", ""OriginalPrice"", ""SellingPrice"", ""IsActive"", ""Description"", ""CreatedAt"", ""UpdatedAt"")
                SELECT 'Proxy HTTP Viettel - 7 Ngày', 'Viettel', 0, 7, '5500.0000', '13000.0000', 1, 'Proxy HTTP tĩnh Viettel, hạn 7 ngày.', datetime('now'), datetime('now')
                WHERE NOT EXISTS (SELECT 1 FROM ""ProxyPackages"" WHERE ""Name"" = 'Proxy HTTP Viettel - 7 Ngày');

                INSERT INTO ""ProxyPackages"" (""Name"", ""Category"", ""ProxyType"", ""PeriodDays"", ""OriginalPrice"", ""SellingPrice"", ""IsActive"", ""Description"", ""CreatedAt"", ""UpdatedAt"")
                SELECT 'Proxy HTTP FPT - 7 Ngày', 'FPT', 0, 7, '5200.0000', '12500.0000', 1, 'Proxy HTTP tĩnh FPT, hạn 7 ngày.', datetime('now'), datetime('now')
                WHERE NOT EXISTS (SELECT 1 FROM ""ProxyPackages"" WHERE ""Name"" = 'Proxy HTTP FPT - 7 Ngày');

                INSERT INTO ""ProxyPackages"" (""Name"", ""Category"", ""ProxyType"", ""PeriodDays"", ""OriginalPrice"", ""SellingPrice"", ""IsActive"", ""Description"", ""CreatedAt"", ""UpdatedAt"")
                SELECT 'Proxy HTTP Đa Mạng - 7 Ngày', 'ALL', 0, 7, '5000.0000', '12000.0000', 1, 'Proxy HTTP tĩnh VIP đa mạng, hạn 7 ngày.', datetime('now'), datetime('now')
                WHERE NOT EXISTS (SELECT 1 FROM ""ProxyPackages"" WHERE ""Name"" = 'Proxy HTTP Đa Mạng - 7 Ngày');

                INSERT INTO ""ProxyPackages"" (""Name"", ""Category"", ""ProxyType"", ""PeriodDays"", ""OriginalPrice"", ""SellingPrice"", ""IsActive"", ""Description"", ""CreatedAt"", ""UpdatedAt"")
                SELECT 'Proxy HTTP VNPT - 30 Ngày', 'VNPT', 0, 30, '18000.0000', '40000.0000', 1, 'Proxy HTTP tĩnh VNPT tháng (30 ngày).', datetime('now'), datetime('now')
                WHERE NOT EXISTS (SELECT 1 FROM ""ProxyPackages"" WHERE ""Name"" = 'Proxy HTTP VNPT - 30 Ngày');

                INSERT INTO ""ProxyPackages"" (""Name"", ""Category"", ""ProxyType"", ""PeriodDays"", ""OriginalPrice"", ""SellingPrice"", ""IsActive"", ""Description"", ""CreatedAt"", ""UpdatedAt"")
                SELECT 'Proxy HTTP Viettel - 30 Ngày', 'Viettel', 0, 30, '20000.0000', '45000.0000', 1, 'Proxy HTTP tĩnh Viettel tháng (30 ngày).', datetime('now'), datetime('now')
                WHERE NOT EXISTS (SELECT 1 FROM ""ProxyPackages"" WHERE ""Name"" = 'Proxy HTTP Viettel - 30 Ngày');

                INSERT INTO ""ProxyPackages"" (""Name"", ""Category"", ""ProxyType"", ""PeriodDays"", ""OriginalPrice"", ""SellingPrice"", ""IsActive"", ""Description"", ""CreatedAt"", ""UpdatedAt"")
                SELECT 'Proxy HTTP FPT - 30 Ngày', 'FPT', 0, 30, '19000.0000', '42000.0000', 1, 'Proxy HTTP tĩnh FPT tháng (30 ngày).', datetime('now'), datetime('now')
                WHERE NOT EXISTS (SELECT 1 FROM ""ProxyPackages"" WHERE ""Name"" = 'Proxy HTTP FPT - 30 Ngày');

                INSERT INTO ""ProxyPackages"" (""Name"", ""Category"", ""ProxyType"", ""PeriodDays"", ""OriginalPrice"", ""SellingPrice"", ""IsActive"", ""Description"", ""CreatedAt"", ""UpdatedAt"")
                SELECT 'Proxy HTTP Đa Mạng - 30 Ngày', 'ALL', 0, 30, '18000.0000', '40000.0000', 1, 'Proxy HTTP tĩnh Đa Mạng tháng (30 ngày).', datetime('now'), datetime('now')
                WHERE NOT EXISTS (SELECT 1 FROM ""ProxyPackages"" WHERE ""Name"" = 'Proxy HTTP Đa Mạng - 30 Ngày');

                INSERT INTO ""ProxyPackages"" (""Name"", ""Category"", ""ProxyType"", ""PeriodDays"", ""OriginalPrice"", ""SellingPrice"", ""IsActive"", ""Description"", ""CreatedAt"", ""UpdatedAt"")
                SELECT 'Proxy SOCKS5 VNPT - 1 Ngày', 'VNPT', 1, 1, '1000.0000', '2500.0000', 1, 'Proxy SOCKS5 tĩnh VNPT, hạn 1 ngày.', datetime('now'), datetime('now')
                WHERE NOT EXISTS (SELECT 1 FROM ""ProxyPackages"" WHERE ""Name"" = 'Proxy SOCKS5 VNPT - 1 Ngày');

                INSERT INTO ""ProxyPackages"" (""Name"", ""Category"", ""ProxyType"", ""PeriodDays"", ""OriginalPrice"", ""SellingPrice"", ""IsActive"", ""Description"", ""CreatedAt"", ""UpdatedAt"")
                SELECT 'Proxy SOCKS5 Viettel - 1 Ngày', 'Viettel', 1, 1, '1200.0000', '3000.0000', 1, 'Proxy SOCKS5 tĩnh Viettel, hạn 1 ngày.', datetime('now'), datetime('now')
                WHERE NOT EXISTS (SELECT 1 FROM ""ProxyPackages"" WHERE ""Name"" = 'Proxy SOCKS5 Viettel - 1 Ngày');

                INSERT INTO ""ProxyPackages"" (""Name"", ""Category"", ""ProxyType"", ""PeriodDays"", ""OriginalPrice"", ""SellingPrice"", ""IsActive"", ""Description"", ""CreatedAt"", ""UpdatedAt"")
                SELECT 'Proxy SOCKS5 FPT - 1 Ngày', 'FPT', 1, 1, '1100.0000', '2800.0000', 1, 'Proxy SOCKS5 tĩnh FPT, hạn 1 ngày.', datetime('now'), datetime('now')
                WHERE NOT EXISTS (SELECT 1 FROM ""ProxyPackages"" WHERE ""Name"" = 'Proxy SOCKS5 FPT - 1 Ngày');

                INSERT INTO ""ProxyPackages"" (""Name"", ""Category"", ""ProxyType"", ""PeriodDays"", ""OriginalPrice"", ""SellingPrice"", ""IsActive"", ""Description"", ""CreatedAt"", ""UpdatedAt"")
                SELECT 'Proxy SOCKS5 Đa Mạng - 1 Ngày', 'ALL', 1, 1, '1000.0000', '2500.0000', 1, 'Proxy SOCKS5 tĩnh đa mạng, hạn 1 ngày.', datetime('now'), datetime('now')
                WHERE NOT EXISTS (SELECT 1 FROM ""ProxyPackages"" WHERE ""Name"" = 'Proxy SOCKS5 Đa Mạng - 1 Ngày');

                INSERT INTO ""ProxyPackages"" (""Name"", ""Category"", ""ProxyType"", ""PeriodDays"", ""OriginalPrice"", ""SellingPrice"", ""IsActive"", ""Description"", ""CreatedAt"", ""UpdatedAt"")
                SELECT 'Proxy SOCKS5 VNPT - 7 Ngày', 'VNPT', 1, 7, '6000.0000', '15000.0000', 1, 'Proxy SOCKS5 tĩnh VNPT, hạn 7 ngày.', datetime('now'), datetime('now')
                WHERE NOT EXISTS (SELECT 1 FROM ""ProxyPackages"" WHERE ""Name"" = 'Proxy SOCKS5 VNPT - 7 Ngày');

                INSERT INTO ""ProxyPackages"" (""Name"", ""Category"", ""ProxyType"", ""PeriodDays"", ""OriginalPrice"", ""SellingPrice"", ""IsActive"", ""Description"", ""CreatedAt"", ""UpdatedAt"")
                SELECT 'Proxy SOCKS5 Viettel - 7 Ngày', 'Viettel', 1, 7, '6500.0000', '16000.0000', 1, 'Proxy SOCKS5 tĩnh Viettel, hạn 7 ngày.', datetime('now'), datetime('now')
                WHERE NOT EXISTS (SELECT 1 FROM ""ProxyPackages"" WHERE ""Name"" = 'Proxy SOCKS5 Viettel - 7 Ngày');

                INSERT INTO ""ProxyPackages"" (""Name"", ""Category"", ""ProxyType"", ""PeriodDays"", ""OriginalPrice"", ""SellingPrice"", ""IsActive"", ""Description"", ""CreatedAt"", ""UpdatedAt"")
                SELECT 'Proxy SOCKS5 Đa Mạng - 7 Ngày', 'ALL', 1, 7, '6000.0000', '15000.0000', 1, 'Proxy SOCKS5 tĩnh đa mạng, hạn 7 ngày.', datetime('now'), datetime('now')
                WHERE NOT EXISTS (SELECT 1 FROM ""ProxyPackages"" WHERE ""Name"" = 'Proxy SOCKS5 Đa Mạng - 7 Ngày');

                INSERT INTO ""ProxyPackages"" (""Name"", ""Category"", ""ProxyType"", ""PeriodDays"", ""OriginalPrice"", ""SellingPrice"", ""IsActive"", ""Description"", ""CreatedAt"", ""UpdatedAt"")
                SELECT 'Proxy SOCKS5 VNPT - 30 Ngày', 'VNPT', 1, 30, '22000.0000', '50000.0000', 1, 'Proxy SOCKS5 tĩnh VNPT tháng (30 ngày).', datetime('now'), datetime('now')
                WHERE NOT EXISTS (SELECT 1 FROM ""ProxyPackages"" WHERE ""Name"" = 'Proxy SOCKS5 VNPT - 30 Ngày');

                INSERT INTO ""ProxyPackages"" (""Name"", ""Category"", ""ProxyType"", ""PeriodDays"", ""OriginalPrice"", ""SellingPrice"", ""IsActive"", ""Description"", ""CreatedAt"", ""UpdatedAt"")
                SELECT 'Proxy SOCKS5 Viettel - 30 Ngày', 'Viettel', 1, 30, '25000.0000', '55000.0000', 1, 'Proxy SOCKS5 tĩnh Viettel tháng (30 ngày).', datetime('now'), datetime('now')
                WHERE NOT EXISTS (SELECT 1 FROM ""ProxyPackages"" WHERE ""Name"" = 'Proxy SOCKS5 Viettel - 30 Ngày');

                INSERT INTO ""ProxyPackages"" (""Name"", ""Category"", ""ProxyType"", ""PeriodDays"", ""OriginalPrice"", ""SellingPrice"", ""IsActive"", ""Description"", ""CreatedAt"", ""UpdatedAt"")
                SELECT 'Proxy SOCKS5 Đa Mạng - 30 Ngày', 'ALL', 1, 30, '22000.0000', '50000.0000', 1, 'Proxy SOCKS5 tĩnh đa mạng tháng (30 ngày).', datetime('now'), datetime('now')
                WHERE NOT EXISTS (SELECT 1 FROM ""ProxyPackages"" WHERE ""Name"" = 'Proxy SOCKS5 Đa Mạng - 30 Ngày');
            ");
        }
        catch { }
    }

    public DbSet<User> Users => Set<User>();
    public DbSet<Transaction> Transactions => Set<Transaction>();
    public DbSet<Job> Jobs => Set<Job>();
    public DbSet<ProxyPackage> ProxyPackages => Set<ProxyPackage>();
    public DbSet<SePayWebhookTransaction> SePayWebhookTransactions => Set<SePayWebhookTransaction>();
    public DbSet<SePayPaymentRequest> SePayPaymentRequests => Set<SePayPaymentRequest>();

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        if (!optionsBuilder.IsConfigured)
        {
            optionsBuilder.UseSqlite($"Data Source={RuntimePaths.DatabasePath}");
        }
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<User>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.ChatId).IsUnique();

            entity.Property(e => e.Balance)
                .HasPrecision(18, 4);

            entity.Property(e => e.TotalSpent)
                .HasPrecision(18, 4);
        });

        modelBuilder.Entity<Transaction>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.UserId);

            entity.Property(e => e.Amount)
                .HasPrecision(18, 4);

            entity.HasOne(d => d.User)
                .WithMany(p => p.Transactions)
                .HasForeignKey(d => d.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Job>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.UserId);

            entity.HasOne(d => d.User)
                .WithMany(p => p.Jobs)
                .HasForeignKey(d => d.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<SePayWebhookTransaction>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.SePayTransactionId).IsUnique();
            entity.Property(e => e.TransferAmount).HasPrecision(18, 4);
        });

        modelBuilder.Entity<SePayPaymentRequest>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.TransferCode).IsUnique();
            entity.HasIndex(e => e.SePayTransactionId).IsUnique();
            entity.HasIndex(e => e.ChatId);
            entity.Property(e => e.ExpectedAmount).HasPrecision(18, 4);
        });

        modelBuilder.Entity<ProxyPackage>(entity =>
        {
            entity.HasKey(e => e.Id);

            entity.Property(e => e.OriginalPrice)
                .HasPrecision(18, 4);

            entity.Property(e => e.SellingPrice)
                .HasPrecision(18, 4);
        });
    }
}
