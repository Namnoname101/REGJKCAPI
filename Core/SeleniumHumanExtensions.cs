using OpenQA.Selenium;
using OpenQA.Selenium.Interactions;
using System;
using System.Threading.Tasks;

namespace VibecodeReg.Core;

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
        RandomPause(100, 300);

        new Actions(driver)
            .MoveToElement(element)
            .Pause(TimeSpan.FromMilliseconds(new Random().Next(100, 300)))
            .ClickAndHold(element)
            .Pause(TimeSpan.FromMilliseconds(new Random().Next(50, 150)))
            .Release(element)
            .Perform();

        RandomPause(300, 700);
    }

    public static void SendKeysLikeHuman(this IWebElement element, IWebDriver driver, string text)
    {
        ArgumentNullException.ThrowIfNull(element);
        ArgumentNullException.ThrowIfNull(driver);

        ScrollIntoView(driver, element);
        RandomPause(300, 1000);

        element.Click();
        RandomPause(100, 300);
        element.Clear();

        foreach (var character in text ?? string.Empty)
        {
            element.SendKeys(character.ToString());
            RandomPause(20, 60);

            if (Random.Shared.NextDouble() < 0.08)
            {
                RandomPause(300, 900);
            }
        }

        RandomPause(200, 500);
    }

    private static void ScrollIntoView(IWebDriver driver, IWebElement element)
    {
        if (driver is IJavaScriptExecutor javaScriptExecutor)
        {
            javaScriptExecutor.ExecuteScript(
                "arguments[0].scrollIntoView({behavior: 'smooth', block: 'center'});",
                element);
        }
    }
}
