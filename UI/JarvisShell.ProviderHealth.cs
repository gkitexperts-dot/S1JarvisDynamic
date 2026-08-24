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

                await ShowProviderHealthStatusAsync(
                    "AI provider: έλεγχος σύνδεσης...",
                    "checking");

                // Resolve once more through the same signed Verilic runtime
                // route so the health probe receives the non-secret model that
                // is configured for this exact contract/Soft1 user. Never use
                // a client-side hardcoded model.
                JarvisRuntimeAccessResult runtime = await Task.Run(() =>
                    JarvisLicenseGuard.CheckRuntimeAccessSilent(_xSupport));

                string model = runtime?.AgentRouting?.Model;
                if (string.IsNullOrWhiteSpace(model))
                {
                    await ShowProviderHealthStatusAsync(
                        "AI provider: δεν έχει οριστεί μοντέλο",
                        "error");
                    return;
                }

                if (runtime.AgentRouting == null ||
                    !runtime.AgentRouting.Available ||
                    !string.Equals(
                        runtime.AgentRouting.AgentAccountRef,
                        _agentAccountRef,
                        StringComparison.Ordinal))
                {
                    await ShowProviderHealthStatusAsync(
                        "AI provider: η δρομολόγηση άλλαξε",
                        "error");
                    return;
                }

                var probe = new JarvisAgentHealthProbe();
                JarvisAgentHealthResult result =
                    await probe.ProbeAsync(_agentAccountRef, model);

                if (result.Ready)
                {
                    await ShowProviderHealthStatusAsync(
                        "AI provider: συνδεδεμένος · " + result.Model,
                        "ready");
                    return;
                }

                string message = result.CreditsExhausted
                    ? "AI provider: τα credits έχουν εξαντληθεί · " + model
                    : result.ReasonCode == "provider_timeout"
                        ? "AI provider: timeout σύνδεσης · " + model
                        : result.ReasonCode == "provider_model_missing"
                            ? "AI provider: δεν έχει οριστεί μοντέλο"
                            : "AI provider: πρόβλημα σύνδεσης · " + model;

                await ShowProviderHealthStatusAsync(message, "error");
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

        private async Task ShowProviderHealthStatusAsync(
            string message,
            string state)
        {
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
                "el.style.top='14px';" +
                "el.style.right='18px';" +
                "el.style.zIndex='2147483647';" +
                "el.style.padding='7px 11px';" +
                "el.style.borderRadius='8px';" +
                "el.style.fontFamily='Segoe UI, sans-serif';" +
                "el.style.fontSize='12px';" +
                "el.style.border='1px solid rgba(255,255,255,.14)';" +
                "el.style.background='rgba(27,28,47,.88)';" +
                "el.style.backdropFilter='blur(6px)';" +
                "el.style.pointerEvents='none';" +
                "document.body.appendChild(el);" +
                "}" +
                "el.textContent=\"" + safeMessage + "\";" +
                "el.setAttribute('data-state',\"" + safeState + "\");" +
                "el.style.color=(\"" + safeState + "\"==='ready')?'#9be8c2':" +
                "((\"" + safeState + "\"==='error')?'#ffb4ab':'#c9c9d6');" +
                "})();";

            await webView.CoreWebView2.ExecuteScriptAsync(script);
        }
    }
}
