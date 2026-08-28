using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Softone;
using S1Jarvis.Access.Verilic;
using S1Jarvis.Core;

namespace S1Jarvis.UI
{
    public partial class JarvisShell
    {
        private const string DrAssistantOrigin = "https://s1jarvis-dr.local/";
        private static readonly bool DrAssistantClassHandlerRegistered = RegisterDrAssistantClassHandler();
        private readonly SemaphoreSlim _drAssistantGate = new SemaphoreSlim(1, 1);
        private readonly Dictionary<string, List<JObject>> _drAssistantHistories =
            new Dictionary<string, List<JObject>>(StringComparer.Ordinal);
        private bool _drAssistantStarted;

        private static bool RegisterDrAssistantClassHandler()
        {
            EventManager.RegisterClassHandler(typeof(JarvisShell), FrameworkElement.LoadedEvent,
                new RoutedEventHandler(JarvisShell_DrAssistantLoaded));
            return true;
        }

        private static void JarvisShell_DrAssistantLoaded(object sender, RoutedEventArgs e)
        {
            var shell = sender as JarvisShell;
            if (shell != null) shell.StartDrAssistant();
        }

        private async void StartDrAssistant()
        {
            if (_drAssistantStarted) return;
            _drAssistantStarted = true;
            try
            {
                for (int attempt = 0; attempt < 200; attempt++)
                {
                    if (webView != null && webView.CoreWebView2 != null) break;
                    await Task.Delay(50);
                }
                if (webView == null || webView.CoreWebView2 == null) return;

                webView.CoreWebView2.AddWebResourceRequestedFilter(
                    DrAssistantOrigin + "*",
                    Microsoft.Web.WebView2.Core.CoreWebView2WebResourceContext.All);
                webView.CoreWebView2.WebResourceRequested += DrAssistant_WebResourceRequested;

                for (int attempt = 0; attempt < 200; attempt++)
                {
                    string ready = await webView.CoreWebView2.ExecuteScriptAsync(
                        "document.getElementById('drWorkflowPane') ? 'ready' : ''");
                    if (!string.IsNullOrWhiteSpace(ready) && ready.IndexOf("ready", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        await InstallDrAssistantUiAsync();
                        return;
                    }
                    await Task.Delay(50);
                }
            }
            catch (Exception ex) { DebugLog.Log("[dr-assistant] startup EXCEPTION: " + ex); }
        }

        private static string NormalizeDrDocumentKey(string key)
        {
            key = (key ?? string.Empty).Trim();
            if (key.Length == 0) return "__session__";
            return key.Length <= 200 ? key : key.Substring(0, 200);
        }

        private List<JObject> GetDrHistory(string documentKey)
        {
            documentKey = NormalizeDrDocumentKey(documentKey);
            List<JObject> history;
            if (!_drAssistantHistories.TryGetValue(documentKey, out history))
            {
                history = new List<JObject>();
                _drAssistantHistories[documentKey] = history;
            }
            return history;
        }

        private async void DrAssistant_WebResourceRequested(object sender,
            Microsoft.Web.WebView2.Core.CoreWebView2WebResourceRequestedEventArgs e)
        {
            Uri uri;
            try { uri = new Uri(e.Request.Uri); } catch { return; }
            if (!string.Equals(uri.Host, "s1jarvis-dr.local", StringComparison.OrdinalIgnoreCase)) return;

            var deferral = e.GetDeferral();
            try
            {
                if (string.Equals(e.Request.Method, "OPTIONS", StringComparison.OrdinalIgnoreCase))
                {
                    e.Response = CreateDrAssistantResponse(204, "No Content", string.Empty);
                    return;
                }

                string requestText = await ReadWebResourceBodyAsync(e.Request.Content);
                JObject request = new JObject();
                if (!string.IsNullOrWhiteSpace(requestText))
                {
                    try { request = JObject.Parse(requestText); }
                    catch
                    {
                        e.Response = CreateDrAssistantResponse(400, "Bad Request",
                            new JObject { ["ok"] = false, ["error"] = "invalid_request" }.ToString(Formatting.None));
                        return;
                    }
                }

                if (string.Equals(uri.AbsolutePath, "/reset", StringComparison.OrdinalIgnoreCase))
                {
                    string key = request["documentKey"]?.ToString();
                    await _drAssistantGate.WaitAsync();
                    try
                    {
                        if (string.IsNullOrWhiteSpace(key)) _drAssistantHistories.Clear();
                        else _drAssistantHistories.Remove(NormalizeDrDocumentKey(key));
                    }
                    finally { _drAssistantGate.Release(); }
                    e.Response = CreateDrAssistantResponse(200, "OK", new JObject { ["ok"] = true }.ToString(Formatting.None));
                    return;
                }

                if (string.Equals(uri.AbsolutePath, "/item-template", StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(e.Request.Method, "POST", StringComparison.OrdinalIgnoreCase))
                {
                    int? templateMtrl = request["templateMtrl"]?.ToObject<int?>();
                    JObject payload = await Dispatcher.InvokeAsync(() => BuildDrItemTemplatePayload(templateMtrl));
                    e.Response = CreateDrAssistantResponse(200, "OK", payload.ToString(Formatting.None));
                    return;
                }

                if (string.Equals(uri.AbsolutePath, "/create-item", StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(e.Request.Method, "POST", StringComparison.OrdinalIgnoreCase))
                {
                    if ((bool?)request["confirm"] != true)
                    {
                        e.Response = CreateDrAssistantResponse(400, "Bad Request",
                            new JObject { ["ok"] = false, ["error"] = "confirmation_required" }.ToString(Formatting.None));
                        return;
                    }
                    JObject input = request["input"] as JObject;
                    if (input == null)
                    {
                        e.Response = CreateDrAssistantResponse(400, "Bad Request",
                            new JObject { ["ok"] = false, ["error"] = "item_input_missing" }.ToString(Formatting.None));
                        return;
                    }
                    try
                    {
                        string raw = await Dispatcher.InvokeAsync(() => JarvisItems.ExecuteCreateItem(_xSupport, input));
                        JObject created = JObject.Parse(raw);
                        created["ok"] = (bool?)created["success"] == true;
                        e.Response = CreateDrAssistantResponse(200, "OK", created.ToString(Formatting.None));
                    }
                    catch (Exception ex)
                    {
                        DebugLog.Log("[dr-assistant] create item EXCEPTION: " + ex);
                        e.Response = CreateDrAssistantResponse(400, "Bad Request",
                            new JObject { ["ok"] = false, ["error"] = "item_create_failed", ["message"] = ex.Message }.ToString(Formatting.None));
                    }
                    return;
                }

                if (!string.Equals(uri.AbsolutePath, "/chat", StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(e.Request.Method, "POST", StringComparison.OrdinalIgnoreCase))
                {
                    e.Response = CreateDrAssistantResponse(404, "Not Found",
                        new JObject { ["ok"] = false, ["error"] = "not_found" }.ToString(Formatting.None));
                    return;
                }

                string text = (request["text"]?.ToString() ?? string.Empty).Trim();
                if (text.Length > 4000) text = text.Substring(0, 4000);
                string context = request["context"] == null ? string.Empty : request["context"].ToString(Formatting.None);
                if (context.Length > 24000) context = context.Substring(0, 24000);
                string documentKey = NormalizeDrDocumentKey(request["documentKey"]?.ToString());

                if (string.IsNullOrWhiteSpace(text))
                {
                    e.Response = CreateDrAssistantResponse(400, "Bad Request",
                        new JObject { ["ok"] = false, ["error"] = "empty_message" }.ToString(Formatting.None));
                    return;
                }
                if (!_drAllowed)
                {
                    e.Response = CreateDrAssistantResponse(403, "Forbidden",
                        new JObject { ["ok"] = false, ["error"] = "dr_not_available", ["reply"] = "Το DR δεν έχει περάσει ακόμη τον έλεγχο άδειας για αυτή τη συνεδρία." }.ToString(Formatting.None));
                    return;
                }

                JObject answer = await SendDrAssistantMessageAsync(documentKey, text, context);
                e.Response = CreateDrAssistantResponse((bool?)answer["ok"] == true ? 200 : 502,
                    (bool?)answer["ok"] == true ? "OK" : "Bad Gateway", answer.ToString(Formatting.None));
            }
            catch (Exception ex)
            {
                DebugLog.Log("[dr-assistant] request EXCEPTION: " + ex);
                e.Response = CreateDrAssistantResponse(500, "Internal Server Error",
                    new JObject { ["ok"] = false, ["error"] = "assistant_failed", ["reply"] = "Η διευκρίνιση δεν μπόρεσε να ολοκληρωθεί." }.ToString(Formatting.None));
            }
            finally { deferral.Complete(); }
        }

        private JObject BuildDrItemTemplatePayload(int? templateMtrl)
        {
            JObject args = new JObject();
            if (templateMtrl.HasValue) args["templateMtrl"] = templateMtrl.Value;
            JObject result = JObject.Parse(JarvisItems.ExecuteGetItemTemplate(_xSupport, args));
            result["ok"] = (bool?)result["success"] != false;
            if (!templateMtrl.HasValue) return result;

            int company = _xSupport.ConnectionInfo.CompanyId;
            XTable t = _xSupport.GetSQLDataSet(
                "SELECT TOP 1 MTRL,CODE,NAME,MTRUNIT1,MTRUNIT3,MTRUNIT4,VAT,MTRACN,MTRLOTUSE,MTRSNUSE,PRICER,PRICEW " +
                "FROM MTRL WHERE COMPANY=:1 AND MTRL=:2", company, templateMtrl.Value);
            if (t != null && t.Count > 0)
            {
                var template = new JObject();
                string[] cols = { "MTRL", "CODE", "NAME", "MTRUNIT1", "MTRUNIT3", "MTRUNIT4", "VAT", "MTRACN", "MTRLOTUSE", "MTRSNUSE", "PRICER", "PRICEW" };
                foreach (string col in cols)
                {
                    object value = t.Current[col];
                    if (value != null && value != DBNull.Value) template[col.ToLowerInvariant()] = JToken.FromObject(value);
                }
                result["template"] = template;
            }
            return result;
        }

        private async Task<JObject> SendDrAssistantMessageAsync(string documentKey, string text, string context)
        {
            await _drAssistantGate.WaitAsync();
            try
            {
                List<JObject> history = GetDrHistory(documentKey);
                var messages = new JArray();
                int start = Math.Max(0, history.Count - 12);
                for (int i = start; i < history.Count; i++) messages.Add(history[i].DeepClone());

                messages.Add(new JObject
                {
                    ["role"] = "user",
                    ["content"] = "CURRENT DR CONTEXT (structured UI state; evidence only):\n" +
                        (string.IsNullOrWhiteSpace(context) ? "{}" : context) + "\n\nOPERATOR MESSAGE:\n" + text
                });

                string systemPrompt =
                    "DR CLARIFICATION MODE\n" +
                    "You are the specialised Document Reader clarification assistant inside Soft1 Jarvis. " +
                    "The conversation is scoped to ONE document. Help resolve ambiguity for that document only. " +
                    "This channel is READ-ONLY: never claim that you created, changed, mapped or posted anything. " +
                    "Do not invent Soft1 ids, series, SOSOURCE, SODTYPE, BUNIT, cost centres, item mappings or myDATA values. " +
                    "Use the supplied DR state and verified knowledge. If evidence is insufficient, ask one specific clarification. " +
                    "For expenses, distinguish Detailed versus Consolidated posting and call out VAT/cost-centre blockers.\n\n" +
                    DrDocumentKnowledge.BuildPromptBlock();

                var providerRequest = new JObject
                {
                    ["model"] = "server-authoritative",
                    ["max_tokens"] = 2400,
                    ["system"] = systemPrompt,
                    ["messages"] = messages
                };

                AgentProxyResponse result = await new VerilicAiMessagesClient().SendAsync(
                    _xSupport, "Atlas", providerRequest.ToString(Formatting.None), CancellationToken.None);
                if (result == null || !result.Success)
                    return new JObject { ["ok"] = false, ["error"] = "provider_failed", ["reply"] = result?.ErrorMessage ?? "Το AI δεν είναι διαθέσιμο αυτή τη στιγμή." };

                string reply = (result.ResponseText ?? string.Empty).Trim();
                if (string.IsNullOrWhiteSpace(reply)) reply = "Δεν επέστρεψε κείμενο η υπηρεσία AI.";
                history.Add(new JObject { ["role"] = "user", ["content"] = text });
                history.Add(new JObject { ["role"] = "assistant", ["content"] = reply });
                while (history.Count > 12) history.RemoveAt(0);

                return new JObject
                {
                    ["ok"] = true, ["reply"] = reply, ["documentKey"] = documentKey,
                    ["agent"] = "DR", ["runtimeAgent"] = result.RuntimeAgent ?? string.Empty,
                    ["provider"] = result.RuntimeProvider ?? string.Empty, ["model"] = result.RuntimeModel ?? string.Empty,
                    ["routing"] = result.RuntimeRouting ?? string.Empty
                };
            }
            finally { _drAssistantGate.Release(); }
        }

        private static async Task<string> ReadWebResourceBodyAsync(Stream stream)
        {
            if (stream == null) return string.Empty;
            using (var reader = new StreamReader(stream, Encoding.UTF8, true, 1024, true)) return await reader.ReadToEndAsync();
        }

        private Microsoft.Web.WebView2.Core.CoreWebView2WebResourceResponse CreateDrAssistantResponse(int statusCode, string reasonPhrase, string json)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(json ?? string.Empty);
            var stream = new MemoryStream(bytes, false);
            string headers = "Content-Type: application/json; charset=utf-8\r\nCache-Control: no-store\r\n" +
                "Access-Control-Allow-Origin: https://s1jarvis.local\r\nAccess-Control-Allow-Methods: POST, OPTIONS\r\nAccess-Control-Allow-Headers: Content-Type";
            return webView.CoreWebView2.Environment.CreateWebResourceResponse(stream, statusCode, reasonPhrase, headers);
        }

        private async Task InstallDrAssistantUiAsync()
        {
            if (webView == null || webView.CoreWebView2 == null) return;
            string script = @"
(function(){
  if(window.__jarvisDrAssistantInstalled)return;
  var host=document.getElementById('drWorkflowPane'); if(!host)return;
  var style=document.createElement('style'); style.id='drAssistantStyle'; style.textContent=`
    #drAssistantPanel{position:sticky;bottom:8px;z-index:30;width:min(760px,96%);margin:12px auto 4px;background:rgba(35,35,52,.98);border:1px solid rgba(255,255,255,.12);border-radius:12px;box-shadow:0 8px 28px rgba(0,0,0,.28);flex:none}
    #drAssistantPanel>summary{cursor:pointer;user-select:none;padding:9px 12px;color:#d8d5ff;font-size:12.5px;font-weight:600;list-style:none}
    .dr-assistant-body{padding:0 10px 10px}.dr-assistant-log{max-height:170px;overflow:auto;display:flex;flex-direction:column;gap:7px;margin:0 0 8px}
    .dr-assistant-msg{max-width:88%;padding:7px 9px;border-radius:9px;font-size:12px;line-height:1.4;white-space:pre-wrap;word-break:break-word}.dr-assistant-msg.user{align-self:flex-end;background:rgba(139,123,255,.30)}.dr-assistant-msg.agent{align-self:flex-start;background:rgba(255,255,255,.07);border:1px solid rgba(255,255,255,.07)}
    .dr-assistant-meta{font-size:10px;opacity:.55;margin-top:3px}.dr-assistant-compose{display:flex;gap:7px;align-items:flex-end}#drAssistantInput{flex:1;min-height:36px;max-height:90px;resize:vertical;background:#1f1f30;color:#fff;border:1px solid rgba(255,255,255,.12);border-radius:9px;padding:8px 9px;font:12px 'Segoe UI',sans-serif;outline:none}#drAssistantSend{flex:none;border:0;border-radius:9px;background:#7566ef;color:#fff;padding:9px 12px;font-size:12px;cursor:pointer}#drAssistantSend:disabled{opacity:.45;cursor:default}`;
  document.head.appendChild(style);
  var panel=document.createElement('details'); panel.id='drAssistantPanel'; panel.innerHTML=`<summary>💬 Διευκρινίσεις DR · ανά παραστατικό</summary><div class='dr-assistant-body'><div class='dr-assistant-log' id='drAssistantLog'></div><div class='dr-assistant-compose'><textarea id='drAssistantInput' placeholder='Ρώτησε ή απάντησε για το ενεργό παραστατικό…'></textarea><button type='button' id='drAssistantSend'>Αποστολή</button></div></div>`;
  function attachToActiveSlot(){var slot=document.querySelector('.dr-session-active .dr-session-chat-slot');if(slot&&panel.parentNode!==slot){slot.appendChild(panel);var s=panel.querySelector('summary');var active=document.querySelector('.dr-session-active');var title=active&&active.querySelector('.dr-session-active-head span');if(s&&title)s.textContent='Διευκρινίσεις για · '+String(title.textContent||'').replace(/^Ενεργό παραστατικό\s*·\s*/,'');return true;}return !!slot;}
  if(!attachToActiveSlot())host.appendChild(panel);
  var slotObserver=new MutationObserver(function(){attachToActiveSlot();});slotObserver.observe(host,{childList:true,subtree:true});
  var log=document.getElementById('drAssistantLog'), input=document.getElementById('drAssistantInput'), send=document.getElementById('drAssistantSend');
  function activeEntry(){ if(typeof drFiles==='undefined'||!Array.isArray(drFiles))return null; var e=null; if(typeof drActiveFileId!=='undefined'&&drActiveFileId!==null)e=drFiles.find(x=>x&&x.id===drActiveFileId); if(!e){var v=drFiles.filter(x=>x&&!x.standalone);e=v.length?v[v.length-1]:null;} return e; }
  function add(kind,text,meta){var b=document.createElement('div');b.className='dr-assistant-msg '+(kind==='user'?'user':'agent');b.textContent=text||'';if(meta){var m=document.createElement('div');m.className='dr-assistant-meta';m.textContent=meta;b.appendChild(m);}log.appendChild(b);log.scrollTop=log.scrollHeight;}
  function renderHistory(e){log.innerHTML='';if(!e)return;(e.drAssistantMessages||[]).forEach(x=>add(x.kind,x.text,x.meta));}
  function clone(v){try{return JSON.parse(JSON.stringify(v));}catch(_e){return null;}}
  function context(e){var c={};if(!e)return c;c.file={name:e.file&&e.file.name||e.name||'',type:e.file&&e.file.type||'',size:e.file&&e.file.size||0};['status','statusText','detail','detection','trader','seriesHistory','duplicateCheck','extraction','manualStep','manualMode','manualSosource','manualSeries','manualLineMtrlIds','registrationResult'].forEach(k=>{if(e[k]!==undefined&&e[k]!==null)c[k]=clone(e[k]);});return c;}
  async function sendMessage(){var e=activeEntry(),text=(input.value||'').trim();if(!e){panel.open=true;add('agent','Επίλεξε πρώτα ένα παραστατικό.','');return;}if(!text||send.disabled)return;if(!e.drAssistantMessages)e.drAssistantMessages=[];panel.open=true;e.drAssistantMessages.push({kind:'user',text:text,meta:''});renderHistory(e);input.value='';send.disabled=true;try{var r=await fetch('https://s1jarvis-dr.local/chat',{method:'POST',headers:{'Content-Type':'text/plain;charset=UTF-8'},body:JSON.stringify({documentKey:String(e.id),text:text,context:context(e)})});var d=await r.json();var meta=d&&d.provider?['DR',d.provider,d.model,d.routing].filter(Boolean).join(' · '):'';e.drAssistantMessages.push({kind:'agent',text:(d&&d.reply)||'Δεν ήταν δυνατή η απάντηση.',meta:meta});renderHistory(e);}catch(err){e.drAssistantMessages.push({kind:'agent',text:'Η διευκρίνιση δεν μπόρεσε να σταλεί: '+String(err&&err.message||err),meta:''});renderHistory(e);}finally{send.disabled=false;input.focus();}}
  send.addEventListener('click',sendMessage);input.addEventListener('keydown',e=>{if(e.key==='Enter'&&!e.shiftKey){e.preventDefault();sendMessage();}});
  if(typeof window.setDrActiveFile==='function'){var original=window.setDrActiveFile;window.setDrActiveFile=function(entry){var r=original.apply(this,arguments);attachToActiveSlot();renderHistory(entry);return r;};}
  var curtain=document.getElementById('drCurtain');if(curtain){new MutationObserver(()=>{if(!curtain.classList.contains('open')){log.innerHTML='';fetch('https://s1jarvis-dr.local/reset',{method:'POST',headers:{'Content-Type':'text/plain;charset=UTF-8'},body:'{}'}).catch(()=>{});}}).observe(curtain,{attributes:true,attributeFilter:['class']});}
  attachToActiveSlot();renderHistory(activeEntry()); window.__jarvisDrAssistantInstalled=true;
})();";
            await webView.CoreWebView2.ExecuteScriptAsync(script);
        }
    }
}
