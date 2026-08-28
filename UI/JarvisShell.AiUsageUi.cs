using System;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using S1Jarvis.Core;

namespace S1Jarvis.UI
{
    public partial class JarvisShell
    {
        private bool _aiUsageUiEnabled;

        // Kept outside index.html deliberately: usage telemetry is runtime UI
        // decoration and can evolve independently from the main embedded chat.
        // The MutationObserver covers the main chat and all Jarvis curtain chats
        // because they share the same .msg user/assistant convention.
        private const string AiUsageUiScript = @"
(function () {
  if (window.__jarvisUsageUiInstalled) return;
  window.__jarvisUsageUiInstalled = true;

  var pending = null;

  function emptyUsage() {
    return { input: 0, output: 0, model: '', provider: '', calls: 0, allLogged: true };
  }

  function resetUsage() {
    pending = emptyUsage();
  }

  window.__jarvisUsagePush = function (u) {
    if (!pending) resetUsage();
    pending.input += Math.max(0, Number(u.inputTokens) || 0);
    pending.output += Math.max(0, Number(u.outputTokens) || 0);
    if (u.model) pending.model = String(u.model);
    if (u.provider) pending.provider = String(u.provider);
    pending.calls += 1;
    pending.allLogged = pending.allLogged && !!u.logged;
  };

  function hasUsage() {
    return pending && pending.calls > 0;
  }

  function addUsageFooter(bubble) {
    if (!bubble || bubble.__jarvisUsageFooterAttached || !hasUsage()) return;
    bubble.__jarvisUsageFooterAttached = true;

    var usage = pending;
    resetUsage();

    var footer = document.createElement('div');
    footer.className = 'jarvis-ai-usage-meta';
    footer.style.cssText =
      'align-self:flex-start;margin:-10px 0 2px 8px;font-size:9.5px;' +
      'line-height:1.25;letter-spacing:.1px;white-space:nowrap;user-select:text;';

    var inEl = document.createElement('span');
    inEl.textContent = 'IN ' + Math.trunc(usage.input);
    inEl.style.cssText = 'color:#60a5fa;opacity:.82;font-weight:600;';

    var outEl = document.createElement('span');
    outEl.textContent = 'OUT ' + Math.trunc(usage.output);
    outEl.style.cssText = 'color:#ff6b6b;opacity:.82;font-weight:600;margin-left:8px;';

    var modelEl = document.createElement('span');
    modelEl.textContent = usage.model || 'model ?';
    modelEl.style.cssText = 'color:var(--text-dim);opacity:.55;margin-left:8px;';

    var statusEl = document.createElement('span');
    statusEl.textContent = '●';
    statusEl.style.cssText = usage.allLogged
      ? 'color:#4cc98a;opacity:.55;margin-left:7px;'
      : 'color:#ffc14c;opacity:.75;margin-left:7px;';
    statusEl.title = usage.allLogged
      ? 'AI usage καταγράφηκε στο CCCJAILOG'
      : 'AI usage log απέτυχε - η απάντηση συνεχίστηκε κανονικά';

    footer.appendChild(inEl);
    footer.appendChild(outEl);
    footer.appendChild(modelEl);
    footer.appendChild(statusEl);

    if (bubble.parentNode) {
      bubble.parentNode.insertBefore(footer, bubble.nextSibling);
    }
  }

  function inspectNode(node) {
    if (!node || node.nodeType !== 1) return;

    var users = [];
    var assistants = [];
    if (node.matches && node.matches('.msg.user')) users.push(node);
    if (node.matches && node.matches('.msg.assistant')) assistants.push(node);
    if (node.querySelectorAll) {
      node.querySelectorAll('.msg.user').forEach(function (x) { users.push(x); });
      node.querySelectorAll('.msg.assistant').forEach(function (x) { assistants.push(x); });
    }

    if (users.length > 0) resetUsage();

    assistants.forEach(function (bubble) {
      // Provider usage is pushed from C# before the final visible response.
      // Small defer protects against WebView message/script queue ordering.
      setTimeout(function () { addUsageFooter(bubble); }, 40);
    });
  }

  function startObserver() {
    if (!document.body || window.__jarvisUsageObserver) return;
    resetUsage();
    window.__jarvisUsageObserver = new MutationObserver(function (mutations) {
      mutations.forEach(function (m) {
        m.addedNodes.forEach(inspectNode);
      });
    });
    window.__jarvisUsageObserver.observe(document.body, { childList: true, subtree: true });
  }

  if (document.readyState === 'loading') {
    document.addEventListener('DOMContentLoaded', startObserver, { once: true });
  } else {
    startObserver();
  }
})();";

        public void EnableAiUsageUi()
        {
            if (_aiUsageUiEnabled)
                return;

            _aiUsageUiEnabled = true;
            JarvisAiUsageLogger.UsageRecorded += OnAiUsageRecorded;
            Unloaded += JarvisShell_AiUsageUiUnloaded;

            webView.CoreWebView2InitializationCompleted +=
                WebView_AiUsageUiInitializationCompleted;

            if (webView.CoreWebView2 != null)
                _ = InstallAiUsageUiAsync();
        }

        private async void WebView_AiUsageUiInitializationCompleted(
            object sender,
            Microsoft.Web.WebView2.Core.CoreWebView2InitializationCompletedEventArgs e)
        {
            if (!e.IsSuccess)
                return;

            await InstallAiUsageUiAsync();
        }

        private async Task InstallAiUsageUiAsync()
        {
            try
            {
                if (webView.CoreWebView2 == null)
                    return;

                await webView.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(
                    AiUsageUiScript);
                await webView.CoreWebView2.ExecuteScriptAsync(AiUsageUiScript);
            }
            catch (Exception ex)
            {
                DebugLog.Log("[AI-USAGE-UI] install failed: " + ex.Message);
            }
        }

        private void OnAiUsageRecorded(JarvisAiUsageEvent usage)
        {
            if (usage == null)
                return;

            try
            {
                Dispatcher.BeginInvoke(new Action(async () =>
                {
                    try
                    {
                        if (webView.CoreWebView2 == null)
                            return;

                        var payload = new JObject
                        {
                            ["inputTokens"] = usage.InputTokens,
                            ["outputTokens"] = usage.OutputTokens,
                            ["model"] = usage.Model ?? string.Empty,
                            ["provider"] = usage.Provider ?? string.Empty,
                            ["logged"] = usage.Logged
                        };

                        string script =
                            "if(window.__jarvisUsagePush){window.__jarvisUsagePush(" +
                            payload.ToString(Formatting.None) + ");}";
                        await webView.CoreWebView2.ExecuteScriptAsync(script);
                    }
                    catch (Exception ex)
                    {
                        DebugLog.Log("[AI-USAGE-UI] push failed: " + ex.Message);
                    }
                }));
            }
            catch (Exception ex)
            {
                DebugLog.Log("[AI-USAGE-UI] dispatch failed: " + ex.Message);
            }
        }

        private void JarvisShell_AiUsageUiUnloaded(
            object sender,
            System.Windows.RoutedEventArgs e)
        {
            if (!_aiUsageUiEnabled)
                return;

            JarvisAiUsageLogger.UsageRecorded -= OnAiUsageRecorded;
            _aiUsageUiEnabled = false;
        }
    }
}
