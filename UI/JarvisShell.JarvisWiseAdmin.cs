using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace S1Jarvis.UI
{
    public partial class JarvisShell
    {
        // A static field initializer is used because JarvisShell already has a
        // static constructor in JarvisShell.JarvisWise.cs. Partial classes cannot
        // declare a second static constructor.
        private static readonly bool _jarvisWiseAdminBootstrapRegistered = RegisterJarvisWiseAdminBootstrap();

        private bool _jarvisWiseIsAdmin;
        private bool _jarvisWiseAdminCoreHooked;
        private int _jarvisWiseAdminMainSerial;
        private int _jarvisWiseAdminBrowserSerial;

        private static bool RegisterJarvisWiseAdminBootstrap()
        {
            EventManager.RegisterClassHandler(
                typeof(JarvisShell),
                FrameworkElement.LoadedEvent,
                new RoutedEventHandler(JarvisWiseAdminLoaded),
                true);
            return true;
        }

        private static void JarvisWiseAdminLoaded(object sender, RoutedEventArgs e)
        {
            var shell = sender as JarvisShell;
            if (shell == null) return;

            shell.ResolveJarvisAdminAtBoot();
            shell.webView.CoreWebView2InitializationCompleted -= shell.JarvisWiseAdminCoreInitialized;
            shell.webView.CoreWebView2InitializationCompleted += shell.JarvisWiseAdminCoreInitialized;

            if (shell.webView.CoreWebView2 != null)
                shell.AttachJarvisWiseAdminRouter();
        }

        private void ResolveJarvisAdminAtBoot()
        {
            try
            {
                _jarvisWiseIsAdmin = Core.JarvisAuthorization.IsCurrentUserAdmin(_xSupport);
                Core.DebugLog.Log("[JARVIS-AUTH] userId=" + _xSupport.ConnectionInfo.UserId +
                    " admin=" + (_jarvisWiseIsAdmin ? "true" : "false") +
                    " ParamCode=" + Core.JarvisAuthorization.AdminsParamCode);
            }
            catch (Exception ex)
            {
                _jarvisWiseIsAdmin = false;
                Core.DebugLog.Log("[JARVIS-AUTH] boot recognition failed; admin=false; error=" + ex.Message);
            }
        }

        private void JarvisWiseAdminCoreInitialized(
            object sender,
            Microsoft.Web.WebView2.Core.CoreWebView2InitializationCompletedEventArgs e)
        {
            if (e.IsSuccess)
                AttachJarvisWiseAdminRouter();
        }

        private void AttachJarvisWiseAdminRouter()
        {
            if (_jarvisWiseAdminCoreHooked || webView.CoreWebView2 == null) return;
            _jarvisWiseAdminCoreHooked = true;
            webView.CoreWebView2.WebMessageReceived += JarvisWiseAdminWebMessageReceived;
            Core.DebugLog.Log("[JARVIS-WISE-ADMIN] companion router attached; admin=" +
                (_jarvisWiseIsAdmin ? "true" : "false"));
        }

        private void JarvisWiseAdminWebMessageReceived(
            object sender,
            Microsoft.Web.WebView2.Core.CoreWebView2WebMessageReceivedEventArgs e)
        {
            if (!_jarvisWiseIsAdmin) return;

            string raw;
            try { raw = e.TryGetWebMessageAsString(); }
            catch { return; }
            if (string.IsNullOrWhiteSpace(raw)) return;

            if (raw.Length > 1 && raw[0] == '{')
            {
                JObject cmd;
                try { cmd = JObject.Parse(raw); }
                catch { return; }

                string type = cmd.Value<string>("type");
                if (string.Equals(type, "browser_message", StringComparison.Ordinal))
                {
                    string text = cmd.Value<string>("text") ?? string.Empty;
                    PrepareJarvisWiseAdminTurn(_browserConversation);
                    int serial = ++_jarvisWiseAdminBrowserSerial;
                    ObserveJarvisWiseAdminResponseAsync(
                        serial, text, _browserConversation,
                        "#browserTranscript .msg.assistant", "addBrowserMessage", true);
                }
                return;
            }

            if (raw.StartsWith("__JARVIS_", StringComparison.Ordinal)) return;

            PrepareJarvisWiseAdminTurn(_conversation);
            int mainSerial = ++_jarvisWiseAdminMainSerial;
            ObserveJarvisWiseAdminResponseAsync(
                mainSerial, raw, _conversation,
                "#transcript .msg.assistant", "addMessage", false);
        }

        private void PrepareJarvisWiseAdminTurn(List<JObject> history)
        {
            if (history == null) return;

            // Refresh on every admin turn so a company switch inside the same
            // Soft1 process picks up the new CompanyId/context immediately.
            RemovePreviousJarvisWiseAdminInstruction(history);
            string instruction = Core.JarvisWiseCompanyAdmin.BuildAdminInstruction(_xSupport);
            if (string.IsNullOrWhiteSpace(instruction)) return;

            history.Add(new JObject { ["role"] = "user", ["content"] = instruction });
            history.Add(new JObject
            {
                ["role"] = "assistant",
                ["content"] = "[JARVIS_WISE_ADMIN_ACK]"
            });
        }

        private static void RemovePreviousJarvisWiseAdminInstruction(List<JObject> history)
        {
            for (int i = history.Count - 1; i >= 0; i--)
            {
                JObject msg = history[i];
                if (msg == null || msg["content"] == null || msg["content"].Type != JTokenType.String)
                    continue;

                string text = msg["content"].ToString();
                if (text.StartsWith("[JARVIS_WISE_ADMIN_CONTEXT]", StringComparison.Ordinal) ||
                    string.Equals(text, "[JARVIS_WISE_ADMIN_ACK]", StringComparison.Ordinal))
                    history.RemoveAt(i);
            }
        }

        private async void ObserveJarvisWiseAdminResponseAsync(
            int serial,
            string userText,
            List<JObject> history,
            string selector,
            string addFunction,
            bool browser)
        {
            try
            {
                for (int attempt = 0; attempt < 240; attempt++)
                {
                    await Task.Delay(500);
                    if (browser)
                    {
                        if (serial != _jarvisWiseAdminBrowserSerial) return;
                    }
                    else if (serial != _jarvisWiseAdminMainSerial) return;

                    if (webView.CoreWebView2 == null) return;

                    string jsSelector = JsonConvert.SerializeObject(selector);
                    string encoded = await webView.ExecuteScriptAsync(@"
                        (() => {
                            const list = Array.from(document.querySelectorAll(" + jsSelector + @"));
                            const el = list.length ? list[list.length - 1] : null;
                            const orb = document.getElementById('orbWrap');
                            return JSON.stringify({
                                thinking: !!(orb && orb.classList.contains('thinking')),
                                raw: el ? (el.dataset.raw || el.innerText || '') : ''
                            });
                        })()");

                    JObject state = DecodeJsObject(encoded);
                    if (state == null || state.Value<bool>("thinking")) continue;

                    string assistantRaw = state.Value<string>("raw") ?? string.Empty;
                    if (string.IsNullOrWhiteSpace(assistantRaw)) continue;

                    Core.JarvisCompanyContextChange change;
                    string visible;
                    if (!Core.JarvisWiseCompanyAdmin.TryExtractChange(assistantRaw, out change, out visible))
                        return;

                    string rendered = visible;
                    if (string.Equals(change.Phase, "DRAFT", StringComparison.Ordinal))
                    {
                        rendered += "\n\n⚠ Η παραπάνω αλλαγή είναι μόνο προεπισκόπηση. Δεν έχει γραφτεί στο εταιρικό context.";
                        await ReplaceLastAssistantAsync(selector, addFunction, rendered);
                        Core.DebugLog.Log("[JARVIS-WISE-ADMIN] context draft prepared; action=" + change.Action +
                            " companyId=" + _xSupport.ConnectionInfo.CompanyId);
                        return;
                    }

                    // Never trust the model's COMMIT marker by itself. The current
                    // human turn must also be an explicit confirmation, and auth is
                    // re-checked inside Commit() immediately before the write.
                    if (!Core.JarvisWiseCompanyAdmin.IsExplicitConfirmation(userText))
                    {
                        rendered += "\n\n✖ Δεν έγινε αποθήκευση: απαιτείται ρητή επιβεβαίωση από Jarvis Admin.";
                        await ReplaceLastAssistantAsync(selector, addFunction, rendered);
                        Core.DebugLog.Log("[JARVIS-WISE-ADMIN] COMMIT rejected; explicit confirmation missing.");
                        return;
                    }

                    try
                    {
                        Core.JarvisWiseCompanyAdmin.Commit(_xSupport, change, userText);
                        rendered += "\n\n✓ Το Jarvis Wise εταιρικό context ενημερώθηκε για την ενεργή εταιρία.";
                    }
                    catch (Exception ex)
                    {
                        rendered += "\n\n✖ Η αποθήκευση του εταιρικού context απέτυχε: " + ex.Message;
                        Core.DebugLog.Log("[JARVIS-WISE-ADMIN] context commit failed: " + ex);
                    }

                    await ReplaceLastAssistantAsync(selector, addFunction, rendered);
                    return;
                }
            }
            catch (Exception ex)
            {
                Core.DebugLog.Log("[JARVIS-WISE-ADMIN] response observer failed; chat unaffected: " + ex);
            }
        }

        private static JObject DecodeJsObject(string encoded)
        {
            if (string.IsNullOrWhiteSpace(encoded) || encoded == "null") return null;
            try
            {
                string json = JsonConvert.DeserializeObject<string>(encoded);
                return string.IsNullOrWhiteSpace(json) ? null : JObject.Parse(json);
            }
            catch { return null; }
        }

        private async Task ReplaceLastAssistantAsync(string selector, string addFunction, string text)
        {
            string jsSelector = JsonConvert.SerializeObject(selector);
            string jsText = JsonConvert.SerializeObject(text ?? string.Empty);
            string fn = addFunction == "addBrowserMessage" ? "addBrowserMessage" : "addMessage";
            string script = @"
                (() => {
                    const list = Array.from(document.querySelectorAll(" + jsSelector + @"));
                    const el = list.length ? list[list.length - 1] : null;
                    if (!el || typeof " + fn + @" !== 'function') return false;
                    el.remove();
                    " + fn + @"('assistant', " + jsText + @");
                    return true;
                })()";
            await webView.ExecuteScriptAsync(script);
        }
    }
}
