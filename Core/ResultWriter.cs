using System;
using System.IO;
using BAOCODETOOL.Models;

namespace BAOCODETOOL.Core;

public static class ResultWriter
{
    private static readonly object SyncObj = new();

    public static void AppendSuccess(AccountModel account, TargetSite site)
    {
        var outputPath = Path.Combine(AppContext.BaseDirectory, "success.txt");
        var line = $"{site}|{account.GetUsername(site)}|{account.GetPassword(site)}|{account.FullName}|{account.AccountNumber}|{account.Level2Password}|";

        lock (SyncObj)
        {
            File.AppendAllText(outputPath, line + Environment.NewLine);
        }
    }
}
