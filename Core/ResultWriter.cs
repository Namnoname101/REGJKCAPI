using System;
using System.IO;
using BAOCODETOOL.Models;

namespace BAOCODETOOL.Core;

public static class ResultWriter
{
    private static readonly object SyncObj = new();

    public static void AppendSuccess(AccountModel account, TargetSite site)
    {
        AppendRegisteredAccount(account, site);
    }

    public static void AppendRegisteredAccount(AccountModel account, TargetSite site)
    {
        var outputPath = RuntimePaths.SuccessPath;
        var line = $"{site}|{account.GetUsername(site)}|{account.GetPassword(site)}|{account.FullName}|{account.AccountNumber}|{account.Level2Password}|";

        lock (SyncObj)
        {
            File.AppendAllText(outputPath, line + Environment.NewLine);
        }
    }
}
