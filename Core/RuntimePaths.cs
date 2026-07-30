using System.Reflection;
using Microsoft.Data.Sqlite;

namespace BAOCODETOOL.Core;

/// <summary>
/// Separates read-only application assets from writable operational data.
/// A self-contained EXE can then be moved or updated without losing its
/// database, bot access list, browser profiles, diagnostics, or settings.
/// </summary>
public static class RuntimePaths
{
    private const string ApplicationFolderName = "VibeCodeReg";
    private const string ResourcePrefix = "BAOCODETOOL.RuntimeAssets/";
    private static readonly string[] MutableFileNames =
    [
        "allowed_users.json",
        "profiles_data.json",
        "success.txt"
    ];
    private static readonly string[] DatabaseFileNames =
    [
        "vibecodereg.db",
        "vibecodereg.db-shm",
        "vibecodereg.db-wal"
    ];
    private static readonly string[] MeaningfulDataTables =
    [
        "Users",
        "Jobs",
        "Transactions",
        "SePayWebhookTransactions",
        "SePayPaymentRequests"
    ];

    private static readonly string RootDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        ApplicationFolderName);

    public static string DataDirectory => Path.Combine(RootDirectory, "data");
    public static string ResourcesDirectory => Path.Combine(RootDirectory, "resources");
    public static string ProfilesDirectory => Path.Combine(DataDirectory, "Profiles");
    public static string BrowserPoolDirectory => Path.Combine(DataDirectory, "BrowserPool");
    public static string DiagnosticsDirectory => Path.Combine(DataDirectory, "diagnostics");
    public static string MetricsDirectory => Path.Combine(DataDirectory, "metrics");
    public static string SettingsPath => Path.Combine(RootDirectory, "settings.json");
    public static string DatabasePath => Path.Combine(DataDirectory, "vibecodereg.db");
    public static string AllowedUsersPath => Path.Combine(DataDirectory, "allowed_users.json");
    public static string ProfilesDataPath => Path.Combine(DataDirectory, "profiles_data.json");
    public static string SuccessPath => Path.Combine(DataDirectory, "success.txt");
    public static string WebRootPath => Path.Combine(ResourcesDirectory, "wwwroot");

    public static void Initialize(bool createSettingsFile)
    {
        Directory.CreateDirectory(RootDirectory);
        Directory.CreateDirectory(DataDirectory);
        Directory.CreateDirectory(ResourcesDirectory);
        Directory.CreateDirectory(ProfilesDirectory);
        Directory.CreateDirectory(BrowserPoolDirectory);
        Directory.CreateDirectory(DiagnosticsDirectory);
        Directory.CreateDirectory(MetricsDirectory);

        MigrateLegacyData();
        ExtractRuntimeAssets();

        if (createSettingsFile && !File.Exists(SettingsPath))
        {
            var templatePath = Path.Combine(ResourcesDirectory, "settings.template.json");
            if (File.Exists(templatePath))
            {
                File.Copy(templatePath, SettingsPath);
            }
        }
    }

    public static string GetResourcePath(params string[] segments)
    {
        ArgumentNullException.ThrowIfNull(segments);
        return Path.Combine([ResourcesDirectory, .. segments]);
    }

    private static void MigrateLegacyData()
    {
        // Old console builds resolved SQLite relative to the launch directory,
        // which is usually the repository root. The first standalone build
        // instead lives beneath artifacts, so look through its nearby parent
        // folders as well as the launch directory.
        var legacyRoots = GetLegacyDataRoots().ToList();
        var destinationState = GetDatabaseDataState(DatabasePath);
        if (destinationState is DatabaseDataState.Missing or DatabaseDataState.Empty)
        {
            foreach (var root in legacyRoots)
            {
                var sourceDatabase = Path.Combine(root, "vibecodereg.db");
                if (GetDatabaseDataState(sourceDatabase) == DatabaseDataState.HasData
                    && TryMigrateDatabase(sourceDatabase))
                {
                    Console.WriteLine($"Imported existing data from '{root}'.");
                    break;
                }
            }
        }

        foreach (var root in legacyRoots)
        {
            foreach (var fileName in MutableFileNames)
            {
                var source = Path.Combine(root, fileName);
                var destination = Path.Combine(DataDirectory, fileName);
                if (!File.Exists(destination) && File.Exists(source))
                {
                    TryCopy(source, destination);
                }
            }

            if (!Directory.EnumerateFileSystemEntries(ProfilesDirectory).Any())
            {
                TryCopyDirectory(Path.Combine(root, "Profiles"), ProfilesDirectory);
            }
        }
    }

    private static IEnumerable<string> GetLegacyDataRoots()
    {
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void AddRoot(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            try
            {
                var fullPath = Path.GetFullPath(path);
                if (!string.Equals(fullPath, DataDirectory, StringComparison.OrdinalIgnoreCase))
                {
                    roots.Add(fullPath);
                }
            }
            catch
            {
            }
        }

        AddRoot(Directory.GetCurrentDirectory());
        AddRoot(AppContext.BaseDirectory);

        var parent = Directory.GetParent(AppContext.BaseDirectory);
        for (var depth = 0; parent is not null && depth < 5; depth++, parent = parent.Parent)
        {
            AddRoot(parent.FullName);
        }

        return roots;
    }

    private static bool TryMigrateDatabase(string sourceDatabase)
    {
        var backupDirectory = Path.Combine(
            DataDirectory,
            "migration-backups",
            DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N"));
        var temporaryDatabase = DatabasePath + "." + Guid.NewGuid().ToString("N") + ".migration";

        try
        {
            Directory.CreateDirectory(backupDirectory);
            MoveCurrentDatabaseAside(backupDirectory);

            using (var source = OpenDatabase(sourceDatabase, SqliteOpenMode.ReadOnly))
            using (var destination = OpenDatabase(temporaryDatabase, SqliteOpenMode.ReadWriteCreate))
            {
                source.BackupDatabase(destination);
            }

            File.Move(temporaryDatabase, DatabasePath);
            return true;
        }
        catch
        {
            RestoreDatabaseFromBackup(backupDirectory);
            return false;
        }
        finally
        {
            TryDelete(temporaryDatabase);
        }
    }

    private static SqliteConnection OpenDatabase(string path, SqliteOpenMode mode)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = mode,
            Pooling = false
        }.ToString());
        connection.Open();
        return connection;
    }

    private static void MoveCurrentDatabaseAside(string backupDirectory)
    {
        foreach (var fileName in DatabaseFileNames)
        {
            var source = Path.Combine(DataDirectory, fileName);
            if (!File.Exists(source))
            {
                continue;
            }

            File.Move(source, Path.Combine(backupDirectory, fileName));
        }
    }

    private static void RestoreDatabaseFromBackup(string backupDirectory)
    {
        foreach (var fileName in DatabaseFileNames)
        {
            var backupFile = Path.Combine(backupDirectory, fileName);
            var destination = Path.Combine(DataDirectory, fileName);
            if (File.Exists(backupFile) && !File.Exists(destination))
            {
                try
                {
                    File.Move(backupFile, destination);
                }
                catch
                {
                    // The backup remains available for manual recovery.
                }
            }
        }
    }

    private static DatabaseDataState GetDatabaseDataState(string databasePath)
    {
        if (!File.Exists(databasePath))
        {
            return DatabaseDataState.Missing;
        }

        try
        {
            using var connection = OpenDatabase(databasePath, SqliteOpenMode.ReadOnly);
            long users = 0;
            long operationalRecords = 0;
            foreach (var table in MeaningfulDataTables)
            {
                using var tableCheck = connection.CreateCommand();
                tableCheck.CommandText =
                    "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = $tableName";
                tableCheck.Parameters.AddWithValue("$tableName", table);
                if (Convert.ToInt64(tableCheck.ExecuteScalar()) == 0)
                {
                    continue;
                }

                using var rowCheck = connection.CreateCommand();
                rowCheck.CommandText = $"SELECT COUNT(*) FROM \"{table}\"";
                var rowCount = Convert.ToInt64(rowCheck.ExecuteScalar());
                if (string.Equals(table, "Users", StringComparison.Ordinal))
                {
                    users = rowCount;
                }
                else
                {
                    operationalRecords += rowCount;
                }
            }

            // A brand-new standalone host may create one administrator row
            // before configuration validation stops startup. Treat that as an
            // empty bootstrap database so the previous console database can
            // still be imported on the next launch.
            return operationalRecords > 0 || users > 1
                ? DatabaseDataState.HasData
                : DatabaseDataState.Empty;
        }
        catch
        {
            return DatabaseDataState.Unknown;
        }
    }

    private static void ExtractRuntimeAssets()
    {
        var assembly = typeof(RuntimePaths).Assembly;
        foreach (var resourceName in assembly.GetManifestResourceNames()
                     .Where(name => name.StartsWith(ResourcePrefix, StringComparison.Ordinal)))
        {
            var relativePath = resourceName[ResourcePrefix.Length..]
                .Replace('/', Path.DirectorySeparatorChar);
            if (string.IsNullOrWhiteSpace(relativePath)
                || Path.IsPathRooted(relativePath)
                || relativePath.Split(Path.DirectorySeparatorChar).Any(segment => segment is "." or ".."))
            {
                continue;
            }

            var destination = Path.GetFullPath(Path.Combine(ResourcesDirectory, relativePath));
            var resourceRoot = Path.GetFullPath(ResourcesDirectory) + Path.DirectorySeparatorChar;
            if (!destination.StartsWith(resourceRoot, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                using var input = assembly.GetManifestResourceStream(resourceName);
                if (input is null)
                {
                    continue;
                }

                var temporaryPath = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    using (var output = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    {
                        input.CopyTo(output);
                    }

                    File.Move(temporaryPath, destination, overwrite: true);
                }
                finally
                {
                    TryDelete(temporaryPath);
                }
            }
            catch
            {
                // Startup continues so a diagnostic message can explain a
                // missing asset instead of terminating in the extractor.
            }
        }
    }

    private static void TryCopy(string source, string destination)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(source, destination, overwrite: false);
        }
        catch
        {
            // Legacy migration is best effort only.
        }
    }

    private static void TryCopyDirectory(string source, string destination)
    {
        if (!Directory.Exists(source))
        {
            return;
        }

        try
        {
            foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(source, directory);
                Directory.CreateDirectory(Path.Combine(destination, relative));
            }

            foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(source, file);
                var target = Path.Combine(destination, relative);
                if (!File.Exists(target))
                {
                    TryCopy(file, target);
                }
            }
        }
        catch
        {
            // A locked browser profile should not prevent the app from starting.
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
        }
    }

    private enum DatabaseDataState
    {
        Missing,
        Empty,
        HasData,
        Unknown
    }
}
