using System;
using System.Windows;
using Newtonsoft.Json.Linq;
using S1Jarvis.Core;

namespace S1Jarvis.UI
{
    public partial class JarvisShell
    {
        private static readonly bool _jarvisActivityBootstrapRegistered = RegisterJarvisActivityBootstrap();
        private bool _jarvisActivityCoreHooked;

        private const string JarvisActivityUiScript = @"
(function () {
  if (window.__jarvisActivityInstalled) return;
  window.__jarvisActivityInstalled = true;

  var active = {};
  var suppress = {};

  var channelMap = {
    main:    { selector: '#transcript', fn: 'addMessage' },
    browser: { selector: '#browserTranscript', fn: 'addBrowserMessage' },
    help:    { selector: '#helpTranscript', fn: 'addHelpMessage' },
    email:   { selector: '#emailTranscript', fn: 'addEmailMessage' },
    courier: { selector: '#courierTranscript', fn: 'addCourierMessage' },
    dr:      { selector: '#drTranscript', fn: null }
  };

  function ensureStyle() {
    if (document.getElementById('jarvisActivityStyle')) return;
    var s = document.createElement('style');
    s.id = 'jarvisActivityStyle';
    s.textContent =
      '.jarvis-activity{align-self:flex-start;display:flex;align-items:center;gap:7px;' +
      'padding:2px 8px 5px 8px;font-size:12px;line-height:1.35;color:var(--text-dim);' +
      'opacity:.62;font-style:italic;user-select:none;pointer-events:none;}' +
      '.jarvis-activity-dot{width:6px;height:6px;border-radius:50%;background:currentColor;' +
      'opacity:.75;animation:jarvisActivityPulse 1.05s ease-in-out infinite;}' +
      '@keyframes jarvisActivityPulse{0%,100%{transform:scale(.72);opacity:.35}50%{transform:scale(1);opacity:.95}}';
    document.head.appendChild(s);
  }

  function cfg(channel) {
    return channelMap[channel] || channelMap.main;
  }

  function transcript(channel) {
    var c = cfg(channel);
    return document.querySelector(c.selector);
  }

  function removeActivity(channel) {
    var el = active[channel];
    if (el && el.parentNode) el.parentNode.removeChild(el);
    delete active[channel];
  }

  function showActivity(channel, text, shouldSuppress) {
    ensureStyle();
    var host = transcript(channel);
    if (!host) return;
    if (shouldSuppress) suppress[channel] = true;

    var el = active[channel];
    if (!el || !el.parentNode) {
      el = document.createElement('div');
      el.className = 'jarvis-activity';
      el.dataset.jarvisActivityChannel = channel;
      var dot = document.createElement('span');
      dot.className = 'jarvis-activity-dot';
      var label = document.createElement('span');
      label.className = 'jarvis-activity-label';
      el.appendChild(dot);
      el.appendChild(label);
      host.appendChild(el);
      active[channel] = el;
    }
    var labelEl = el.querySelector('.jarvis-activity-label');
    if (labelEl) labelEl.textContent = text || 'Επεξεργασία…';
    host.scrollTop = host.scrollHeight;
  }

  function removeLastAssistant(channel) {
    var host = transcript(channel);
    if (!host) return;
    var list = host.querySelectorAll('.msg.assistant');
    var el = list.length ? list[list.length - 1] : null;
    if (!el) return;
    var next = el.nextElementSibling;
    if (next && next.classList && next.classList.contains('jarvis-ai-usage-meta')) next.remove();
    el.remove();
  }

  function addAssistant(channel, text) {
    var c = cfg(channel);
    var fn = c.fn && window[c.fn];
    if (typeof fn === 'function') {
      fn('assistant', text || '');
      return;
    }
    var host = transcript(channel);
    if (!host) return;
    var el = document.createElement('div');
    el.className = 'msg assistant';
    el.textContent = text || '';
    host.appendChild(el);
    host.scrollTop = host.scrollHeight;
  }

  function complete(channel, text) {
    removeActivity(channel);
    removeLastAssistant(channel);
    suppress[channel] = false;
    addAssistant(channel, text);
  }

  function end(channel) {
    removeActivity(channel);
    suppress[channel] = false;
    var host = transcript(channel);
    if (!host) return;
    host.querySelectorAll('.msg.assistant[data-jarvis-suppressed="1"]').forEach(function (x) {
      x.style.display = '';
      delete x.dataset.jarvisSuppressed;
    });
  }

  function hideSuppressed(node) {
    if (!node || node.nodeType !== 1) return;
    Object.keys(suppress).forEach(function (channel) {
      if (!suppress[channel]) return;
      var host = transcript(channel);
      if (!host) return;
      var candidates = [];
      if (node.matches && node.matches('.msg.assistant')) candidates.push(node);
      if (node.querySelectorAll) node.querySelectorAll('.msg.assistant').forEach(function (x) { candidates.push(x); });
      candidates.forEach(function (el) {
        if (!host.contains(el)) return;
        el.dataset.jarvisSuppressed = '1';
        el.style.display = 'none';
      });
    });
  }

  if (document.body) {
    new MutationObserver(function (mutations) {
      mutations.forEach(function (m) { m.addedNodes.forEach(hideSuppressed); });
    }).observe(document.body, { childList: true, subtree: true });
  }

  window.__jarvisActivity = {
    start: function (channel, text, shouldSuppress) { showActivity(channel || 'main', text, !!shouldSuppress); },
    update: function (channel, text) { showActivity(channel || 'main', text, false); },
    end: function (channel) { end(channel || 'main'); },
    complete: function (channel, text) { complete(channel || 'main', text || ''); }
  };

  function inferChannel(type) {
    if (!type) return null;
    if (type === 'thinking_update') return 'main';
    if (type.indexOf('browser_') === 0) return 'browser';
    if (type.indexOf('help_') === 0) return 'help';
    if (type.indexOf('email_') === 0) return 'email';
    if (type.indexOf('courier_') === 0) return 'courier';
    if (type.indexOf('dr_') === 0) return 'dr';
    return null;
  }

  if (window.chrome && window.chrome.webview) {
    window.chrome.webview.addEventListener('message', function (ev) {
      var data = ev.data;
      if (typeof data === 'string') {
        try { data = JSON.parse(data); } catch (_) {
          if (active.main && !suppress.main) end('main');
          return;
        }
      }
      if (!data || typeof data !== 'object') return;

      if (data.type === 'jarvis_activity') {
        var ch = data.channel || 'main';
        if (data.action === 'start') showActivity(ch, data.text, !!data.suppressAssistant);
        else if (data.action === 'update') showActivity(ch, data.text, false);
        else if (data.action === 'complete') complete(ch, data.text || '');
        else if (data.action === 'end') end(ch);
        return;
      }

      var channel = inferChannel(data.type);
      if (!channel) return;

      if (data.type === 'thinking_update' || /_status$/.test(data.type)) {
        if (data.text) showActivity(channel, data.text, false);
        return;
      }

      if (/_reply$/.test(data.type) || /_solution$/.test(data.type) || /_result$/.test(data.type)) {
        if (!suppress[channel]) end(channel);
      }
    });
  }
})();";

