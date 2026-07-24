using OpenQA.Selenium;
using System;
using System.Threading.Tasks;

namespace BAOCODETOOL.Core;

public static class SeleniumExtensions
{
    public static void SendKeysHumanLike(
        this IWebElement element,
        string text,
        AutomationExecutionContext? executionContext = null)
    {
        ArgumentNullException.ThrowIfNull(element);

        foreach (var character in text ?? string.Empty)
        {
            executionContext?.Checkpoint();
            element.SendKeys(character.ToString());

            var delay = TimeSpan.FromMilliseconds(Random.Shared.Next(50, 150));
            if (executionContext is null)
            {
                Task.Delay(delay).GetAwaiter().GetResult();
            }
            else
            {
                executionContext.Delay(delay);
            }
        }
    }
}
