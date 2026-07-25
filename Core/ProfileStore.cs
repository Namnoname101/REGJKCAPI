using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using BAOCODETOOL.Models;

namespace BAOCODETOOL.Core;

public static class ProfileStore
{
    private static readonly string SaveFilePath = Path.Combine(AppContext.BaseDirectory, "profiles_data.json");
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public class ProfileSaveData
    {
        public Guid ProfileId { get; set; }
        public string Username { get; set; } = string.Empty;
        public string Password { get; set; } = string.Empty;
        public string PhoneNumber { get; set; } = string.Empty;
        public string FullName { get; set; } = string.Empty;
        public string Level2Password { get; set; } = string.Empty;
        public string AccountNumber { get; set; } = string.Empty;
        public string BankName { get; set; } = string.Empty;
        public string Branch { get; set; } = string.Empty;
        public string Proxy { get; set; } = string.Empty;
        public string UserAgent { get; set; } = string.Empty;
        public Dictionary<TargetSite, bool> SelectedSites { get; set; } = new();
        public Dictionary<TargetSite, AccountStatus> SiteStatuses { get; set; } = new();
    }

    public static void SaveProfiles(IEnumerable<AccountModel> profiles)
    {
        try
        {
            var data = profiles.Select(p => new ProfileSaveData
            {
                ProfileId = p.ProfileId,
                Username = p.Username,
                Password = p.Password,
                PhoneNumber = p.PhoneNumber,
                FullName = p.FullName,
                Level2Password = p.Level2Password,
                AccountNumber = p.AccountNumber,
                BankName = p.BankName,
                Branch = p.Branch,
                Proxy = p.Proxy,
                UserAgent = p.UserAgent,
                SelectedSites = p.SiteRuns.ToDictionary(sr => sr.Site, sr => sr.IsSelected),
                SiteStatuses = p.SiteRuns.ToDictionary(sr => sr.Site, sr => sr.Status)
            }).ToList();

            var json = JsonSerializer.Serialize(data, JsonOptions);
            // Write beside the destination and replace it in one move. A
            // process termination or disk hiccup must not leave a truncated
            // profiles_data.json that prevents the next startup from loading
            // every account.
            var temporaryPath = SaveFilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temporaryPath, json);
                File.Move(temporaryPath, SaveFilePath, overwrite: true);
            }
            finally
            {
                try
                {
                    if (File.Exists(temporaryPath))
                    {
                        File.Delete(temporaryPath);
                    }
                }
                catch
                {
                }
            }
        }
        catch (Exception exception)
        {
            // Persistence must not crash an active registration, but retain a
            // breadcrumb for operators diagnosing a full/locked disk.
            Debug.WriteLine($"Could not save profiles: {exception}");
        }
    }

    public static List<AccountModel> LoadProfiles()
    {
        var profiles = new List<AccountModel>();

        if (!File.Exists(SaveFilePath))
        {
            return profiles;
        }

        try
        {
            var json = File.ReadAllText(SaveFilePath);
            var data = JsonSerializer.Deserialize<List<ProfileSaveData>>(json, JsonOptions);

            if (data == null)
            {
                return profiles;
            }

            foreach (var item in data)
            {
                var account = new AccountModel
                {
                    ProfileId = item.ProfileId,
                    Username = item.Username,
                    Password = item.Password,
                    PhoneNumber = item.PhoneNumber,
                    FullName = item.FullName,
                    Level2Password = item.Level2Password,
                    AccountNumber = item.AccountNumber,
                    BankName = item.BankName,
                    Branch = item.Branch,
                    Proxy = item.Proxy,
                    UserAgent = item.UserAgent
                };

                foreach (var siteRun in account.SiteRuns)
                {
                    if (item.SelectedSites.TryGetValue(siteRun.Site, out var isSelected))
                    {
                        siteRun.IsSelected = isSelected;
                    }
                    else
                    {
                        siteRun.IsSelected = false; // Default to false if not found in save
                    }

                    if (item.SiteStatuses != null && item.SiteStatuses.TryGetValue(siteRun.Site, out var status))
                    {
                        siteRun.Status = status;
                        if (status == AccountStatus.Success)
                        {
                            siteRun.LogMessage = "Skipped (Already Registered).";
                        }
                        else
                        {
                            siteRun.LogMessage = siteRun.IsSelected ? "Ready." : "Skipped.";
                        }
                    }
                    else
                    {
                        siteRun.Status = AccountStatus.Ready;
                        siteRun.LogMessage = siteRun.IsSelected ? "Ready." : "Skipped.";
                    }
                }

                profiles.Add(account);
            }
        }
        catch
        {
            // If the file is corrupted, return whatever we managed to load
        }

        return profiles;
    }
}
