using System.Text.Json;

namespace BAOCODETOOL.Core;

public static class JavaScriptValueEncoder
{
    public static string StringLiteral(string? value) =>
        JsonSerializer.Serialize(value ?? string.Empty);
}
