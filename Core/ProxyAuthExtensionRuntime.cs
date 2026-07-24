using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace BAOCODETOOL.Core;

internal static class ProxyAuthExtensionRuntime
{
    private const string ExtensionFolderName = "BAOCODETOOLProxyExtension";

    public static string CreateOrUpdate(string profileFolderPath, ProxyConfiguration proxyConfiguration)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileFolderPath);
        ArgumentNullException.ThrowIfNull(proxyConfiguration);

        var extensionDirectory = Path.Combine(profileFolderPath, ExtensionFolderName);
        Directory.CreateDirectory(extensionDirectory);

        WriteAllText(Path.Combine(extensionDirectory, "manifest.json"), CreateManifest());
        WriteAllText(Path.Combine(extensionDirectory, "background.js"), CreateBackgroundScript(proxyConfiguration));

        return extensionDirectory;
    }

    public static void WaitForBootstrap()
    {
        Thread.Sleep(1200);
    }

    public static void Cleanup(string? extensionDirectory)
    {
        if (string.IsNullOrWhiteSpace(extensionDirectory) || !Directory.Exists(extensionDirectory))
        {
            return;
        }

        for (var attempt = 1; attempt <= 5; attempt++)
        {
            try
            {
                Directory.Delete(extensionDirectory, recursive: true);
                return;
            }
            catch (IOException) when (attempt < 5)
            {
                Thread.Sleep(200);
            }
            catch (UnauthorizedAccessException) when (attempt < 5)
            {
                Thread.Sleep(200);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Debug.WriteLine($"Proxy auth extension cleanup warning: {ex.Message}");
                return;
            }
        }
    }

    private static void WriteAllText(string path, string content)
    {
        File.WriteAllText(path, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    private static string CreateManifest() =>
        """
        {
          "version": "1.0.0",
          "manifest_version": 3,
          "name": "BAOCODETOOL Proxy",
          "description": "Per-profile proxy injector for BAOCODETOOL browser sessions.",
          "permissions": [
            "proxy",
            "storage",
            "webRequest",
            "webRequestAuthProvider"
          ],
          "host_permissions": [
            "<all_urls>"
          ],
          "background": {
            "service_worker": "background.js"
          },
          "action": {
            "default_title": "BAOCODETOOL Proxy"
          },
          "minimum_chrome_version": "120.0.0"
        }
        """;

    private static string CreateBackgroundScript(ProxyConfiguration proxyConfiguration)
    {
        var scheme = JsonSerializer.Serialize(proxyConfiguration.Scheme);
        var host = JsonSerializer.Serialize(proxyConfiguration.Host);
        var username = JsonSerializer.Serialize(proxyConfiguration.Username ?? string.Empty);
        var password = JsonSerializer.Serialize(proxyConfiguration.Password ?? string.Empty);
        var port = proxyConfiguration.Port.ToString(CultureInfo.InvariantCulture);

        return $$"""
        const proxyState = {
          scheme: {{scheme}},
          host: {{host}},
          port: {{port}},
          username: {{username}},
          password: {{password}}
        };

        const retryByRequestId = new Map();

        const proxyConfig = {
          mode: "fixed_servers",
          rules: {
            singleProxy: {
              scheme: proxyState.scheme,
              host: proxyState.host,
              port: proxyState.port
            },
            bypassList: ["localhost", "127.0.0.1", "[::1]"]
          }
        };

        function hasCredentials() {
          return proxyState.username.length > 0 && proxyState.password.length > 0;
        }

        function normalizeHost(host) {
          return String(host || "").replace(/^\[|\]$/g, "").toLowerCase();
        }

        function isExpectedProxyChallenge(details) {
          if (details.isProxy === true) {
            return true;
          }

          const challenger = details.challenger || {};
          return normalizeHost(challenger.host) === normalizeHost(proxyState.host)
            && Number(challenger.port) === Number(proxyState.port);
        }

        function applyProxyConfig() {
          chrome.proxy.settings.set({ value: proxyConfig, scope: "regular" });
        }

        chrome.runtime.onInstalled.addListener(applyProxyConfig);
        chrome.runtime.onStartup.addListener(applyProxyConfig);
        applyProxyConfig();

        chrome.webRequest.onCompleted.addListener(
          details => retryByRequestId.delete(details.requestId),
          { urls: ["<all_urls>"] }
        );

        chrome.webRequest.onErrorOccurred.addListener(
          details => retryByRequestId.delete(details.requestId),
          { urls: ["<all_urls>"] }
        );

        chrome.webRequest.onAuthRequired.addListener(
          (details, callback) => {
            if (!isExpectedProxyChallenge(details) || !hasCredentials()) {
              callback({});
              return;
            }

            const attempts = (retryByRequestId.get(details.requestId) || 0) + 1;
            retryByRequestId.set(details.requestId, attempts);

            if (attempts > 3) {
              callback({ cancel: true });
              return;
            }

            callback({
              authCredentials: {
                username: proxyState.username,
                password: proxyState.password
              }
            });
          },
          { urls: ["<all_urls>"] },
          ["asyncBlocking"]
        );
        """;
    }
}
