using OpenQA.Selenium;
using OpenQA.Selenium.Interactions;
using System;
using System.Threading.Tasks;

namespace BAOCODETOOL.Core;

public static class SeleniumHumanExtensions
{
    public static void RandomPause(int min = 200, int max = 800)
    {
        if (max <= min)
        {
            max = min + 1;
        }

        Task.Delay(new Random().Next(min, max)).Wait();
    }

    public static void ClickLikeHuman(this IWebElement element, IWebDriver driver)
    {
        ArgumentNullException.ThrowIfNull(element);
        ArgumentNullException.ThrowIfNull(driver);

        ScrollIntoView(driver, element);
        
        try 
        { 
            element.Click(); 
        } 
        catch 
        {
            // Fallback to JS click if normal click fails
            ((IJavaScriptExecutor)driver).ExecuteScript("arguments[0].click();", element);
        }
    }

    public static void SendKeysLikeHuman(this IWebElement element, IWebDriver driver, string text)
    {
        ArgumentNullException.ThrowIfNull(element);
        ArgumentNullException.ThrowIfNull(driver);

        ScrollIntoView(driver, element);
        
        try { element.Click(); } catch { }
        try { element.Clear(); } catch { }

        // Tối ưu tốc độ: Nhập toàn bộ chuỗi ngay lập tức thay vì gõ từng phím
        if (!string.IsNullOrEmpty(text))
        {
            element.SendKeys(text);
        }
    }

    private static void ScrollIntoView(IWebDriver driver, IWebElement element)
    {
        if (driver is IJavaScriptExecutor javaScriptExecutor)
        {
            javaScriptExecutor.ExecuteScript(
                "arguments[0].scrollIntoView({block: 'center'});",
                element);
            Task.Delay(100).Wait(); // Nghỉ một nhịp ngắn sau khi cuộn để DOM ổn định
        }
    }
}
