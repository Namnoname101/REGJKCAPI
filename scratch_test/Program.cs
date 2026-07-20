using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using VibecodeReg.Models;
using VibecodeReg.Core;
using VibecodeReg.Strategies;
using OpenQA.Selenium;

class Program
{
    static async Task Main(string[] args)
    {
        Console.WriteLine("Starting test...");
        var account = new AccountModel
        {
            Username = "namshexs4",
            Password = "ssssssss@e355R",
            PhoneNumber = "774463527",
            FullName = "TRAN ANH HUY",
            Proxy = "118.68.126.40:26244:isabella671:otqymzuymzcy",
            UserAgent = "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/605.1.15 (KHTML, like Gecko)"
        };
        var siteRun = new SiteRunModel { Site = TargetSite.RR88 };
        
        var cts = new CancellationTokenSource();
        var mre = new ManualResetEventSlim(true);
        var execCtx = new AutomationExecutionContext(cts.Token, mre);

        try
        {
            Console.WriteLine("Creating driver...");
            using var driver = DriverFactory.CreateDriver(account, TargetSite.RR88);
            Console.WriteLine("Driver created. Executing strategy...");
            
            var strategy = new RR88Strategy();
            
            // Set up a background thread to take a screenshot after 10 seconds to see the page state
            _ = Task.Run(() => 
            {
                Thread.Sleep(12000);
                try {
                    ((ITakesScreenshot)driver).GetScreenshot().SaveAsFile("screenshot1.png");
                    Console.WriteLine("Took screenshot1.png after 12s");
                } catch { }
            });
            
            bool result = strategy.ExecuteRegistration(driver, account, siteRun, execCtx);
            Console.WriteLine("Strategy result: " + result);
            
            try {
                ((ITakesScreenshot)driver).GetScreenshot().SaveAsFile("screenshot2.png");
                Console.WriteLine("Took screenshot2.png at end");
            } catch { }
        }
        catch (Exception ex)
        {
            Console.WriteLine("Exception: " + ex);
        }
    }
}
