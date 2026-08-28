using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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

        // Short-lived server-side AADE payloads for the explicit
        // "Άνοιγμα προμηθευτή" confirmation flow. The browser receives only
        // a token and display data; the authoritative create payload remains
        // in the host and is not trusted back from JavaScript.
        private readonly Dictionary<string, JObject> _welcomeStoresPendingSuppliers =
            new Dictionary<string, JObject>(StringComparer.OrdinalIgnoreCase);

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

                if (string.Equals(type, "ws_stock_supplier_prepare", StringComparison.OrdinalIgnoreCase))
                {
                    string afm = ((string)cmd["afm"] ?? string.Empty).Trim();
                    if (string.IsNullOrWhiteSpace(afm))
                        throw new Exception("Λείπει το ΑΦΜ του καταστήματος.");

                    JObject existing = JObject.Parse(
                        JarvisTools.ExecuteFindTraderByAfmAndSodType(_xSupport, afm, 12));
                    if ((bool?)existing["found"] == true)
                    {
                        await SendWelcomeStoresStockResultAsync("supplier_already_exists", new
                        {
                            success = true,
                            afm,
                            trdrId = (int?)existing["trdrId"]
                        });
                        return;
                    }

                    JObject aade = JObject.Parse(JarvisTools.ExecuteGetAadeData(_xSupport, afm, 12));
                    if ((bool?)aade["success"] != true)
                        throw new Exception((string)aade["message"] ?? "Η ΑΑΔΕ δεν επέστρεψε στοιχεία.");

                    string suggestedCode = ((string)aade["suggestedCode"] ?? string.Empty).Trim();
                    if (string.IsNullOrWhiteSpace(suggestedCode))
                        throw new Exception("Δεν ήταν δυνατό να προταθεί κωδικός προμηθευτή.");

                    var createPayload = new JObject
                    {
                        ["afm"] = afm,
                        ["name"] = (string)aade["name"],
                        ["code"] = suggestedCode,
                        ["sodType"] = 12,
                        ["address"] = (string)aade["address"],
                        ["city"] = (string)aade["city"],
                        ["doy"] = (string)aade["doy"],
                        ["zip"] = (string)aade["zip"],
                        ["jobType"] = (string)aade["jobType"]
                    };

                    string token = Guid.NewGuid().ToString("N");
                    _welcomeStoresPendingSuppliers[token] = createPayload;

                    await SendWelcomeStoresStockResultAsync("supplier_preview", new
                    {
                        success = true,
                        token,
                        afm,
                        name = (string)aade["name"],
                        code = suggestedCode,
                        address = (string)aade["address"],
                        city = (string)aade["city"],
                        doy = (string)aade["doy"],
                        zip = (string)aade["zip"]
                    });
                    return;
                }

                if (string.Equals(type, "ws_stock_supplier_create", StringComparison.OrdinalIgnoreCase))
                {
                    string token = ((string)cmd["token"] ?? string.Empty).Trim();
                    JObject createPayload;
                    if (string.IsNullOrWhiteSpace(token) ||
                        !_welcomeStoresPendingSuppliers.TryGetValue(token, out createPayload))
                        throw new Exception("Η επιβεβαίωση ανοίγματος προμηθευτή έληξε. Ξεκίνησε ξανά από το κουμπί Άνοιγμα.");

                    string afm = ((string)createPayload["afm"] ?? string.Empty).Trim();

                    // Re-check immediately before the write to avoid a duplicate
                    // if another user opened the supplier after the preview.
                    JObject existing = JObject.Parse(
                        JarvisTools.ExecuteFindTraderByAfmAndSodType(_xSupport, afm, 12));
                    if ((bool?)existing["found"] == true)
                    {
                        _welcomeStoresPendingSuppliers.Remove(token);
                        await SendWelcomeStoresStockResultAsync("supplier_created", new
                        {
                            success = true,
                            alreadyExisted = true,
                            afm,
                            trdrId = (int?)existing["trdrId"]
                        });
                        return;
                    }

                    JObject created = JObject.Parse(
                        JarvisTools.ExecuteCreateTraderFromAade(_xSupport, createPayload));
                    if ((bool?)created["success"] != true)
                        throw new Exception((string)created["message"] ?? "Απέτυχε η δημιουργία προμηθευτή.");

                    _welcomeStoresPendingSuppliers.Remove(token);
                    await SendWelcomeStoresStockResultAsync("supplier_created", new
                    {
                        success = true,
                        alreadyExisted = false,
                        afm,
                        trdrId = (int?)created["trdrId"]
                    });
                    return;
                }

                if (string.Equals(type, "ws_stock_order_create", StringComparison.OrdinalIgnoreCase))
                {
                    await HandleWelcomeStoresOrderCreateAsync(cmd);
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

        private async Task HandleWelcomeStoresOrderCreateAsync(JObject cmd)
        {
            int storeCompany = (int?)cmd["storeCompany"] ?? 0;
            int whouse = (int?)cmd["whouse"] ?? 0;
            string itemCode = ((string)cmd["itemCode"] ?? string.Empty).Trim();
            decimal quantity = (decimal?)cmd["quantity"] ?? 0m;
            decimal price = (decimal?)cmd["price"] ?? 0m;

            if (storeCompany <= 0 || whouse <= 0 || string.IsNullOrWhiteSpace(itemCode))
                throw new Exception("Λείπουν στοιχεία καταστήματος/αποθήκης/είδους για την παραγγελία.");
            if (storeCompany == _xSupport.ConnectionInfo.CompanyId)
                throw new Exception("Δεν μπορεί να δημιουργηθεί παραγγελία προς το τρέχον κατάστημα.");
            if (quantity <= 0)
                throw new Exception("Η ποσότητα πρέπει να είναι μεγαλύτερη από μηδέν.");
            if (price <= 0)
                throw new Exception("Η συμφωνημένη τιμή πρέπει να είναι μεγαλύτερη από μηδέν.");

            // Re-read live availability immediately before the write. The UI
            // value is display state only and is not trusted as stock authority.
            WelcomeStoresInventoryService.StockRow stockRow =
                WelcomeStoresInventoryService.GetStoreAvailability(_xSupport, itemCode)
                    .FirstOrDefault(x =>
                        x.StoreCompany == storeCompany &&
                        x.Whouse == whouse &&
                        string.Equals(x.ItemCode, itemCode, StringComparison.OrdinalIgnoreCase));

            if (stockRow == null)
                throw new Exception("Η επιλεγμένη γραμμή αποθέματος δεν είναι πλέον διαθέσιμη. Κάνε νέα αναζήτηση.");
            if (quantity > stockRow.Available)
                throw new Exception(
                    "Η ζητούμενη ποσότητα (" + quantity + ") ξεπερνά το τρέχον διαθέσιμο (" + stockRow.Available + ").");
            if (!stockRow.SupplierExists || !stockRow.SupplierTrdr.HasValue || stockRow.SupplierTrdr.Value <= 0)
                throw new Exception("Το κατάστημα δεν είναι ακόμα ανοιγμένο ως προμηθευτής. Πάτησε πρώτα Άνοιγμα.");

            // Canonical item CODE is resolved to the local company's own MTRL.
            // Never reuse MTRL ids from company 1000 or from the source store.
            int localMtrl = WelcomeStoresInventoryService.ResolveCurrentCompanyMtrl(_xSupport, itemCode);
            int series = WelcomeStoresInventoryService.ResolvePurchaseOrderSeries(_xSupport);

            var orderInput = new JObject
            {
                ["sosource"] = WelcomeStoresInventoryService.PurchaseOrderSosource,
                ["series"] = series,
                ["trdrId"] = stockRow.SupplierTrdr.Value,
                ["lines"] = new JArray
                {
                    new JObject
                    {
                        ["mtrlId"] = localMtrl,
                        ["quantity"] = quantity,
                        ["price"] = price
                    }
                },
                ["sourceInstruction"] =
                    "WelcomeStores Stores Inventory - παραγγελία από εταιρία " + storeCompany +
                    ", αποθήκη " + whouse + ", είδος " + itemCode +
                    ", ποσότητα " + quantity + ", συμφωνημένη τιμή " + price,
                // Deterministic UI flow: store/supplier/item/qty/price are all
                // explicitly selected and server-validated, so there is no AI
                // interpretation uncertainty at this point.
                ["confidence"] = 1.0
            };

            JObject created = JObject.Parse(JarvisTools.ExecuteCreateOrder(_xSupport, orderInput));
            if ((bool?)created["success"] != true)
                throw new Exception((string)created["message"] ?? "Απέτυχε η δημιουργία παραγγελίας PURDOC.");

            int findocId = (int?)created["findocId"] ?? 0;
            bool opened = false;
            if (findocId > 0)
            {
                try
                {
                    JarvisTools.ExecuteOpenDocument(_xSupport, new JObject
                    {
                        ["sosource"] = WelcomeStoresInventoryService.PurchaseOrderSosource,
                        ["mode"] = "locate",
                        ["id"] = findocId
                    });
                    opened = true;
                }
                catch (Exception openEx)
                {
                    // The order already exists at this point. Opening the native
                    // card is convenience only and must not turn a successful
                    // write into a false failure.
                    DebugLog.Log("[WS-STOCK] order created but native open failed: " + openEx);
                }
            }

            await SendWelcomeStoresStockResultAsync("order_created", new
            {
                success = true,
                findocId,
                series,
                sosource = WelcomeStoresInventoryService.PurchaseOrderSosource,
                opened
            });
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
