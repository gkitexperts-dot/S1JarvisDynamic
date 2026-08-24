using System;
using System.Threading.Tasks;
using System.Windows;
using S1Jarvis.Access;
using S1Jarvis.Core;

namespace S1Jarvis.UI
{
    public partial class JarvisShell
    {
        private bool _providerHealthCheckEnabled;
        private string _providerHealthState;
        private string _providerHealthMessage;
        private string _providerHealthModel;

        internal void EnableProviderHealthCheck()
        {
            if (_providerHealthCheckEnabled)
                return;

            _providerHealthCheckEnabled = true;
            Loaded += ProviderHealthCheck_Loaded;
        }

        private async void ProviderHealthCheck_Loaded(object sender, RoutedEventArgs e)
        {
            Loaded -= ProviderHealthCheck_Loaded;

            try
            {
                // The existing NavigationCompleted flow performs the
                // authoritative licence + AI-routing checks and only then sets
                // _agentAccountRef. Wait for that controlled startup flow to
                // finish instead of bypassing it here.
                for (int attempt = 0; attempt < 150; attempt++)
                {
                    if (!string.IsNullOrWhiteSpace(_agentAccountRef) &&
                        webView != null &&
                        webView.CoreWebView2 != null)
                        break;

                    await Task.Delay(100);
                }

                if (string.IsNullOrWhiteSpace(_agentAccountRef) ||
                    webView == null ||
                    webView.CoreWebView2 == null)
                    return;

                await RefreshProviderHealthStatusAsync(false);
            }
            catch
            {
                // Startup health feedback must never be able to crash the
                // in-process Soft1 host. Real AI requests remain authoritative
                // and will surface their own failure if this optional probe
                // itself cannot complete.
                try
                {
                    await ShowProviderHealthStatusAsync(
                        "AI provider: ο έλεγχος απέτυχε",
                        "error");
                }
                catch { }
            }
        }

        // Reusable by startup and by the deterministic HEALTH command.
        // It always re-resolves the authoritative Verilic route and performs
        // a fresh signed provider probe; no provider credential is exposed to
        // the client.
        private async Task RefreshProviderHealthStatusAsync(bool explicitCommand)
        {
            string message;
            string state;
            string model = null;

            try
            {
                if (string.IsNullOrWhiteSpace(_agentAccountRef))
                {
                    message = "AI agent: δεν υπάρχει ενεργή άδεια/agent";
                    state = "error";
                    await ShowProviderHealthStatusAsync(message, state);
                    if (explicitCommand)
                        PostProviderHealthCommandResult(message, state);
                    return;
                }

                await ShowProviderHealthStatusAsync(
                    explicitCommand ? "AI agent: έλεγχος..." : "AI provider: έλεγχος σύνδεσης...",
                    "checking");

                JarvisRuntimeAccessResult runtime = await Task.Run(() =>
                    JarvisLicenseGuard.CheckRuntimeAccessSilent(_xSupport));

                model = runtime?.AgentRouting?.Model;
                if (string.IsNullOrWhiteSpace(model))
                {
                    message = "AI provider: δεν έχει οριστεί μοντέλο";
                    state = "error";
                    await ShowProviderHealthStatusAsync(message, state);
                    if (explicitCommand)
                        PostProviderHealthCommandResult(message, state);
                    return;
                }

                if (runtime.AgentRouting == null ||
                    !runtime.AgentRouting.Available ||
                    !string.Equals(
                        runtime.AgentRouting.AgentAccountRef,
                        _agentAccountRef,
                        StringComparison.Ordinal))
                {
                    message = "AI provider: η δρομολόγηση άλλαξε · " + model;
                    state = "error";
                    await ShowProviderHealthStatusAsync(message, state);
                    if (explicitCommand)
                        PostProviderHealthCommandResult(message, state);
                    return;
                }

                var probe = new JarvisAgentHealthProbe();
                JarvisAgentHealthResult result = await probe.ProbeAsync(
                    _xSupport,
                    _agentAccountRef,
                    model);

                if (result.Ready)
                {
                    model = result.Model ?? model;
                    message = "AI provider: συνδεδεμένος · " + model;
                    state = "ready";
                }
                else
                {
                    message = BuildProviderHealthFailureMessage(result, model);
                    state = "error";
                }

                await ShowProviderHealthStatusAsync(message, state);
                if (explicitCommand)
                    PostProviderHealthCommandResult(message, state);
            }
            catch
            {
                message = "AI provider: ο έλεγχος απέτυχε" +
                    (string.IsNullOrWhiteSpace(model) ? string.Empty : " · " + model);
                state = "error";
                await ShowProviderHealthStatusAsync(message, state);
                if (explicitCommand)
                    PostProviderHealthCommandResult(message, state);
            }
        }

        private void PostProviderHealthCommandResult(string message, string state)
        {
            if (webView == null || webView.CoreWebView2 == null)
                return;

            string prefix = state == "ready" ? "✓ " : "✖ ";
            string text = message.StartsWith("AI provider:", StringComparison.Ordinal)
                ? "AI agent:" + message.Substring("AI provider:".Length)
                : message;
            webView.CoreWebView2.PostWebMessageAsString(prefix + text);
        }

