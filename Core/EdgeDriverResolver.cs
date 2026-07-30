using System.Diagnostics;
using System.IO.Compression;

namespace BAOCODETOOL.Core;

internal static class EdgeDriverResolver
{
    private static readonly object Sync = new();
    private static readonly HttpClient DownloadClient = new()
    {
        Timeout = TimeSpan.FromSeconds(60)
    };

    public static string ResolveEdgeBinaryPath()
    {
        var candidates = new[]
        {
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                "Microsoft", "Edge", "Application", "msedge.exe"),
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "Microsoft", "Edge", "Application", "msedge.exe")
        };
        return candidates.FirstOrDefault(File.Exists)
               ?? throw new FileNotFoundException("Microsoft Edge is not installed.");
    }

    public static string ResolveDriverDirectory()
    {
        var overrideDirectory = Environment.GetEnvironmentVariable("VIBECODE_EDGE_DRIVER_DIR");
        if (!string.IsNullOrWhiteSpace(overrideDirectory)
            && File.Exists(Path.Combine(overrideDirectory, "msedgedriver.exe")))
        {
            return overrideDirectory;
        }

        var edgePath = ResolveEdgeBinaryPath();
        var version = FileVersionInfo.GetVersionInfo(edgePath).FileVersion;
        if (string.IsNullOrWhiteSpace(version))
        {
            throw new InvalidOperationException("The installed Edge version could not be detected.");
        }

        var targetDirectory = Path.Combine(
            RuntimePaths.DataDirectory,
            "drivers",
            "edge",
            version);
        var targetDriver = Path.Combine(targetDirectory, "msedgedriver.exe");
        if (File.Exists(targetDriver))
        {
            return targetDirectory;
        }

        lock (Sync)
        {
            if (File.Exists(targetDriver))
            {
                return targetDirectory;
            }

            DownloadDriver(version, targetDirectory, targetDriver);
            return targetDirectory;
        }
    }

    private static void DownloadDriver(
        string version,
        string targetDirectory,
        string targetDriver)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(targetDirectory)!);
        var stagingDirectory = targetDirectory + ".staging-" + Guid.NewGuid().ToString("N");
        var archivePath = Path.Combine(stagingDirectory, "edgedriver_win64.zip");

        try
        {
            Directory.CreateDirectory(stagingDirectory);
            var uri = new Uri(
                $"https://msedgedriver.microsoft.com/{Uri.EscapeDataString(version)}/edgedriver_win64.zip");
            using var response = DownloadClient.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead)
                .GetAwaiter()
                .GetResult();
            response.EnsureSuccessStatusCode();
            using (var input = response.Content.ReadAsStream())
            using (var output = new FileStream(
                       archivePath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None))
            {
                input.CopyTo(output);
            }

            ZipFile.ExtractToDirectory(archivePath, stagingDirectory, overwriteFiles: true);
            var extractedDriver = Directory
                .EnumerateFiles(stagingDirectory, "msedgedriver.exe", SearchOption.AllDirectories)
                .FirstOrDefault()
                ?? throw new InvalidDataException("The Edge driver archive did not contain msedgedriver.exe.");

            Directory.CreateDirectory(targetDirectory);
            File.Copy(extractedDriver, targetDriver, overwrite: true);
        }
        finally
        {
            TryDeleteDirectory(stagingDirectory);
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
        }
    }
}
