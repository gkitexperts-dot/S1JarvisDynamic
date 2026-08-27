using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using S1Jarvis.Core;

namespace S1Jarvis.UI
{
    public partial class JarvisShell
    {
        private readonly HashSet<string> _mainEmailUiBridgedToolIds =
            new HashSet<string>(StringComparer.Ordinal);
        private DispatcherTimer _mainEmailUiBridgeTimer;

        public override void OnApplyTemplate()
        {
            base.OnApplyTemplate();

            if (_mainEmailUiBridgeTimer != null)
                return;

            _mainEmailUiBridgeTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(300)
            };
            _mainEmailUiBridgeTimer.Tick += MainEmailUiBridgeTimer_Tick;
            _mainEmailUiBridgeTimer.Start();
        }

        private static string NormalizeInboxSinceDate(string sinceDate)
        {
            if (string.IsNullOrWhiteSpace(sinceDate))
                return sinceDate;

            DateTime parsed;
            if (!DateTime.TryParseExact(
                    sinceDate.Trim(),
                    "yyyy-MM-dd",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out parsed) &&
                !DateTime.TryParse(sinceDate, out parsed))
                return sinceDate;

            // Some model/provider paths historically used 2000-01-01 as an
            // artificial "no date supplied" sentinel. Passing that value to
            // the deterministic inbox UI is misleading and, because the Graph
            // fetch is capped, can also hide a relevant sender among newer mail.
            // Only normalize clearly synthetic/implausibly old dates. A real
            // date supplied by the operator remains byte-for-byte authoritative.
            DateTime oldestReasonableUserDate = DateTime.Today.AddYears(-5);
            if (parsed.Date < oldestReasonableUserDate)
            {
                string normalized = DateTime.Today.AddYears(-1).ToString("yyyy-MM-dd");
                DebugLog.Log(
                    "[main-email-bridge] normalized synthetic inbox sinceDate=" +
                    sinceDate + " -> " + normalized);
                return normalized;
            }

            return parsed.ToString("yyyy-MM-dd");
        }

        private void MainEmailUiBridgeTimer_Tick(object sender, EventArgs e)
        {
            try
            {
                if (webView == null || webView.CoreWebView2 == null ||
                    _conversation == null || _conversation.Count == 0)
                    return;

                var completedToolIds = new HashSet<string>(StringComparer.Ordinal);
                foreach (JObject message in _conversation)
                {
                    JArray blocks = message?["content"] as JArray;
                    if (blocks == null) continue;

                    foreach (JToken block in blocks)
                    {
                        if (!string.Equals(
                            block?["type"]?.ToString(),
                            "tool_result",
                            StringComparison.OrdinalIgnoreCase))
                            continue;

                        string toolUseId = block?["tool_use_id"]?.ToString();
                        if (!string.IsNullOrWhiteSpace(toolUseId))
                            completedToolIds.Add(toolUseId);
                    }
                }

                if (completedToolIds.Count == 0)
                    return;

                foreach (JObject message in _conversation)
                {
                    JArray blocks = message?["content"] as JArray;
                    if (blocks == null) continue;

                    foreach (JToken block in blocks)
                    {
                        if (!string.Equals(
                            block?["type"]?.ToString(),
                            "tool_use",
                            StringComparison.OrdinalIgnoreCase))
                            continue;

                        string toolUseId = block?["id"]?.ToString();
                        if (string.IsNullOrWhiteSpace(toolUseId) ||
                            !completedToolIds.Contains(toolUseId) ||
                            _mainEmailUiBridgedToolIds.Contains(toolUseId))
                            continue;

                        string toolName = block?["name"]?.ToString();
                        JObject input = block?["input"] as JObject ?? new JObject();

                        if (string.Equals(toolName, "filter_email_inbox", StringComparison.OrdinalIgnoreCase))
                        {
                            string sinceDate = NormalizeInboxSinceDate(
                                input["sinceDate"]?.ToString());
                            if (string.IsNullOrWhiteSpace(sinceDate))
                                continue;

                            webView.CoreWebView2.PostWebMessageAsString(
                                new JObject
                                {
                                    ["type"] = "email_set_inbox_filter",
                                    ["sinceDate"] = sinceDate,
                                    ["searchText"] = input["searchText"]?.DeepClone(),
                                    ["insight"] = input["insight"]?.DeepClone(),
                                    ["filters"] = input["filters"]?.DeepClone()
                                }.ToString(Formatting.None));

                            _mainEmailUiBridgedToolIds.Add(toolUseId);
                            DebugLog.Log("[main-email-bridge] inbox filter posted toolUseId=" + toolUseId);
                        }
                        else if (string.Equals(toolName, "filter_calendar", StringComparison.OrdinalIgnoreCase))
                        {
                            string date = input["date"]?.ToString();
                            if (string.IsNullOrWhiteSpace(date))
                                continue;

                            webView.CoreWebView2.PostWebMessageAsString(
                                new JObject
                                {
                                    ["type"] = "email_set_calendar_filter",
                                    ["date"] = date,
                                    ["searchText"] = input["searchText"]?.DeepClone(),
                                    ["insight"] = input["insight"]?.DeepClone()
                                }.ToString(Formatting.None));

                            _mainEmailUiBridgedToolIds.Add(toolUseId);
                            DebugLog.Log("[main-email-bridge] calendar filter posted toolUseId=" + toolUseId);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                DebugLog.Log("[main-email-bridge] EXCEPTION: " + ex.Message);
            }
        }
    }
}
