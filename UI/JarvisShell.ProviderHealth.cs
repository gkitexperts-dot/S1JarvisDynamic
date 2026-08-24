using System;
using System.Text;
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
                try
                {
                    await ShowProviderHealthStatusAsync(
                        "AI provider: ο έλεγχος απέτυχε",
                        "error");
                }
                catch { }
            }
        }

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
                    explicitCommand ? "AI agents: έλεγχος..." : "AI provider: έλεγχος σύνδεσης...",
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
                    string provider = string.IsNullOrWhiteSpace(result.Provider)
                        ? "provider"
                        : result.Provider;
                    message = "AI provider: συνδεδεμένος · " + provider + " · " + model;
                    state = "ready";
                }
                else
                {
                    message = BuildProviderHealthFailureMessage(result, model);
                    state = "error";
                }

                await ShowProviderHealthStatusAsync(message, state);
                if (explicitCommand)
                    PostProviderHealthCommandResult(result, message, state);
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

        private void PostProviderHealthCommandResult(
            JarvisAgentHealthResult result,
            string fallbackMessage,
            string state)
        {
            if (webView == null || webView.CoreWebView2 == null)
                return;

            if (result == null || result.Targets == null || result.Targets.Count == 0)
            {
                PostProviderHealthCommandResult(fallbackMessage, state);
                return;
            }

            var text = new StringBuilder();
            text.AppendLine("**AI HEALTH**");
            text.AppendLine();
            text.AppendLine("| Agent | Status | Provider | Model | Routing |");
            text.AppendLine("|---|---|---|---|---|");

            foreach (JarvisAgentHealthTargetResult target in result.Targets)
            {
                string status = target.Ready ? "✓ Connected" : "✖ " + FriendlyTargetReason(target.ReasonCode);
                string provider = string.IsNullOrWhiteSpace(target.Provider) ? "—" : target.Provider;
                string model = string.IsNullOrWhiteSpace(target.Model) ? "—" : target.Model;
                string routing = target.Inherited ? "Inherited" : "Dedicated";
                text.Append("| ").Append(target.Agent ?? "—")
                    .Append(" | ").Append(status)
                    .Append(" | ").Append(provider)
                    .Append(" | ").Append(model)
                    .Append(" | ").Append(routing)
                    .AppendLine(" |");
            }

            webView.CoreWebView2.PostWebMessageAsString(text.ToString().TrimEnd());
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

        private static string FriendlyTargetReason(string reason)
        {
            if (string.IsNullOrWhiteSpace(reason)) return "Unavailable";
            if (reason == "provider_ready") return "Connected";
            if (reason == "provider_timeout") return "Timeout";
            if (reason == "provider_auth_failed") return "Credential";
            if (reason == "provider_model_missing") return "No model";
            if (reason == "provider_rate_limited") return "Rate limit";
            if (reason == "provider_credits_exhausted") return "No credits";
            if (reason == "provider_customer_mismatch") return "Customer mismatch";
            if (reason == "provider_credential_unavailable") return "Credential unavailable";
            if (reason == "agent_account_unavailable") return "Account unavailable";
            return reason;
        }

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
            string provider = result == null || string.IsNullOrWhiteSpace(result.Provider)
                ? string.Empty
                : " · " + result.Provider;
            string suffix = provider +
                (string.IsNullOrWhiteSpace(model) ? string.Empty : " · " + model);

            if (result != null && result.CreditsExhausted)
                return "AI provider: τα credits έχουν εξαντληθεί" + suffix;

            string reason = result?.ReasonCode ?? string.Empty;
            if (reason == "provider_timeout")
                return "AI provider: timeout σύνδεσης" + suffix;
            if (reason == "provider_model_missing")
                return "AI provider: δεν έχει οριστεί μοντέλο" + provider;
            if (reason == "provider_auth_failed")
                return "AI provider: μη έγκυρο provider credential" + suffix;
            if (reason == "provider_model_or_request_invalid")
                return "AI provider: μη έγκυρο μοντέλο ή provider request" + suffix;
            if (reason == "provider_rate_limited")
                return "AI provider: προσωρινό rate limit" + suffix;
            if (reason == "provider_upstream_error")
                return "AI provider: προσωρινό σφάλμα provider" + suffix;
            if (reason == "provider_unsupported")
                return "AI provider: μη υποστηριζόμενος provider" + suffix;
            if (reason == "provider_credential_unavailable")
                return "AI provider: credential μη διαθέσιμο στον Verilic" + suffix;
            if (reason == "agent_account_unavailable")
                return "AI provider: agent account μη διαθέσιμο" + suffix;
            if (reason == "provider_routing_changed")
                return "AI provider: η δρομολόγηση άλλαξε" + suffix;
            if (reason.StartsWith("routing_", StringComparison.Ordinal) ||
                reason.StartsWith("licence_", StringComparison.Ordinal) ||
                reason.StartsWith("installation_", StringComparison.Ordinal) ||
                reason.StartsWith("proof_", StringComparison.Ordinal))
                return "AI provider: Verilic " + reason + suffix;
            if (reason.StartsWith("provider_health_http_", StringComparison.Ordinal))
                return "AI provider: Verilic HTTP " +
                    reason.Substring("provider_health_http_".Length) + suffix;
            if (reason == "provider_health_configuration_invalid" ||
                reason == "provider_health_installation_invalid" ||
                reason == "provider_health_response_invalid" ||
                reason == "provider_health_failed")
                return "AI provider: αποτυχία Verilic health check" + suffix;

            return "AI provider: πρόβλημα σύνδεσης" + suffix;
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
                "if(!el){el=document.createElement('div');el.id=id;" +
                "el.style.zIndex='2147483647';el.style.padding='7px 11px';" +
                "el.style.borderRadius='8px';el.style.fontFamily='Segoe UI, sans-serif';" +
                "el.style.fontSize='12px';el.style.border='1px solid rgba(255,255,255,.14)';" +
                "el.style.background='rgba(27,28,47,.94)';el.style.backdropFilter='blur(6px)';" +
                "el.style.pointerEvents='none';}" +
                "el.style.display='block';el.textContent=\"" + safeMessage + "\";" +
                "el.setAttribute('data-state',\"" + safeState + "\");" +
                "el.style.color=(\"" + safeState + "\"==='ready')?'#9be8c2':" +
                "((\"" + safeState + "\"==='error')?'#ffb4ab':'#c9c9d6');" +
                "function visible(n){if(!n)return false;var s=getComputedStyle(n),r=n.getBoundingClientRect();" +
                "return s.display!=='none'&&s.visibility!=='hidden'&&r.width>40&&r.height>20&&r.bottom>0&&r.top<innerHeight;}" +
                "var app=document.getElementById('app');" +
                "var active=app&&app.classList.contains('active');" +
                "if(!active){" +
                "var transcript=document.getElementById('transcript');" +
                "if(el.parentNode!==app){if(el.parentNode)el.parentNode.removeChild(el);app.insertBefore(el,transcript);}" +
                "el.style.position='static';el.style.width='min(520px,94vw)';el.style.marginTop='10px';" +
                "el.style.left='auto';el.style.right='auto';el.style.top='auto';el.style.bottom='auto';return;}" +
                "if(el.parentNode!==document.body){if(el.parentNode)el.parentNode.removeChild(el);document.body.appendChild(el);}" +
                "el.style.position='fixed';el.style.marginTop='0';" +
                "var selectors=['#courierCurtain.open .courier-composer','#emailCurtain.open .email-composer'," +
                "'#helpCurtain.open .help-composer','#browserCurtain.open .browser-composer','.app.active .composer'];" +
                "var box=null;for(var i=0;i<selectors.length&&!box;i++){var n=document.querySelector(selectors[i]);if(visible(n))box=n;}" +
                "if(box){var r=box.getBoundingClientRect();var w=Math.min(Math.max(260,r.width),520);" +
                "el.style.width=w+'px';el.style.left=Math.max(12,r.left+(r.width-w)/2)+'px';" +
                "el.style.right='auto';el.style.bottom='auto';el.style.top=Math.max(12,r.top-el.offsetHeight-8)+'px';}" +
                "else{el.style.width='auto';el.style.left='auto';el.style.right='18px';el.style.top='14px';}" +
                "})();";

            await webView.CoreWebView2.ExecuteScriptAsync(script);
        }
    }
}
