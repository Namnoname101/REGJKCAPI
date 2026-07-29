using System.Reflection;
using System.Text.Json;
using BAOCODETOOL.Models;
using BAOCODETOOL.Services;

var readProxyResult = typeof(ProxyVnService).GetMethod(
    "ReadProxyResult",
    BindingFlags.NonPublic | BindingFlags.Static)
    ?? throw new InvalidOperationException("Proxy.vn response parser was not found.");
var readProxyResults = typeof(ProxyVnService).GetMethod(
    "ReadProxyResults",
    BindingFlags.NonPublic | BindingFlags.Static)
    ?? throw new InvalidOperationException("Proxy.vn batch response parser was not found.");
var buildBuyUrl = typeof(ProxyVnService).GetMethod(
    "BuildBuyUrl",
    BindingFlags.NonPublic | BindingFlags.Static)
    ?? throw new InvalidOperationException("Proxy.vn purchase URL builder was not found.");
var resolveProxyCategory = typeof(ProxyVnService).GetMethod(
    "ResolveProxyCategory",
    BindingFlags.NonPublic | BindingFlags.Static)
    ?? throw new InvalidOperationException("Proxy.vn category resolver was not found.");

AssertCandidates(
    """
    [
      {
        "status": 100,
        "idproxy": 42,
        "ip": "192.0.2.10",
        "port": 3210,
        "user": "sample-user",
        "password": "sample-pass",
        "type": "HTTPS",
        "proxy": "198.51.100.20:3210:sample-user:sample-pass"
      },
      {
        "status": 200,
        "comen": "You have successfully purchased 1"
      }
    ]
    """,
    [
        "192.0.2.10:3210:sample-user:sample-pass",
        "198.51.100.20:3210:sample-user:sample-pass"
    ]);

AssertCandidates(
    """
    {
      "status": "100",
      "idproxy": "43",
      "type": "HTTP",
      "proxy": "198.51.100.30:8080:fallback-user:fallback-pass"
    }
    """,
    ["198.51.100.30:8080:fallback-user:fallback-pass"]);

AssertCandidates(
    """
    {
      "status": 100,
      "idproxy": 44,
      "ip": "192.0.2.40",
      "port": "9090",
      "user": "same-user",
      "password": "same-pass",
      "proxy": "192.0.2.40:9090:same-user:same-pass"
    }
    """,
    ["192.0.2.40:9090:same-user:same-pass"]);

AssertCandidates(
    """
    {
      "status": 100,
      "idproxy": 45,
      "ip": "192.0.2.50",
      "port": 10080,
      "user": "valid-user",
      "password": "valid-pass",
      "proxy": "not-a-valid-proxy"
    }
    """,
    ["192.0.2.50:10080:valid-user:valid-pass"]);

AssertBatchCount(
    """
    [
      {
        "status": 100,
        "idproxy": 101,
        "proxy": "192.0.2.101:8080:batch-user:batch-pass"
      },
      {
        "status": 100,
        "idproxy": 102,
        "proxy": "192.0.2.102:8080:batch-user:batch-pass"
      },
      {
        "status": 201,
        "comen": "Đã mua thành công nhưng không đủ số lượng"
      }
    ]
    """,
    2);

var purchaseUrl = (string?)buildBuyUrl.Invoke(null, ["key", "VNPT", 3, "HTTP", 7])
    ?? throw new InvalidOperationException("Proxy.vn purchase URL builder returned null.");
if (!purchaseUrl.Contains("soluong=7", StringComparison.Ordinal))
{
    throw new InvalidOperationException($"Expected quantity in purchase URL, got '{purchaseUrl}'.");
}

AssertCategory("vnpt", "VNPT");
AssertCategory("VIETTEL", "Viettel");
AssertCategory("fpt", "FPT");

var allowedRandomCategories = new HashSet<string>(["VNPT", "Viettel", "FPT"]);
for (var index = 0; index < 20; index++)
{
    var resolved = ResolveCategory("ALL");
    if (!allowedRandomCategories.Contains(resolved))
    {
        throw new InvalidOperationException($"ALL resolved to unsupported category '{resolved}'.");
    }
}

Console.WriteLine("PASS Proxy.vn response parsing: original endpoint is preferred, intermediary fallback is retained, and duplicates are removed.");
return 0;

void AssertCandidates(string json, IReadOnlyList<string> expected)
{
    using var document = JsonDocument.Parse(json);
    var parsed = readProxyResult.Invoke(null, [document.RootElement, json])
        ?? throw new InvalidOperationException("Proxy.vn response parser returned null.");

    var candidateProperty = parsed.GetType().GetProperty("CandidateProxies")
        ?? throw new InvalidOperationException("CandidateProxies was not found.");
    var actual = (IReadOnlyList<string>?)candidateProperty.GetValue(parsed)
        ?? throw new InvalidOperationException("CandidateProxies was null.");

    if (!actual.SequenceEqual(expected, StringComparer.Ordinal))
    {
        throw new InvalidOperationException(
            $"Candidate mismatch. Expected=[{string.Join(", ", expected)}] Actual=[{string.Join(", ", actual)}]");
    }
}

void AssertBatchCount(string json, int expectedCount)
{
    using var document = JsonDocument.Parse(json);
    var parsed = readProxyResults.Invoke(null, [document.RootElement, json])
        ?? throw new InvalidOperationException("Proxy.vn batch response parser returned null.");
    var actualCount = (int?)parsed.GetType().GetProperty("Count")?.GetValue(parsed)
        ?? throw new InvalidOperationException("Proxy.vn batch response count was not found.");
    if (actualCount != expectedCount)
    {
        throw new InvalidOperationException(
            $"Batch count mismatch. Expected={expectedCount} Actual={actualCount}");
    }
}

void AssertCategory(string input, string expected)
{
    var actual = ResolveCategory(input);
    if (!string.Equals(actual, expected, StringComparison.Ordinal))
    {
        throw new InvalidOperationException(
            $"Category mismatch. Input={input} Expected={expected} Actual={actual}");
    }
}

string ResolveCategory(string category)
{
    return (string?)resolveProxyCategory.Invoke(
        null,
        [new ProxyPackage { Category = category }])
        ?? throw new InvalidOperationException("Proxy.vn category resolver returned null.");
}