        private static bool RegisterJarvisActivityBootstrap()
        {
            EventManager.RegisterClassHandler(
                typeof(JarvisShell),
                FrameworkElement.LoadedEvent,
                new RoutedEventHandler(JarvisActivityLoaded),
                true);
            return true;
        }

        private static void JarvisActivityLoaded(object sender, RoutedEventArgs e)
        {
            var shell = sender as JarvisShell;
            if (shell == null) return;

            shell.webView.CoreWebView2InitializationCompleted -= shell.JarvisActivityCoreInitialized;
            shell.webView.CoreWebView2InitializationCompleted += shell.JarvisActivityCoreInitialized;

            if (shell.webView.CoreWebView2 != null)
                shell.InstallJarvisActivityUi();
        }

        private async void JarvisActivityCoreInitialized(
            object sender,
            Microsoft.Web.WebView2.Core.CoreWebView2InitializationCompletedEventArgs e)
        {
            if (e.IsSuccess)
                InstallJarvisActivityUi();
        }

        private async void InstallJarvisActivityUi()
        {
            if (_jarvisActivityCoreHooked || webView.CoreWebView2 == null) return;
            _jarvisActivityCoreHooked = true;
            try
            {
                await webView.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(JarvisActivityUiScript);
                await webView.CoreWebView2.ExecuteScriptAsync(JarvisActivityUiScript);
                DebugLog.Log("[JARVIS-ACTIVITY] shared activity UI installed");
            }
            catch (Exception ex)
            {
                _jarvisActivityCoreHooked = false;
                DebugLog.Log("[JARVIS-ACTIVITY] install failed: " + ex.Message);
            }
        }

        private void PostJarvisActivity(string action, string channel, string text = null, bool suppressAssistant = false)
        {
            try
            {
                if (webView.CoreWebView2 == null) return;
                var payload = new JObject
                {
                    ["type"] = "jarvis_activity",
                    ["action"] = action ?? "update",
                    ["channel"] = channel ?? "main",
                    ["text"] = text,
                    ["suppressAssistant"] = suppressAssistant
                };
                webView.CoreWebView2.PostWebMessageAsString(payload.ToString(Newtonsoft.Json.Formatting.None));
            }
            catch (Exception ex)
            {
                DebugLog.Log("[JARVIS-ACTIVITY] post failed: " + ex.Message);
            }
        }
    }
}
