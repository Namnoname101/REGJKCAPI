using System;
using System.IO;
using VibecodeReg.Models;

namespace VibecodeReg.Core;

public static class ResultWriter
{
    private static readonly object SyncObj = new();

    public static void AppendSuccess(AccountModel account, TargetSite site)
    {
        var outputPath = Path.Combine(AppContext.BaseDirectory, "success.txt");
        var line = $"{site}|{account.Username}|{account.Password}|{account.FullName}|{account.AccountNumber}|{account.Level2Password}|";

        lock (SyncObj)
        {
            File.AppendAllText(outputPath, line + Environment.NewLine);
        }
    }
}
