using System;

namespace BAOCODETOOL.Core;

internal static class ShortUsernameGenerator
{
    private static readonly string[] MeaningfulNames =
    [
        "hoang", "nguyen", "thanh", "minh", "tuan", "dung", "hung", "duc",
        "linh", "phuong", "thao", "trang", "quang", "long", "nhat", "khanh",
        "hieu", "thang", "kien", "phuc", "binh", "phong", "cuong", "thien",
        "ngoc", "xuan", "dong", "hong", "giang", "tuananh", "minhtri",
        "giahan", "baolong", "thienan", "nhathuy", "quocdat", "linhchi",
        "sonhai", "tailoc", "haidang", "vananh", "minhan", "anphuc"
    ];

    public static string Next()
    {
        // Use a meaningful human name combined with 5 or 6 random digits.
        // This ensures a natural human appearance while keeping collision probability near zero (< 10-12 chars).
        var name = MeaningfulNames[Random.Shared.Next(MeaningfulNames.Length)];
        var suffix = name.Length <= 5
            ? Random.Shared.Next(100_000, 1_000_000).ToString()
            : Random.Shared.Next(10_000, 100_000).ToString();

        return $"{name}{suffix}";
    }
}
