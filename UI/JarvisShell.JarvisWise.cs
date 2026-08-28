using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace S1Jarvis.UI
{
    /// <summary>
    /// Jarvis Wise integration kept in a partial class so the learned-knowledge
    /// layer stays isolated from the already large JarvisShell message router.
    ///
    /// It hooks before the existing WebMessageReceived router, injects current
    /// company + verified learned knowledge into the appropriate conversation,
    /// observes the final rendered response, stores reusable knowledge candidates,
    /// and reuses the existing rate:SOACTIONID star UI.
    /// </summary>
    public partial class JarvisShell
    {
        private bool _jarvisWiseCoreHooked;
        private int _jarvisWiseMainTurnSerial;
        private int _jarvisWiseHelpTurnSerial;
        private string _jarvisWiseLastMainRaw;
        private readonly HashSet<int> _jarvisWisePromotedHelpIds = new HashSet<int>();

        static JarvisShell()
        {
            // Class handler runs before the instance Loaded handler. This lets us
            // subscribe to CoreWebView2InitializationCompleted before
            // JarvisShell_Loaded calls EnsureCoreWebView2Async, so our synchronous
            // context injection runs before the existing primary message router.
            EventManager.RegisterClassHandler(
                typeof(JarvisShell),
                FrameworkElement.LoadedEvent,
                new RoutedEventHandler(JarvisWiseLoaded),
                true);
        }

        private static void JarvisWiseLoaded(object sender, RoutedEventArgs e)
        {
            var shell = sender as JarvisShell;
            if (shell == null) return;

            shell.webView.CoreWebView2InitializationCompleted -= shell.JarvisWiseCoreInitialized;
            shell.webView.CoreWebView2InitializationCompleted += shell.JarvisWiseCoreInitialized;

            if (shell.webView.CoreWebView2 != null)
                shell.AttachJarvisWiseCoreRouter();
        }

        private void JarvisWiseCoreInitialized(
            object sender,
            Microsoft.Web.WebView2.Core.CoreWebView2InitializationCompletedEventArgs e)
        {
            if (e.IsSuccess)
                AttachJarvisWiseCoreRouter();
        }

        private void AttachJarvisWiseCoreRouter()
        {
            if (_jarvisWiseCoreHooked || webView.CoreWebView2 == null) return;
            _jarvisWiseCoreHooked = true;
            webView.CoreWebView2.WebMessageReceived += JarvisWiseWebMessageReceived;
            Core.DebugLog.Log("[JARVIS-WISE] primary companion router attached");
        }

        private void JarvisWiseWebMessageReceived(
            object sender,
            Microsoft.Web.WebView2.Core.CoreWebView2WebMessageReceivedEventArgs e)
        {
            string raw;
            try
            {
                raw = e.TryGetWebMessageAsString();
            }
            catch
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(raw)) return;

            // Structured commands are JSON strings produced by postCommand().
            if (raw.Length > 1 && raw[0] == '{')
            {
                JObject cmd;
                try { cmd = JObject.Parse(raw); }
                catch { return; }

                string type = cmd.Value<string>("type");
                if (string.Equals(type, "help_message", StringComparison.Ordinal))
                {
                    string text = cmd.Value<string>("text") ?? string.Empty;
                    PrepareJarvisWiseHelpTurn(text);
                    return;
                }

                // Existing Help stars and generic inline rate: links continue to
                // use their old commands. We observe the same command and update
                // the new Jarvis Wise status fields; the existing handler still
                // updates SOSMALLINT exactly as before.
                if (string.Equals(type, "help_rate", StringComparison.Ordinal) ||
                    string.Equals(type, "rate_order_prompt", StringComparison.Ordinal))
                {
                    int soactionId = cmd.Value<int?>("soactionId") ?? 0;
                    int rating = cmd.Value<int?>("rating") ?? 0;
                    try
                    {
                        Core.JarvisWise.ApplyRating(_xSupport, soactionId, rating);
                    }
                    catch (Exception ex)
                    {
                        Core.DebugLog.Log("[JARVIS-WISE] rating update failed: " + ex);
                    }
                    return;
                }

                // Other curtains/deterministic commands keep their existing flow.
                return;
            }

            // Sentinels and internal commands are not chat knowledge turns.
            if (raw.StartsWith("__JARVIS_", StringComparison.Ordinal)) return;

            PrepareJarvisWiseMainTurn(raw);
        }

        private void PrepareJarvisWiseMainTurn(string userText)
        {
            try
            {
                // Synchronous by design: XSupport/Soft1 SDK access stays on the
                // Soft1 integration/UI thread. The existing AskAsync handler runs
                // after this handler and therefore sees the injected context.
                Core.JarvisWise.InjectTurnContext(
                    _xSupport,
                    _conversation,
                    userText,
                    includeCandidateInstruction: true);
            }
            catch (Exception ex)
            {
                Core.DebugLog.Log("[JARVIS-WISE] main context injection failed; chat continues: " + ex);
            }

            int serial = ++_jarvisWiseMainTurnSerial;
            ObserveMainJarvisWiseTurnAsync(serial, userText);
        }

        private void PrepareJarvisWiseHelpTurn(string userText)
        {
            try
            {
                // Help already has its proven ΛΕΞΕΙΣ-ΚΛΕΙΔΙΑ / ΠΕΡΙΛΗΨΗ /
                // ΛΥΣΗ completion marker and star workflow. We inject company +
                // retrieved knowledge only, then promote the SAME SOACTION record
                // after help_solution instead of creating a duplicate candidate.
                Core.JarvisWise.InjectTurnContext(
                    _xSupport,
                    _helpConversation,
                    userText,
                    includeCandidateInstruction: false);
            }
            catch (Exception ex)
            {
                Core.DebugLog.Log("[JARVIS-WISE] Help context injection failed; Help continues: " + ex);
            }

            int serial = ++_jarvisWiseHelpTurnSerial;
            ObserveHelpJarvisWiseTurnAsync(serial, userText);
        }

        private async void ObserveMainJarvisWiseTurnAsync(int serial, string userText)
        {
            try
            {
                for (int attempt = 0; attempt < 240; attempt++) // max ~120 sec
                {
                    await Task.Delay(500);
                    if (serial != _jarvisWiseMainTurnSerial) return;
                    if (webView.CoreWebView2 == null) return;

                    JObject state = await ReadJsStateAsync(@"
                        (() => {
                            const list = Array.from(document.querySelectorAll('#transcript .msg.assistant'));
                            const el = list.length ? list[list.length - 1] : null;
                            const orb = document.getElementById('orbWrap');
                            return JSON.stringify({
                                thinking: !!(orb && orb.classList.contains('thinking')),
                                raw: el ? (el.dataset.raw || el.innerText || '') : ''
                            });
                        })()");

                    if (state == null || state.Value<bool>("thinking")) continue;
                    string assistantRaw = state.Value<string>("raw") ?? string.Empty;
                    if (string.IsNullOrWhiteSpace(assistantRaw)) continue;
                    if (string.Equals(assistantRaw, _jarvisWiseLastMainRaw, StringComparison.Ordinal)) continue;

                    if (Core.JarvisWise.TryExtractCandidate(
                        assistantRaw,
                        userText,
                        out var candidate,
                        out var visibleText))
                    {
                        // Strip machine metadata from the model history as well as
                        // the visible DOM so it never becomes conversational noise.
                        Core.JarvisWise.CleanMarkerFromHistory(_conversation);

                        int soactionId = 0;
                        try
                        {
                            soactionId = Core.JarvisWise.CreateCandidateSoAction(_xSupport, candidate);
                        }
                        catch (Exception ex)
                        {
                            Core.DebugLog.Log("[JARVIS-WISE] candidate persistence failed: " + ex);
                        }

                        string rendered = visibleText;
                        if (soactionId > 0)
                            rendered += "\n\n[⭐ Βαθμολόγησε](rate:" + soactionId + ")";

                        await ReplaceLastMainAssistantAsync(rendered);
                        _jarvisWiseLastMainRaw = rendered;
                        return;
                    }

                    _jarvisWiseLastMainRaw = assistantRaw;
                    return;
                }
            }
            catch (Exception ex)
            {
                Core.DebugLog.Log("[JARVIS-WISE] main observer failed; chat unaffected: " + ex);
            }
        }

        private async void ObserveHelpJarvisWiseTurnAsync(int serial, string fallbackRequest)
        {
            try
            {
                for (int attempt = 0; attempt < 240; attempt++) // max ~120 sec
                {
                    await Task.Delay(500);
                    if (serial != _jarvisWiseHelpTurnSerial) return;
                    if (webView.CoreWebView2 == null) return;

                    JObject state = await ReadJsStateAsync(@"
                        (() => {
                            const list = Array.from(document.querySelectorAll('#helpTranscript .msg.assistant'));
                            const el = list.length ? list[list.length - 1] : null;
                            let thinking = false, resolved = false, id = 0;
                            try { thinking = !!helpThinking; } catch (_) {}
                            try { resolved = !!helpResolved; } catch (_) {}
                            try { id = Number(helpSoactionId || 0); } catch (_) {}
                            return JSON.stringify({
                                thinking: thinking,
                                resolved: resolved,
                                soactionId: id,
                                raw: el ? (el.dataset.raw || el.innerText || '') : ''
                            });
                        })()");

                    if (state == null || state.Value<bool>("thinking")) continue;
                    if (!state.Value<bool>("resolved")) return; // intermediate help_reply

                    int soactionId = state.Value<int?>("soactionId") ?? 0;
                    if (soactionId <= 0 || _jarvisWisePromotedHelpIds.Contains(soactionId)) return;

                    string response = state.Value<string>("raw") ?? string.Empty;
                    Core.JarvisWise.PromoteHelpRecord(
                        _xSupport,
                        soactionId,
                        fallbackRequest,
                        response);
                    _jarvisWisePromotedHelpIds.Add(soactionId);
                    return;
                }
            }
            catch (Exception ex)
            {
                Core.DebugLog.Log("[JARVIS-WISE] Help promotion failed; existing Help record remains valid: " + ex);
            }
        }

        private async Task<JObject> ReadJsStateAsync(string script)
        {
            string encoded = await webView.ExecuteScriptAsync(script);
            if (string.IsNullOrWhiteSpace(encoded) || encoded == "null") return null;

            string json;
            try { json = JsonConvert.DeserializeObject<string>(encoded); }
            catch { return null; }
            if (string.IsNullOrWhiteSpace(json)) return null;

            try { return JObject.Parse(json); }
            catch { return null; }
        }

        private async Task ReplaceLastMainAssistantAsync(string text)
        {
            string jsText = JsonConvert.SerializeObject(text ?? string.Empty);
            string script = @"
                (() => {
                    const list = Array.from(document.querySelectorAll('#transcript .msg.assistant'));
                    const el = list.length ? list[list.length - 1] : null;
                    if (!el || typeof addMessage !== 'function') return false;
                    el.remove();
                    addMessage('assistant', " + jsText + @");
                    return true;
                })()";
            await webView.ExecuteScriptAsync(script);
        }
    }
}