        // Once a conversation/interaction begins, a successful startup badge
        // is only noise. Error state is deliberately kept visible so the
        // operator sees the problem above the active chat box.
        private async Task HideProviderHealthIfReadyAsync()
        {
            if (!string.Equals(_providerHealthState, "ready", StringComparison.Ordinal) ||
                webView == null || webView.CoreWebView2 == null)
                return;

            await webView.CoreWebView2.ExecuteScriptAsync(
                "(function(){var el=document.getElementById('jarvis-provider-health');" +
                "if(el){el.style.display='none';}})();");
        }

        private static string BuildProviderHealthFailureMessage(
            JarvisAgentHealthResult result,
            string model)
        {
            if (result.CreditsExhausted)
                return "AI provider: τα credits έχουν εξαντληθεί · " + model;

            string reason = result.ReasonCode ?? string.Empty;
            if (reason == "provider_timeout")
                return "AI provider: timeout σύνδεσης · " + model;
            if (reason == "provider_model_missing")
                return "AI provider: δεν έχει οριστεί μοντέλο";
            if (reason == "provider_auth_failed")
                return "AI provider: μη έγκυρο provider credential · " + model;
            if (reason == "provider_model_or_request_invalid")
                return "AI provider: μη έγκυρο μοντέλο ή provider request · " + model;
            if (reason == "provider_rate_limited")
                return "AI provider: προσωρινό rate limit · " + model;
            if (reason == "provider_upstream_error")
                return "AI provider: προσωρινό σφάλμα provider · " + model;
            if (reason == "provider_unsupported")
                return "AI provider: μη υποστηριζόμενος provider · " + model;
            if (reason == "provider_credential_unavailable")
                return "AI provider: credential μη διαθέσιμο στον Verilic · " + model;
            if (reason == "agent_account_unavailable")
                return "AI provider: agent account μη διαθέσιμο · " + model;
            if (reason == "provider_routing_changed")
                return "AI provider: η δρομολόγηση άλλαξε · " + (result.Model ?? model);
            if (reason.StartsWith("routing_", StringComparison.Ordinal) ||
                reason.StartsWith("licence_", StringComparison.Ordinal) ||
                reason.StartsWith("installation_", StringComparison.Ordinal) ||
                reason.StartsWith("proof_", StringComparison.Ordinal))
                return "AI provider: Verilic " + reason + " · " + model;
            if (reason.StartsWith("provider_health_http_", StringComparison.Ordinal))
                return "AI provider: Verilic HTTP " +
                    reason.Substring("provider_health_http_".Length) + " · " + model;
            if (reason == "provider_health_configuration_invalid" ||
                reason == "provider_health_installation_invalid" ||
                reason == "provider_health_response_invalid" ||
                reason == "provider_health_failed")
                return "AI provider: αποτυχία Verilic health check · " + model;

            return "AI provider: πρόβλημα σύνδεσης · " + model;
        }

        private async Task ShowProviderHealthStatusAsync(
            string message,
            string state)
        {
            _providerHealthMessage = message;
            _providerHealthState = state;
            int separator = message.LastIndexOf(" · ", StringComparison.Ordinal);
            _providerHealthModel = separator >= 0 && separator + 3 < message.Length
                ? message.Substring(separator + 3)
                : null;

            if (webView == null || webView.CoreWebView2 == null)
                return;

            string safeMessage = JsEscape(message);
            string safeState = JsEscape(state);

            string script =
                "(function(){" +
                "var id='jarvis-provider-health';" +
                "var el=document.getElementById(id);" +
                "if(!el){" +
                "el=document.createElement('div');" +
                "el.id=id;" +
                "el.style.position='fixed';" +
                "el.style.zIndex='2147483647';" +
                "el.style.padding='7px 11px';" +
                "el.style.borderRadius='8px';" +
                "el.style.fontFamily='Segoe UI, sans-serif';" +
                "el.style.fontSize='12px';" +
                "el.style.border='1px solid rgba(255,255,255,.14)';" +
                "el.style.background='rgba(27,28,47,.94)';" +
                "el.style.backdropFilter='blur(6px)';" +
                "el.style.pointerEvents='none';" +
                "document.body.appendChild(el);" +
                "}" +
                "el.style.display='block';" +
                "el.textContent=\"" + safeMessage + "\";" +
                "el.setAttribute('data-state',\"" + safeState + "\");" +
                "el.style.color=(\"" + safeState + "\"==='ready')?'#9be8c2':" +
                "((\"" + safeState + "\"==='error')?'#ffb4ab':'#c9c9d6');" +
                "function visible(n){if(!n)return false;var s=getComputedStyle(n),r=n.getBoundingClientRect();" +
                "return s.display!=='none'&&s.visibility!=='hidden'&&r.width>40&&r.height>20&&r.bottom>0&&r.top<innerHeight;}" +
                "function place(){" +
                "var all=Array.from(document.querySelectorAll('.composer,[class*=\"composer\"]'));" +
                "var boxes=all.filter(visible);" +
                "var box=boxes.length?boxes[boxes.length-1]:null;" +
                "if(box){var r=box.getBoundingClientRect();var w=Math.min(Math.max(260,r.width),520);" +
                "el.style.width=w+'px';el.style.left=Math.max(12,r.left+(r.width-w)/2)+'px';" +
                "el.style.right='auto';el.style.bottom='auto';" +
                "el.style.top=Math.max(12,r.top-el.offsetHeight-8)+'px';}" +
                "else{el.style.width='auto';el.style.left='auto';el.style.right='18px';el.style.top='14px';}" +
                "}" +
                "place();setTimeout(place,0);" +
                "})();";

            await webView.CoreWebView2.ExecuteScriptAsync(script);
        }
    }
}
