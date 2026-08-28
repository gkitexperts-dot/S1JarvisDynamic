using System;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using S1Jarvis.Core;

namespace S1Jarvis.UI
{
    public partial class JarvisShell
    {
        private bool _welcomeStoresStockHooked;
        private bool _welcomeStoresStockInjected;

        private void WelcomeStoresStock_Loaded(object sender, RoutedEventArgs e)
        {
            if (_welcomeStoresStockHooked) return;
            _welcomeStoresStockHooked = true;

            if (webView.CoreWebView2 != null)
            {
                HookWelcomeStoresStockWebView();
                return;
            }

            webView.CoreWebView2InitializationCompleted += WelcomeStoresStock_CoreWebView2InitializationCompleted;
        }

        private void WelcomeStoresStock_CoreWebView2InitializationCompleted(
            object sender,
            CoreWebView2InitializationCompletedEventArgs e)
        {
            if (!e.IsSuccess || webView.CoreWebView2 == null)
            {
                DebugLog.Log("[WS-STOCK] WebView2 initialization failed: " + e.InitializationException);
                return;
            }

            HookWelcomeStoresStockWebView();
        }

        private void HookWelcomeStoresStockWebView()
        {
            webView.CoreWebView2.WebMessageReceived -= WelcomeStoresStock_WebMessageReceived;
            webView.CoreWebView2.WebMessageReceived += WelcomeStoresStock_WebMessageReceived;
            webView.CoreWebView2.NavigationCompleted -= WelcomeStoresStock_NavigationCompleted;
            webView.CoreWebView2.NavigationCompleted += WelcomeStoresStock_NavigationCompleted;
        }

        private async void WelcomeStoresStock_NavigationCompleted(
            object sender,
            CoreWebView2NavigationCompletedEventArgs e)
        {
            if (!e.IsSuccess || _welcomeStoresStockInjected) return;

            try
            {
                string script = ReadWelcomeStoresStockScript();
                await webView.CoreWebView2.ExecuteScriptAsync(script);
                _welcomeStoresStockInjected = true;
                DebugLog.Log("[WS-STOCK] curtain injected");
            }
            catch (Exception ex)
            {
                DebugLog.Log("[WS-STOCK] curtain injection failed: " + ex);
            }
        }

        private async void WelcomeStoresStock_WebMessageReceived(
            object sender,
            CoreWebView2WebMessageReceivedEventArgs e)
        {
            JObject cmd;
            try
            {
                cmd = JObject.Parse(e.WebMessageAsJson);
            }
            catch
            {
                return;
            }

            string type = (string)cmd["type"];
            if (string.IsNullOrWhiteSpace(type) ||
                !type.StartsWith("ws_stock_", StringComparison.OrdinalIgnoreCase))
                return;

            try
            {
                if (string.Equals(type, "ws_stock_search", StringComparison.OrdinalIgnoreCase))
                {
                    string query = ((string)cmd["query"] ?? string.Empty).Trim();
                    var items = WelcomeStoresInventoryService.SearchMasterItems(_xSupport, query);
                    await SendWelcomeStoresStockResultAsync("search", new
                    {
                        success = true,
                        items
                    });
                    return;
                }

                if (string.Equals(type, "ws_stock_availability", StringComparison.OrdinalIgnoreCase))
                {
                    string itemCode = ((string)cmd["itemCode"] ?? string.Empty).Trim();
                    var rows = WelcomeStoresInventoryService.GetStoreAvailability(_xSupport, itemCode);
                    await SendWelcomeStoresStockResultAsync("availability", new
                    {
                        success = true,
                        currentCompany = _xSupport.ConnectionInfo.CompanyId,
                        itemCode,
                        rows
                    });
                    return;
                }
            }
            catch (Exception ex)
            {
                DebugLog.Log("[WS-STOCK] " + type + " failed: " + ex);
                await SendWelcomeStoresStockResultAsync("error", new
                {
                    success = false,
                    message = ex.Message
                });
            }
        }

        private Task<string> SendWelcomeStoresStockResultAsync(string kind, object payload)
        {
            string kindJson = JsonConvert.SerializeObject(kind ?? string.Empty);
            string payloadJson = JsonConvert.SerializeObject(payload ?? new { });
            return webView.CoreWebView2.ExecuteScriptAsync(
                "window.welcomeStoresStockReceive && window.welcomeStoresStockReceive(" +
                kindJson + "," + payloadJson + ");");
        }

        private static string ReadWelcomeStoresStockScript()
        {
            const string resourceName = "S1Jarvis.web.welcomestores-stock.js";
            Assembly asm = Assembly.GetExecutingAssembly();
            using (Stream stream = asm.GetManifestResourceStream(resourceName))
            {
                if (stream == null)
                    throw new InvalidOperationException("Missing embedded resource: " + resourceName);

                using (var reader = new StreamReader(stream))
                    return reader.ReadToEnd();
            }
        }
    }
}
