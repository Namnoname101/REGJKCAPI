using System;
using BAOCODETOOL.Models;

namespace BAOCODETOOL.Strategies;

public static class StrategyFactory
{
    public static ISiteAutomationStrategy Create(TargetSite site) =>
        site switch
        {
            TargetSite.RR88 => new RR88Strategy(),
            TargetSite.MM88 => new MM88Strategy(),
            TargetSite.XX88 => new XX88Strategy(),
            TargetSite.LLWIN => new LLWINStrategy(),
            TargetSite.GG88 => new GG88Strategy(),
            _ => throw new NotSupportedException($"No strategy registered for {site}.")
        };
}
