using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
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

            // Some model/provider paths historically used dates such as
            // 1970-01-01/2000-01-01 as an artificial "no date supplied"
            // sentinel. Passing that value to the capped deterministic inbox
            // fetch can hide relevant recent mail. Normalize only clearly
            // synthetic/implausibly old dates; real operator dates stay intact.
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

        private static bool ContainsGreekLetters(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return false;
            foreach (char c in value)
                if ((c >= '\u0370' && c <= '\u03FF') || (c >= '\u1F00' && c <= '\u1FFF'))
                    return true;
            return false;
        }

        private static string RemoveDiacritics(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return string.Empty;
            string decomposed = value.Normalize(NormalizationForm.FormD);
            var sb = new StringBuilder(decomposed.Length);
            foreach (char c in decomposed)
            {
                UnicodeCategory category = CharUnicodeInfo.GetUnicodeCategory(c);
                if (category != UnicodeCategory.NonSpacingMark)
                    sb.Append(c);
            }
            return sb.ToString().Normalize(NormalizationForm.FormC);
        }

        private static string TransliterateGreekForInbox(string value)
        {
            string source = RemoveDiacritics(value).ToLowerInvariant();
            var sb = new StringBuilder(source.Length * 2);
            foreach (char c in source)
            {
                switch (c)
                {
                    case 'α': sb.Append('a'); break;
                    case 'β': sb.Append('v'); break;
                    case 'γ': sb.Append('g'); break;
                    case 'δ': sb.Append('d'); break;
                    case 'ε': sb.Append('e'); break;
                    case 'ζ': sb.Append('z'); break;
                    case 'η': sb.Append('i'); break;
                    case 'θ': sb.Append("th"); break;
                    case 'ι': sb.Append('i'); break;
                    case 'κ': sb.Append('k'); break;
                    case 'λ': sb.Append('l'); break;
                    case 'μ': sb.Append('m'); break;
                    case 'ν': sb.Append('n'); break;
                    case 'ξ': sb.Append("x"); break;
                    case 'ο': sb.Append('o'); break;
                    case 'π': sb.Append('p'); break;
                    case 'ρ': sb.Append('r'); break;
                    case 'σ':
                    case 'ς': sb.Append('s'); break;
                    case 'τ': sb.Append('t'); break;
                    case 'υ': sb.Append('i'); break;
                    case 'φ': sb.Append('f'); break;
                    case 'χ': sb.Append("ch"); break;
                    case 'ψ': sb.Append("ps"); break;
                    case 'ω': sb.Append('o'); break;
                    default: sb.Append(c); break;
                }
            }
            return sb.ToString();
        }

        private static string BuildInboxSenderSearchKey(string value)
        {
            if (!ContainsGreekLetters(value))
                return value;

            string latin = TransliterateGreekForInbox(value);
            string[] tokens = latin.Split(
                new[] { ' ', '\t', ',', ';' },
                StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length == 0)
                return value;

            string surname = tokens[tokens.Length - 1].Trim();
            string[] endings = { "ous", "ou", "os", "as", "is", "es", "on", "a", "i", "o", "s" };
            foreach (string ending in endings.OrderByDescending(x => x.Length))
            {
                if (surname.Length >= 6 && surname.EndsWith(ending, StringComparison.Ordinal))
                {
                    surname = surname.Substring(0, surname.Length - ending.Length);
                    break;
                }
            }

            // Keep a useful stem; if stemming became too aggressive, use the
            // transliterated surname instead of broadening the filter too much.
            if (surname.Length < 4)
                surname = tokens[tokens.Length - 1].Trim();

            DebugLog.Log(
                "[main-email-bridge] normalized Greek sender search='" + value +
                "' -> '" + surname + "'");
            return surname;
        }

        private static JArray NormalizeInboxFilters(JArray filters)
        {
            if (filters == null) return null;
            JArray clone = (JArray)filters.DeepClone();
            foreach (JObject filter in clone.OfType<JObject>())
            {
                string field = filter["field"]?.ToString();
                string op = filter["op"]?.ToString();
                string value = filter["value"]?.ToString();
                if (string.Equals(field, "from", StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(op, "contains", StringComparison.OrdinalIgnoreCase) &&
                    ContainsGreekLetters(value))
                {
                    filter["value"] = BuildInboxSenderSearchKey(value);
                }
            }
            return clone;
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

                            string searchText = input["searchText"]?.ToString();
                            if (ContainsGreekLetters(searchText))
                                searchText = BuildInboxSenderSearchKey(searchText);

                            webView.CoreWebView2.PostWebMessageAsString(
                                new JObject
                                {
                                    ["type"] = "email_set_inbox_filter",
                                    ["sinceDate"] = sinceDate,
                                    ["searchText"] = searchText,
                                    ["insight"] = input["insight"]?.DeepClone(),
                                    ["filters"] = NormalizeInboxFilters(input["filters"] as JArray)
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
