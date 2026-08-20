using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading.Tasks;
using Microsoft.Identity.Client;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Softone;

namespace S1Jarvis.Core
{
    // ══════════════════════════════════════════════════════════════════════
    // JarvisEmailAccess - ΝΕΟ 17/08, ρητό αίτημα χρήστη: πρόσβαση του Jarvis
    // στο email του χειριστή. Office 365 πρώτα (Microsoft Graph), Application
    // permission (Mail.Read) - ΟΧΙ Delegated (ρητή απόφαση χρήστη:
    // "θα καλύπτει έναν χρήστη, δεν βολεύει" - το delegated flow θα
    // χρειαζόταν interactive login ΑΝΑ χρήστη). Client credentials flow -
    // ΕΝΑ app-level token, μετά διαβάζει ΟΠΟΙΟΔΗΠΟΤΕ mailbox (email address)
    // δίνοντάς το ρητά στο Graph API call (GET /users/{email}/messages) -
    // ΔΕΝ χρειάζεται κανένα login popup ανά χρήστη.
    //
    // ΑΣΦΑΛΕΙΑ: το permission Mail.Read (Application) δίνει ΘΕΩΡΗΤΙΚΑ
    // πρόσβαση σε ΟΛΑ τα mailboxes του tenant - περιορίζεται ΠΡΑΓΜΑΤΙΚΑ σε
    // συγκεκριμένα mailboxes μέσω Exchange Online Application Access Policy
    // (New-ApplicationAccessPolicy, ρυθμίζεται ΕΚΤΟΣ κώδικα, από Exchange
    // admin). Το Client Secret (ParamCode 500021) είναι ουσιαστικά password -
    // ΙΔΙΟ ParamCode idiom με όλα τα άλλα (ρητή απόφαση χρήστη), αλλά
    // χειρίσου το ανάλογα (ποιος έχει πρόσβαση στο Soft1 Designer/Params).
    //
    // Compile-checked ΞΕΧΩΡΙΣΤΑ (throwaway net48 project + πραγματικό
    // Softone.Lib.dll) ΠΡΙΝ προστεθεί στο πραγματικό project - το
    // ConfidentialClientApplicationBuilder/AcquireTokenForClient API shape
    // επιβεβαιώθηκε ζωντανά, ΟΧΙ εικασία.
    //
    // ParamCodes:
    //   500019 - Client ID (Application/client ID από το Azure AD App Registration)
    //   500020 - Tenant ID (Directory/tenant ID)
    //   500021 - Client Secret (η τιμή του secret, ΟΧΙ το Secret ID)
    // ══════════════════════════════════════════════════════════════════════
    internal static class JarvisEmailAccess
    {
        private static readonly HttpClient _http = new HttpClient();

        // ΙΔΙΟ idiom με τα υπόλοιπα ParamCode readers στο JarvisTools.cs
        // (π.χ. GetCrmTaskOptionalParam) - throw αν λείπει, ΧΩΡΙΣ αυτά τα 3
        // δεν μπορεί να λειτουργήσει καθόλου το email feature (ΟΧΙ ασφαλές
        // fallback σε default όπως άλλα προαιρετικά ParamCodes).
        //
        // ΝΕΟ 17/08, ρητό αίτημα χρήστη - φιλτράρει paramsIsActive (1 Ή
        // NULL γίνονται δεκτά, ΜΟΝΟ 0 αποκλείεται - τα περισσότερα ΠΑΛΙΑ
        // ParamCodes σε αυτό το αρχείο δεν έχουν καν συμπληρωμένο αυτό το
        // πεδίο, δεν πρέπει να "σπάσουν" απαιτώντας ρητά =1) + TOP 1 ORDER
        // BY cccParams DESC, ώστε αν υπάρχουν ΔΙΠΛΕΣ γραμμές για το ίδιο
        // ParamCode (π.χ. παλιά ανενεργή + καινούρια), να παίρνουμε ΠΑΝΤΑ
        // τη σωστή/ενεργή/πιο πρόσφατη - ΟΧΙ ό,τι τύχει να γυρίσει πρώτο.
        private static string GetRequiredParamString(XSupport xSupport, int paramCode, string label)
        {
            XTable t = xSupport.GetSQLDataSet(
                $"SELECT TOP 1 ParamValueString FROM cccParams " +
                $"WHERE ParamCode={paramCode} AND (paramsIsActive=1 OR paramsIsActive IS NULL) " +
                $"ORDER BY cccParams DESC");
            if (t == null || t.Count == 0 || t.Current["ParamValueString"] == DBNull.Value)
                throw new Exception($"Δεν βρέθηκε ενεργή παράμετρος {paramCode} ({label}) στο cccParams.");
            string value = t.Current["ParamValueString"].ToString();
            if (string.IsNullOrWhiteSpace(value))
                throw new Exception($"Η παράμετρος {paramCode} ({label}) είναι κενή.");
            return value;
        }

        // ConfidentialClientApplication με δικό του, internal in-memory token
        // cache (η ίδια η MSAL φροντίζει reuse μέχρι λήξη + αυτόματο refresh
        // στο ΕΠΟΜΕΝΟ AcquireTokenForClient - ΔΕΝ χρειάζεται δικό μας
        // caching/expiry logic εδώ). Static ώστε να ξαναχρησιμοποιείται η
        // cache μεταξύ κλήσεων (ΟΧΙ νέο instance/νέο network call κάθε φορά).
        private static IConfidentialClientApplication _app;
        private static readonly object _appLock = new object();

        private static IConfidentialClientApplication GetOrCreateApp(XSupport xSupport)
        {
            if (_app != null) return _app;
            lock (_appLock)
            {
                if (_app != null) return _app;
                string clientId = GetRequiredParamString(xSupport, 500019, "Email OAuth Client ID");
                string tenantId = GetRequiredParamString(xSupport, 500020, "Email OAuth Tenant ID");
                string clientSecret = GetRequiredParamString(xSupport, 500021, "Email OAuth Client Secret");

                _app = ConfidentialClientApplicationBuilder
                    .Create(clientId)
                    .WithClientSecret(clientSecret)
                    .WithAuthority($"https://login.microsoftonline.com/{tenantId}")
                    .Build();
                return _app;
            }
        }

        private static async Task<string> GetAccessTokenAsync(XSupport xSupport)
        {
            var app = GetOrCreateApp(xSupport);
            var scopes = new[] { "https://graph.microsoft.com/.default" };
            AuthenticationResult result = await app.AcquireTokenForClient(scopes).ExecuteAsync();
            return result.AccessToken;
        }

        // ΙΔΙΟ lookup pattern με JarvisShell.GetDisplayName - PRSN.EMAIL μέσω
        // PRSN.USERS=ConnectionInfo.UserId, fallback USERS.EMAIL (π.χ. για
        // generic λογαριασμούς χωρίς PRSN, τύπου "Administrator").
        private static string GetCurrentUserEmail(XSupport xSupport)
        {
            int userId = xSupport.ConnectionInfo.UserId;
            XTable t = xSupport.GetSQLDataSet(
                "SELECT EMAIL FROM PRSN WHERE USERS=:1", userId);
            if (t != null && t.Count > 0 && t.Current["EMAIL"] != DBNull.Value)
            {
                string email = t.Current["EMAIL"].ToString();
                if (!string.IsNullOrWhiteSpace(email)) return email;
            }
            XTable t2 = xSupport.GetSQLDataSet(
                "SELECT EMAIL FROM USERS WHERE USERS=:1", userId);
            if (t2 != null && t2.Count > 0 && t2.Current["EMAIL"] != DBNull.Value)
                return t2.Current["EMAIL"].ToString();
            return null;
        }

        // read_email tool - ΝΕΟ 17/08. Διαβάζει τα ΠΙΟ ΠΡΟΣΦΑΤΑ emails ενός
        // mailbox (Graph API GET /users/{email}/messages, $top/$select/
        // $orderby) - ΜΟΝΟ ανάγνωση, καμία εγγραφή/αποστολή/διαγραφή.
        public static readonly object ReadEmailToolDefinition = new
        {
            name = "read_email",
            description =
                "Διαβάζει τα πιο πρόσφατα emails από το Inbox του τρέχοντος " +
                "χειριστή (Office 365/Exchange Online) - ΜΟΝΟ ανάγνωση, καμία " +
                "αποστολή/διαγραφή/τροποποίηση. Χρησιμοποίησέ το όταν ο " +
                "χειριστής ζητήσει κάτι σχετικό με τα email του (π.χ. " +
                "\"τι email έχω\", \"δες αν ήρθε απάντηση από τον Χ\"). Κάθε " +
                "email έχει 'id' και 'hasAttachments' - αν ο χειριστής " +
                "ζητήσει να κατεβάσεις συνημμένο, χρησιμοποίησε το 'id' " +
                "ΑΥΤΟΥ του email στο download_email_attachment.",
            input_schema = new
            {
                type = "object",
                properties = new
                {
                    count = new
                    {
                        type = "integer",
                        description = "Πόσα emails να φέρει (πιο πρόσφατα πρώτα). Default 10, max 50."
                    },
                    searchText = new
                    {
                        type = "string",
                        description = "Προαιρετικό - φιλτράρει με βάση κείμενο (θέμα/αποστολέα/περιεχόμενο), αν το ζήτησε ο χειριστής."
                    }
                },
                required = new string[0]
            }
        };

        public static async Task<string> ExecuteReadEmail(XSupport xSupport, JObject input)
        {
            int count = (int?)input?["count"] ?? 10;
            if (count <= 0) count = 10;
            if (count > 50) count = 50;
            string searchText = input?["searchText"]?.ToString();

            string userEmail = GetCurrentUserEmail(xSupport);
            if (string.IsNullOrWhiteSpace(userEmail))
                throw new Exception("Δεν βρέθηκε email για τον τρέχοντα χειριστή (PRSN.EMAIL/USERS.EMAIL).");

            string token = await GetAccessTokenAsync(xSupport);

            // ΝΕΟ 17/08, ζωντανό bug (χρήστης εντόπισε): το Microsoft Graph
            // ΔΕΝ επιτρέπει $search ΜΑΖΙ με $orderby στο /messages endpoint -
            // 400 Bad Request αν συνδυαστούν (τεκμηριωμένος περιορισμός του
            // Graph API, ΟΧΙ θέμα δικαιωμάτων/Application Access Policy όπως
            // αρχικά φάνηκε). Όταν υπάρχει searchText, παραλείπουμε το
            // $orderby - το $search ήδη επιστρέφει με τη δική του σειρά
            // σχετικότητας.
            // ΝΕΟ 17/08 - id/hasAttachments προστέθηκαν ώστε ο Jarvis να ξέρει
            // ΠΟΙΟ email έχει συνημμένα (και το id του, απαραίτητο για το
            // επόμενο βήμα - download_email_attachment).
            string url = $"https://graph.microsoft.com/v1.0/users/{Uri.EscapeDataString(userEmail)}/messages" +
                $"?$top={count}&$select=id,subject,from,receivedDateTime,bodyPreview,isRead,webLink,hasAttachments";
            if (!string.IsNullOrWhiteSpace(searchText))
                url += $"&$search=\"{Uri.EscapeDataString(searchText)}\"";
            else
                url += "&$orderby=receivedDateTime desc";

            using (var req = new HttpRequestMessage(HttpMethod.Get, url))
            {
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                // $search απαιτεί ρητά το header ConsistencyLevel: eventual
                // στο Graph API (advanced query capability) - χωρίς αυτό
                // επιστρέφει ΚΙ ΑΥΤΟ 400, ανεξάρτητα από το $orderby conflict
                // παραπάνω. Άκυρο/άσχετο όταν ΔΕΝ γίνεται search, αλλά
                // αβλαβές να μπαίνει πάντα.
                req.Headers.Add("ConsistencyLevel", "eventual");
                using (var resp = await _http.SendAsync(req))
                {
                    string body = await resp.Content.ReadAsStringAsync();
                    if (!resp.IsSuccessStatusCode)
                    {
                        DebugLog.Log($"[email] Graph API error {(int)resp.StatusCode}: {body}");
                        throw new Exception($"Αποτυχία ανάγνωσης email (Graph API {(int)resp.StatusCode}) - " +
                            "πιθανό θέμα δικαιωμάτων (Application Access Policy) ή ρύθμισης.");
                    }

                    JObject parsed = JObject.Parse(body);
                    var items = new JArray();
                    foreach (var m in parsed["value"] as JArray ?? new JArray())
                    {
                        items.Add(new JObject
                        {
                            ["id"] = m["id"],
                            ["subject"] = m["subject"],
                            ["from"] = m["from"]?["emailAddress"]?["address"],
                            ["fromName"] = m["from"]?["emailAddress"]?["name"],
                            ["receivedDateTime"] = m["receivedDateTime"],
                            ["preview"] = m["bodyPreview"],
                            ["isRead"] = m["isRead"],
                            ["webLink"] = m["webLink"],
                            ["hasAttachments"] = m["hasAttachments"]
                        });
                    }
                    return JsonConvert.SerializeObject(new { success = true, mailbox = userEmail, emails = items });
                }
            }
        }

        // ── Αποστολή/Απάντηση email - ΝΕΟ 18/08, ρητό αίτημα χρήστη: "θα
        // πρέπει να το βάλουμε να στέλνει email". ΔΥΟ entry points στο ΙΔΙΟ
        // deterministic backend (SendEmailAsync/ReplyEmailAsync εδώ) - (1)
        // LLM tools (send_email/reply_email, με υποχρεωτική επιβεβαίωση στο
        // system prompt - βλ. JarvisAgentClient.cs), (2) κουμπιά "✎ Νέο
        // email"/"↩ Απάντηση" στην κουρτίνα Email (JarvisShell.xaml.cs,
        // ΧΩΡΙΣ LLM). ΙΔΙΟ mailbox με την ανάγνωση (GetCurrentUserEmail -
        // "ίδιο με αυτό που διαβάζει το inbox", ρητή απόφαση χρήστη) -
        // ΧΡΕΙΑΖΕΤΑΙ ΕΠΙΠΛΕΟΝ Application permission "Mail.Send" στο ΙΔΙΟ
        // Azure AD App Registration (μόνο Mail.Read υπήρχε πριν) - ΔΕΝ
        // μπορεί να γίνει από κώδικα, χρειάζεται admin consent στο Azure.
        //
        // sendMail: POST .../sendMail, ΝΕΟ email (200/202, ΚΕΝΟ body σε
        // επιτυχία). reply: POST .../messages/{id}/reply - ΠΡΑΓΜΑΤΙΚΟ Graph
        // reply (σωστό threading/References headers, "RE:" subject
        // αυτόματο) - ΟΧΙ νέο sendMail με χειροκίνητο "RE:" prefix, θα
        // έχανε το threading στο Outlook του παραλήπτη.
        // ΝΕΟ 19/08, ζωντανό bug report χρήστη ("να μου βγάλει μια λίστα
        // δεδομένων και να την στείλει με email... δεν μπορεί να στείλει
        // ως συνημμένο") - attachmentContent/attachmentFilename
        // προαιρετικά. Ο Jarvis ΦΤΙΑΧΝΕΙ το περιεχόμενο ο ίδιος (π.χ. CSV
        // κείμενο από ένα προηγούμενο query_data αποτέλεσμα ΠΟΥ ΗΔΗ έχει
        // στο context) - ΔΕΝ διαβάζει/γράφει τοπικό αρχείο (ΞΕΧΩΡΙΣΤΟ από
        // το export_query_to_file, που φτιάχνει αρχείο στον δίσκο για τον
        // χειριστή, ΟΧΙ για attach). Graph fileAttachment θέλει
        // base64-encoded bytes - το κάνουμε ΕΔΩ, server-side, από το raw
        // κείμενο που έστειλε ο Jarvis.
        // ΔΙΟΡΘΩΘΗΚΕ 19/08 - ζωντανή διευκρίνιση χρήστη ("βήμα 1 φτιάχνει
        // το αρχείο, ξέρει το path - βήμα 2 το επισυνάπτει, βήμα 3 το
        // στέλνει"): νέο attachmentFilePath - ΠΡΑΓΜΑΤΙΚΟ αρχείο από τον
        // δίσκο (π.χ. το path που επέστρεψε το export_shown_table), ΟΧΙ
        // κείμενο. Διαβάζουμε τα ΠΡΑΓΜΑΤΙΚΑ bytes (File.ReadAllBytes,
        // ΟΧΙ UTF8-encode κειμένου όπως το attachmentContent - ένα PDF/
        // XLSX είναι binary format) και τα base64-κωδικοποιούμε
        // απευθείας. Αν δοθούν ΚΑΙ τα δύο, το attachmentFilePath
        // προηγείται (πραγματικό αρχείο > κείμενο).
        // ΝΕΟ 20/08, ρητό αίτημα χρήστη (task #55, "Email: πεδίο CC") -
        // κοινό helper, χρησιμοποιείται ΚΑΙ από SendEmailAsync ΚΑΙ από
        // ReplyEmailAsync. Δέχεται comma/semicolon-delimited λίστα
        // διευθύνσεων (ίδια σύμβαση με το "to" ενός κανονικού mail client -
        // ο χειριστής δεν χρειάζεται να ξέρει JSON). null/κενό -> null
        // (καμία στήλη ccRecipients στο payload, ΟΧΙ κενό array).
        private static JArray ParseCcRecipients(string cc)
        {
            if (string.IsNullOrWhiteSpace(cc)) return null;
            var addresses = cc.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(a => a.Trim())
                .Where(a => a.Length > 0)
                .ToArray();
            if (addresses.Length == 0) return null;
            var arr = new JArray();
            foreach (var addr in addresses)
                arr.Add(new JObject { ["emailAddress"] = new JObject { ["address"] = addr } });
            return arr;
        }

        public static async Task SendEmailAsync(
            XSupport xSupport, string to, string subject, string body,
            string attachmentContent = null, string attachmentFilename = null,
            string attachmentFilePath = null, string cc = null)
        {
            if (string.IsNullOrWhiteSpace(to)) throw new Exception("Λείπει ο παραλήπτης.");
            if (string.IsNullOrWhiteSpace(subject)) throw new Exception("Λείπει το θέμα.");

            string userEmail = GetCurrentUserEmail(xSupport);
            if (string.IsNullOrWhiteSpace(userEmail))
                throw new Exception("Δεν βρέθηκε email για τον τρέχοντα χειριστή (PRSN.EMAIL/USERS.EMAIL).");
            string token = await GetAccessTokenAsync(xSupport);

            var message = new JObject
            {
                ["subject"] = subject,
                ["body"] = new JObject { ["contentType"] = "Text", ["content"] = body ?? "" },
                ["toRecipients"] = new JArray
                {
                    new JObject { ["emailAddress"] = new JObject { ["address"] = to } }
                }
            };
            JArray ccRecipients = ParseCcRecipients(cc);
            if (ccRecipients != null) message["ccRecipients"] = ccRecipients;

            if (!string.IsNullOrWhiteSpace(attachmentFilePath))
            {
                if (!File.Exists(attachmentFilePath))
                    throw new Exception($"Το αρχείο δεν βρέθηκε: {attachmentFilePath}");
                string ext = Path.GetExtension(attachmentFilePath).ToLowerInvariant();
                string contentType = ext == ".pdf" ? "application/pdf"
                    : ext == ".csv" ? "text/csv"
                    : ext == ".xlsx" ? "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"
                    : "application/octet-stream";
                byte[] fileBytes = File.ReadAllBytes(attachmentFilePath);
                message["attachments"] = new JArray
                {
                    new JObject
                    {
                        ["@odata.type"] = "#microsoft.graph.fileAttachment",
                        ["name"] = Path.GetFileName(attachmentFilePath),
                        ["contentType"] = contentType,
                        ["contentBytes"] = Convert.ToBase64String(fileBytes)
                    }
                };
            }
            else if (!string.IsNullOrWhiteSpace(attachmentContent))
            {
                if (string.IsNullOrWhiteSpace(attachmentFilename))
                    throw new Exception("Δόθηκε attachmentContent χωρίς attachmentFilename.");
                string contentType = attachmentFilename.EndsWith(".csv", StringComparison.OrdinalIgnoreCase)
                    ? "text/csv" : "text/plain";
                string base64 = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(attachmentContent));
                message["attachments"] = new JArray
                {
                    new JObject
                    {
                        ["@odata.type"] = "#microsoft.graph.fileAttachment",
                        ["name"] = attachmentFilename,
                        ["contentType"] = contentType,
                        ["contentBytes"] = base64
                    }
                };
            }

            var payload = new JObject { ["message"] = message, ["saveToSentItems"] = true };

            string url = $"https://graph.microsoft.com/v1.0/users/{Uri.EscapeDataString(userEmail)}/sendMail";
            using (var req = new HttpRequestMessage(HttpMethod.Post, url))
            {
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                req.Content = new StringContent(payload.ToString(Formatting.None), System.Text.Encoding.UTF8, "application/json");
                using (var resp = await _http.SendAsync(req))
                {
                    if (!resp.IsSuccessStatusCode)
                    {
                        string errBody = await resp.Content.ReadAsStringAsync();
                        DebugLog.Log($"[email-send] Graph API error {(int)resp.StatusCode}: {errBody}");
                        throw new Exception($"Αποτυχία αποστολής email (Graph API {(int)resp.StatusCode}) - " +
                            "πιθανό θέμα δικαιωμάτων (χρειάζεται Application permission Mail.Send).");
                    }
                }
            }
        }

        public static async Task ReplyEmailAsync(XSupport xSupport, string messageId, string comment, string cc = null)
        {
            if (string.IsNullOrWhiteSpace(messageId)) throw new Exception("Λείπει το id του email.");
            if (string.IsNullOrWhiteSpace(comment)) throw new Exception("Λείπει το κείμενο της απάντησης.");

            string userEmail = GetCurrentUserEmail(xSupport);
            if (string.IsNullOrWhiteSpace(userEmail))
                throw new Exception("Δεν βρέθηκε email για τον τρέχοντα χειριστή (PRSN.EMAIL/USERS.EMAIL).");
            string token = await GetAccessTokenAsync(xSupport);

            var payload = new JObject { ["comment"] = comment };
            // ΝΕΟ 20/08 (task #55) - το Graph "reply" action δέχεται
            // προαιρετικό "message" override object για να προσθέσεις
            // επιπλέον ccRecipients στο draft ΠΡΙΝ σταλεί (χωρίς αυτό, το
            // reply κρατάει ΜΟΝΟ τον αρχικό αποστολέα/παραλήπτες).
            JArray ccRecipients = ParseCcRecipients(cc);
            if (ccRecipients != null)
                payload["message"] = new JObject { ["ccRecipients"] = ccRecipients };
            string url = $"https://graph.microsoft.com/v1.0/users/{Uri.EscapeDataString(userEmail)}" +
                $"/messages/{Uri.EscapeDataString(messageId)}/reply";
            using (var req = new HttpRequestMessage(HttpMethod.Post, url))
            {
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                req.Content = new StringContent(payload.ToString(Formatting.None), System.Text.Encoding.UTF8, "application/json");
                using (var resp = await _http.SendAsync(req))
                {
                    if (!resp.IsSuccessStatusCode)
                    {
                        string errBody = await resp.Content.ReadAsStringAsync();
                        DebugLog.Log($"[email-reply] Graph API error {(int)resp.StatusCode}: {errBody}");
                        throw new Exception($"Αποτυχία απάντησης email (Graph API {(int)resp.StatusCode}) - " +
                            "πιθανό θέμα δικαιωμάτων (χρειάζεται Application permission Mail.Send).");
                    }
                }
            }
        }

        // ── send_email (LLM tool) - χρησιμοποιεί το SendEmailAsync πιο
        // πάνω. ΑΝΕΠΙΣΤΡΕΠΤΗ ενέργεια (πραγματικό email σε πραγματικό
        // παραλήπτη) - βλ. system prompt (JarvisAgentClient.cs) για την
        // ΥΠΟΧΡΕΩΤΙΚΗ επιβεβαίωση σε ξεχωριστό turn πριν την κλήση.
        public static readonly object SendEmailToolDefinition = new
        {
            name = "send_email",
            description =
                "Στέλνει ΝΕΟ email από τον λογαριασμό του τρέχοντος χειριστή " +
                "(ίδιο mailbox με το read_email). ΑΝΕΠΙΣΤΡΕΠΤΗ ενέργεια - " +
                "χρησιμοποίησέ το ΜΟΝΟ αφού έδειξες το draft (προς/θέμα/" +
                "κείμενο/ΚΑΙ το συνημμένο αν υπάρχει) στον χειριστή ΚΑΙ " +
                "πήρες ρητή επιβεβαίωση σε ΕΠΟΜΕΝΟ μήνυμα. ΝΕΟ 19/08 - " +
                "υποστηρίζει ΣΥΝΗΜΜΕΝΟ αρχείο με ΔΥΟ τρόπους: (α) " +
                "attachmentFilePath - ΠΡΑΓΜΑΤΙΚΟ αρχείο (π.χ. PDF/Excel) " +
                "που ΗΔΗ υπάρχει στον δίσκο, το path είναι το αποτέλεσμα " +
                "ΕΝΟΣ ΠΡΟΗΓΟΥΜΕΝΟΥ export_shown_table στο ΙΔΙΟ turn " +
                "(διάβασε το πεδίο 'path' από το tool result εκείνου) - " +
                "χρησιμοποίησέ το ΟΤΑΝ ο χειριστής θέλει το ΙΔΙΟ PDF/" +
                "Excel που μόλις εξήχθη ΩΣ συνημμένο (ΟΧΙ ξανά-φτιαγμένο " +
                "κείμενο). (β) attachmentContent/attachmentFilename - ΕΣΥ " +
                "φτιάχνεις το περιεχόμενο ο ίδιος (π.χ. CSV: γραμμή " +
                "header + γραμμή ανά εγγραφή, κόμμα ως διαχωριστικό) - " +
                "ΧΩΡΙΣ κανένα άλλο tool πρώτα, χρήσιμο όταν ΔΕΝ υπάρχει " +
                "ήδη αρχείο στον δίσκο. Αν δοθούν ΚΑΙ τα δύο, προηγείται " +
                "το attachmentFilePath. ΞΕΧΩΡΙΣΤΟ και τα δύο από το " +
                "export_query_to_file (εκείνο φτιάχνει αρχείο στον δίσκο " +
                "ΤΟΥ ΧΕΙΡΙΣΤΗ, ΟΧΙ για attach σε email).",
            input_schema = new
            {
                type = "object",
                properties = new
                {
                    to = new { type = "string", description = "Email διεύθυνση παραλήπτη." },
                    subject = new { type = "string" },
                    body = new { type = "string", description = "Απλό κείμενο (ΟΧΙ HTML)." },
                    attachmentFilePath = new
                    {
                        type = "string",
                        description = "ΠΡΟΑΙΡΕΤΙΚΟ - πλήρες path ΗΔΗ υπάρχοντος αρχείου " +
                            "στον δίσκο (π.χ. το 'path' από export_shown_table). " +
                            "Προηγείται του attachmentContent αν δοθούν και τα δύο."
                    },
                    attachmentContent = new
                    {
                        type = "string",
                        description = "ΠΡΟΑΙΡΕΤΙΚΟ - αυτούσιο περιεχόμενο του συνημμένου " +
                            "αρχείου (π.χ. CSV κείμενο). Αν δοθεί, χρειάζεται ΚΑΙ " +
                            "attachmentFilename."
                    },
                    attachmentFilename = new
                    {
                        type = "string",
                        description = "ΠΡΟΑΙΡΕΤΙΚΟ - όνομα του συνημμένου αρχείου, π.χ. " +
                            "\"λίστα.csv\" (η κατάληξη .csv καθορίζει το content type)."
                    },
                    cc = new
                    {
                        type = "string",
                        description = "ΠΡΟΑΙΡΕΤΙΚΟ (task #55, ΝΕΟ 20/08) - κοινοποίηση, " +
                            "μία ή περισσότερες διευθύνσεις χωρισμένες με κόμμα. " +
                            "Βάλε το ΜΟΝΟ αν ο χειριστής το ζήτησε ρητά."
                    }
                },
                required = new[] { "to", "subject", "body" }
            }
        };

        public static async Task<string> ExecuteSendEmail(XSupport xSupport, JObject input)
        {
            string to = input?["to"]?.ToString();
            string subject = input?["subject"]?.ToString();
            string body = input?["body"]?.ToString();
            string attachmentContent = input?["attachmentContent"]?.ToString();
            string attachmentFilename = input?["attachmentFilename"]?.ToString();
            string attachmentFilePath = input?["attachmentFilePath"]?.ToString();
            string cc = input?["cc"]?.ToString();
            await SendEmailAsync(xSupport, to, subject, body, attachmentContent, attachmentFilename, attachmentFilePath, cc);
            bool hasAttachment = !string.IsNullOrWhiteSpace(attachmentFilePath) || !string.IsNullOrWhiteSpace(attachmentContent);
            return JsonConvert.SerializeObject(new { success = true, hasAttachment });
        }

        // ── reply_email (LLM tool) - χρησιμοποιεί το ReplyEmailAsync πιο
        // πάνω. Το messageId έρχεται από ΗΔΗ γνωστό email (π.χ.
        // αποτέλεσμα read_email) - ΙΔΙΟ σκεπτικό/ΙΔΙΑ επιβεβαίωση με το
        // send_email.
        public static readonly object ReplyEmailToolDefinition = new
        {
            name = "reply_email",
            description =
                "Απαντάει σε ΣΥΓΚΕΚΡΙΜΕΝΟ, ΗΔΗ γνωστό email (χρειάζεται το " +
                "'id' του - βρες το πρώτα με read_email αν δεν το έχεις " +
                "ήδη). Πραγματικό Graph reply (σωστό threading, ΟΧΙ νέο " +
                "email) - ΑΝΕΠΙΣΤΡΕΠΤΗ ενέργεια, χρησιμοποίησέ το ΜΟΝΟ αφού " +
                "έδειξες το draft ΚΑΙ πήρες ρητή επιβεβαίωση σε ΕΠΟΜΕΝΟ " +
                "μήνυμα.",
            input_schema = new
            {
                type = "object",
                properties = new
                {
                    messageId = new { type = "string", description = "Το 'id' του email που απαντάς (από read_email)." },
                    body = new { type = "string", description = "Κείμενο απάντησης, απλό κείμενο (ΟΧΙ HTML)." },
                    cc = new
                    {
                        type = "string",
                        description = "ΠΡΟΑΙΡΕΤΙΚΟ (task #55, ΝΕΟ 20/08) - κοινοποίηση, " +
                            "μία ή περισσότερες διευθύνσεις χωρισμένες με κόμμα. " +
                            "Βάλε το ΜΟΝΟ αν ο χειριστής το ζήτησε ρητά."
                    }
                },
                required = new[] { "messageId", "body" }
            }
        };

        public static async Task<string> ExecuteReplyEmail(XSupport xSupport, JObject input)
        {
            string messageId = input?["messageId"]?.ToString();
            string body = input?["body"]?.ToString();
            string cc = input?["cc"]?.ToString();
            await ReplyEmailAsync(xSupport, messageId, body, cc);
            return JsonConvert.SerializeObject(new { success = true });
        }

        // ── search_outlook_contacts (LLM tool) - ΝΕΟ 18/08, ρητό αίτημα
        // χρήστη ("ιδανικά αν υπάρχει η δυνατότητα να συνδυάσουμε και τις
        // επαφές από το Outlook ακόμα καλύτερα"). Graph
        // GET /users/{mailbox}/contacts - ΙΔΙΟ mailbox/auth idiom με τα
        // υπόλοιπα (client-credentials, GetAccessTokenAsync). $search
        // απαιτεί το header "ConsistencyLevel: eventual" (advanced query
        // capability, όπως και σε άλλα Graph resources - ΔΙΑΦΟΡΕΤΙΚΟ από
        // το read_email που κάνει ήδη $search χωρίς αυτό το header, αλλά
        // η τεκμηρίωση Graph το απαιτεί ρητά για /contacts).
        // ΧΡΕΙΑΖΕΤΑΙ ΕΠΙΠΛΕΟΝ Application permission "Contacts.Read" στο
        // ΙΔΙΟ App Registration (ΔΕΝ υπάρχει ακόμα, ίδιο μπλοκάρισμα με το
        // Mail.Send - βλ. README) - αποτυγχάνει graceful (error μήνυμα
        // που το Claude μπορεί να σχολιάσει ΧΩΡΙΣ να σκάσει όλη η
        // συζήτηση), ΙΔΙΟ idiom με το read_calendar/Calendars.Read.
        public static readonly object SearchOutlookContactsToolDefinition = new
        {
            name = "search_outlook_contacts",
            description =
                "Ψάχνει επαφές στο Outlook/Exchange (Contacts) του " +
                "τρέχοντος χειριστή - ΣΥΜΠΛΗΡΩΜΑΤΙΚΟ στο query_data (PRSN), " +
                "ΟΧΙ αντικαταστάτης. Χρησιμοποίησέ το ΜΑΖΙ με το PRSN " +
                "lookup όταν ο χειριστής ζητήσει να βρεις στοιχεία επαφής " +
                "(show_contact_results) - αν αποτύχει με σφάλμα " +
                "δικαιωμάτων, ΣΥΝΕΧΙΣΕ με ό,τι βρήκες στο PRSN, μην " +
                "σταματήσεις όλη τη ροή.",
            input_schema = new
            {
                type = "object",
                properties = new
                {
                    searchText = new { type = "string", description = "Όνομα/κομμάτι ονόματος για αναζήτηση (π.χ. 'Παπαδόπουλος')." }
                },
                required = new[] { "searchText" }
            }
        };

        public static async Task<string> ExecuteSearchOutlookContacts(XSupport xSupport, JObject input)
        {
            string searchText = input?["searchText"]?.ToString();
            if (string.IsNullOrWhiteSpace(searchText))
                return JsonConvert.SerializeObject(new { success = false, error = "Λείπει το κείμενο αναζήτησης." });

            string userEmail = GetCurrentUserEmail(xSupport);
            if (string.IsNullOrWhiteSpace(userEmail))
                return JsonConvert.SerializeObject(new { success = false, error = "Δεν βρέθηκε email για τον τρέχοντα χειριστή." });

            try
            {
                string token = await GetAccessTokenAsync(xSupport);
                string safeSearch = searchText.Replace("\"", "");
                string url = $"https://graph.microsoft.com/v1.0/users/{Uri.EscapeDataString(userEmail)}/contacts" +
                    $"?$search=\"{Uri.EscapeDataString(safeSearch)}\"" +
                    "&$select=displayName,emailAddresses,businessPhones,mobilePhone,jobTitle,companyName&$top=10";
                using (var req = new HttpRequestMessage(HttpMethod.Get, url))
                {
                    req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                    req.Headers.Add("ConsistencyLevel", "eventual");
                    using (var resp = await _http.SendAsync(req))
                    {
                        string body = await resp.Content.ReadAsStringAsync();
                        if (!resp.IsSuccessStatusCode)
                        {
                            DebugLog.Log($"[outlook-contacts] Graph API error {(int)resp.StatusCode}: {body}");
                            return JsonConvert.SerializeObject(new
                            {
                                success = false,
                                error = $"Αποτυχία αναζήτησης επαφών Outlook (Graph API {(int)resp.StatusCode}) - " +
                                    "πιθανό θέμα δικαιωμάτων (χρειάζεται Application permission Contacts.Read)."
                            });
                        }
                        JObject parsed = JObject.Parse(body);
                        var results = new JArray();
                        foreach (JObject c in (parsed["value"] as JArray ?? new JArray()).OfType<JObject>())
                        {
                            var emails = c["emailAddresses"] as JArray;
                            string email = emails != null && emails.Count > 0 ? (string)emails[0]["address"] : null;
                            var phones = c["businessPhones"] as JArray;
                            string phone = phones != null && phones.Count > 0 ? (string)phones[0] : null;
                            results.Add(new JObject
                            {
                                ["name"] = (string)c["displayName"],
                                ["email"] = email,
                                ["phone"] = phone,
                                ["mobile"] = (string)c["mobilePhone"],
                                ["title"] = string.Join(" - ", new[] { (string)c["jobTitle"], (string)c["companyName"] }
                                    .Where(s => !string.IsNullOrWhiteSpace(s)))
                            });
                        }
                        return JsonConvert.SerializeObject(new { success = true, contacts = results });
                    }
                }
            }
            catch (Exception ex)
            {
                DebugLog.Log("[outlook-contacts] EXCEPTION: " + ex);
                return JsonConvert.SerializeObject(new { success = false, error = ex.Message });
            }
        }

        // ── Inbox (Email tab) - ΝΕΟ 17/08, ρητό αίτημα χρήστη (βλ. README
        // Roadmap #1, "Email curtain" - Email tab). ΑΔΙΑΒΑΣΤΑ emails από ένα
        // εύρος ημερομηνιών (default τελευταία εβδομάδα, "date-7") -
        // deterministic UI fetch (date picker + "Ανανέωση"), ΞΕΧΩΡΙΣΤΟ από
        // το read_email tool (chat - "πιο σύνθετα πράγματα", ρητή διάκριση
        // χρήστη 17/08). $filter (ΟΧΙ $search) - διαφορετικός μηχανισμός
        // από το ExecuteReadEmail, συνδυάζεται κανονικά με $orderby (το
        // documented $search+$orderby conflict ΕΚΕΙ δεν ισχύει εδώ).
        // searchText - ΝΕΟ 17/08, ρητό αίτημα χρήστη ("συνθέτει φίλτρο, " +
        // "δηλαδή ημερομηνία και κάτι ακόμα;") - προαιρετικό, φιλτράρει
        // ΜΕΤΑ το Graph fetch (client-side στο C#, ΟΧΙ $search στο Graph -
        // το $search ΔΕΝ συνδυάζεται αξιόπιστα με $filter/$orderby, βλ.
        // ήδη τεκμηριωμένο conflict στο ExecuteReadEmail). Ταιριάζει σε
        // subject/fromName/from (contains, case-insensitive) - φθηνό
        // (already-fetched, μικρό σύνολο ≤maxEmails), απόλυτα αξιόπιστο.
        public static async Task<JArray> GetInboxEmailsAsync(XSupport xSupport, DateTime sinceDate, string searchText = null)
        {
            string userEmail = GetCurrentUserEmail(xSupport);
            if (string.IsNullOrWhiteSpace(userEmail))
                throw new Exception("Δεν βρέθηκε email για τον τρέχοντα χειριστή (PRSN.EMAIL/USERS.EMAIL).");

            string token = await GetAccessTokenAsync(xSupport);

            // receivedDateTime filter σε UTC (Graph σύμβαση) - "Z" literal
            // suffix, ΧΩΡΙΣ πραγματική μετατροπή ζώνης ώρας (χοντρικό "τις
            // τελευταίες Ν ημέρες" φίλτρο, δεν χρειάζεται λεπτομέρεια ώρας).
            // ΑΛΛΑΓΗ 20/08, ρητό αίτημα χρήστη - πριν ήταν "isRead eq false
            // and ..." (ΜΟΝΟ αδιάβαστα, hardcoded, καμία επιλογή). Τώρα
            // φέρνει ΟΛΑ (διαβασμένα+αδιάβαστα) - το isRead προωθείται πλέον
            // στο JSON (βλ. πιο κάτω) και το index.html τα ξεχωρίζει
            // ΟΠΤΙΚΑ (έντονα/dot για αδιάβαστα, ίδιο idiom με Outlook) αντί
            // να αποκλείει εντελώς τα διαβασμένα.
            string filter = $"receivedDateTime ge {sinceDate:yyyy-MM-ddTHH:mm:ss}Z";
            // ΝΕΟ 17/08, ρητό αίτημα χρήστη - "flag" (θαυμαστικό αν
            // σημαιωμένο) + "$expand=attachments" (ΟΝΟΜΑΤΑ συνημμένων ΣΤΗ
            // λίστα - ΟΧΙ contentBytes, γι' αυτό ρητό $select ΜΕΣΑ στο
            // expand, αλλιώς θα τραβούσε ΚΑΙ το (βαρύ) περιεχόμενο κάθε
            // συνημμένου για ΚΑΘΕ email της λίστας).
            // ΝΕΟ 17/08, ρητό αίτημα χρήστη - $top ΠΑΡΑΜΕΤΡΙΚΟ (ParamCode
            // 500022, "Jarvis - Email Inbox Max Emails"), default 100 αν
            // λείπει η παράμετρος ("αν δεν υπάρχει τότε top 100").
            int maxEmails = JarvisTools.GetCrmTaskOptionalParam(xSupport, 500022, 100);
            string url = $"https://graph.microsoft.com/v1.0/users/{Uri.EscapeDataString(userEmail)}/messages" +
                $"?$filter={Uri.EscapeDataString(filter)}" +
                "&$select=id,subject,from,receivedDateTime,isRead,webLink,hasAttachments,flag" +
                "&$expand=" + Uri.EscapeDataString("attachments($select=id,name,contentType,size)") +
                $"&$orderby=receivedDateTime desc&$top={maxEmails}";

            using (var req = new HttpRequestMessage(HttpMethod.Get, url))
            {
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                using (var resp = await _http.SendAsync(req))
                {
                    string body = await resp.Content.ReadAsStringAsync();
                    if (!resp.IsSuccessStatusCode)
                    {
                        DebugLog.Log($"[email-inbox] Graph API error {(int)resp.StatusCode}: {body}");
                        throw new Exception($"Αποτυχία ανάγνωσης email (Graph API {(int)resp.StatusCode}) - " +
                            "πιθανό θέμα δικαιωμάτων/ρύθμισης.");
                    }

                    JObject parsed = JObject.Parse(body);
                    var items = new JArray();
                    foreach (var m in parsed["value"] as JArray ?? new JArray())
                    {
                        // "flagged" = ενεργή σημαία (θαυμαστικό) - "complete"
                        // (ήδη διεκπεραιωμένη σημαία) ΚΑΙ "notFlagged" ΔΕΝ
                        // δείχνουν θαυμαστικό, ίδια σύμβαση με το οπτικό
                        // (κόκκινο) flag icon του ίδιου του Outlook.
                        string flagStatus = m["flag"]?["flagStatus"]?.ToString();
                        var attachments = new JArray();
                        foreach (var a in m["attachments"] as JArray ?? new JArray())
                        {
                            attachments.Add(new JObject
                            {
                                ["name"] = a["name"],
                                ["size"] = a["size"]
                            });
                        }
                        items.Add(new JObject
                        {
                            ["id"] = m["id"],
                            ["subject"] = m["subject"],
                            ["from"] = m["from"]?["emailAddress"]?["address"],
                            ["fromName"] = m["from"]?["emailAddress"]?["name"],
                            ["receivedDateTime"] = m["receivedDateTime"],
                            ["webLink"] = m["webLink"],
                            ["hasAttachments"] = m["hasAttachments"],
                            ["isFlagged"] = flagStatus == "flagged",
                            // ΝΕΟ 20/08, ρητό αίτημα χρήστη - πριν διαβαζόταν
                            // από το Graph αλλά ΔΕΝ προωθούνταν στο JS (το
                            // $filter το χρειαζόταν μόνο server-side). Τώρα
                            // που το $filter δεν αποκλείει πια τα διαβασμένα,
                            // το index.html το χρειάζεται για την οπτική
                            // διάκριση.
                            ["isRead"] = m["isRead"],
                            ["attachments"] = attachments
                        });
                    }
                    if (!string.IsNullOrWhiteSpace(searchText))
                    {
                        var filtered = new JArray();
                        foreach (var it in items)
                        {
                            string subj = it["subject"]?.ToString() ?? "";
                            string fromN = it["fromName"]?.ToString() ?? "";
                            string fromA = it["from"]?.ToString() ?? "";
                            if (subj.IndexOf(searchText, StringComparison.OrdinalIgnoreCase) >= 0 ||
                                fromN.IndexOf(searchText, StringComparison.OrdinalIgnoreCase) >= 0 ||
                                fromA.IndexOf(searchText, StringComparison.OrdinalIgnoreCase) >= 0)
                                filtered.Add(it);
                        }
                        return filtered;
                    }
                    return items;
                }
            }
        }

        // Πλήρες περιεχόμενο ΕΝΟΣ email (θέμα/αποστολέας/παραλήπτες/σώμα/
        // συνημμένα) - ΝΕΟ 17/08, ρητό αίτημα χρήστη: double-click σε
        // εγγραφή του Email tab -> Modal "σαν να είναι Outlook". Ξεχωριστό
        // request ΑΝΑ email (ΟΧΙ μαζί με τη λίστα - το σώμα/HTML μπορεί να
        // είναι βαρύ, δεν έχει νόημα να το τραβάμε για ΟΛΑ τα emails της
        // λίστας όταν ο χειριστής θα ανοίξει ίσως ένα-δύο).
        public static async Task<JObject> GetEmailDetailAsync(XSupport xSupport, string messageId)
        {
            string userEmail = GetCurrentUserEmail(xSupport);
            if (string.IsNullOrWhiteSpace(userEmail))
                throw new Exception("Δεν βρέθηκε email για τον τρέχοντα χειριστή (PRSN.EMAIL/USERS.EMAIL).");

            string token = await GetAccessTokenAsync(xSupport);

            string url = $"https://graph.microsoft.com/v1.0/users/{Uri.EscapeDataString(userEmail)}" +
                $"/messages/{Uri.EscapeDataString(messageId)}" +
                "?$select=" + Uri.EscapeDataString("subject,from,toRecipients,ccRecipients,receivedDateTime,body,webLink,flag") +
                "&$expand=" + Uri.EscapeDataString("attachments($select=id,name,contentType,size)");

            using (var req = new HttpRequestMessage(HttpMethod.Get, url))
            {
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                using (var resp = await _http.SendAsync(req))
                {
                    string body = await resp.Content.ReadAsStringAsync();
                    if (!resp.IsSuccessStatusCode)
                    {
                        DebugLog.Log($"[email-detail] Graph API error {(int)resp.StatusCode}: {body}");
                        throw new Exception($"Αποτυχία ανάγνωσης email (Graph API {(int)resp.StatusCode}).");
                    }

                    JObject m = JObject.Parse(body);
                    var attachments = new JArray();
                    foreach (var a in m["attachments"] as JArray ?? new JArray())
                    {
                        attachments.Add(new JObject { ["name"] = a["name"], ["size"] = a["size"] });
                    }
                    Func<JToken, JArray> recipientNames = recipients =>
                    {
                        var names = new JArray();
                        foreach (var r in recipients as JArray ?? new JArray())
                        {
                            string name = r["emailAddress"]?["name"]?.ToString();
                            string addr = r["emailAddress"]?["address"]?.ToString();
                            names.Add(string.IsNullOrWhiteSpace(name) ? addr : $"{name} <{addr}>");
                        }
                        return names;
                    };

                    return new JObject
                    {
                        ["id"] = messageId,
                        ["subject"] = m["subject"],
                        ["from"] = m["from"]?["emailAddress"]?["address"],
                        ["fromName"] = m["from"]?["emailAddress"]?["name"],
                        ["toRecipients"] = recipientNames(m["toRecipients"]),
                        ["ccRecipients"] = recipientNames(m["ccRecipients"]),
                        ["receivedDateTime"] = m["receivedDateTime"],
                        ["bodyContentType"] = m["body"]?["contentType"],
                        ["bodyContent"] = m["body"]?["content"],
                        ["webLink"] = m["webLink"],
                        ["isFlagged"] = m["flag"]?["flagStatus"]?.ToString() == "flagged",
                        ["attachments"] = attachments
                    };
                }
            }
        }

        // ── Chat-driven filter control (Email curtain) - ΝΕΟ 17/08, ρητό
        // αίτημα χρήστη: "η απόκριση έγινε ως απάντηση μέσα στο chat box -
        // οι πληροφορίες για φιλτράρισμα θέλω να γίνονται στο main
        // παράθυρο... στο chat box θέλω να μένει ΜΟΝΟ chat". ΑΥΤΑ ΤΑ 2
        // tools ΔΕΝ φέρνουν τα ίδια τα δεδομένα (καμία κλήση Graph API εδώ,
        // ΔΕΝ χρειάζονται καν xSupport) - απλά ενεργοποιούν το callback
        // (onFilterEmailInbox/onFilterCalendar, βλ. JarvisAgentClient.
        // AskAsync/JarvisShell.HandleEmailMessageAsync) που στέλνει
        // postMessage στο index.html - ΕΚΕΙΝΟ ενημερώνει το date filter ΚΑΙ
        // ξανακαλεί το ΙΔΙΟ deterministic fetch (email_get_inbox/
        // email_get_calendar) που ήδη χρησιμοποιεί το toolbar - ΜΙΑ πηγή
        // αλήθειας για το πώς φορτώνει η λίστα, το chat απλά την
        // "τηλεχειρίζεται", ΔΕΝ την ξαναφτιάχνει σαν κείμενο.
        public static readonly object FilterEmailInboxToolDefinition = new
        {
            name = "filter_email_inbox",
            description =
                "Αλλάζει το φίλτρο (ημερομηνία \"Από\" + προαιρετικά " +
                "αποστολέα/θέμα) της λίστας email στο Email tab ΚΑΙ την " +
                "ανανεώνει ΑΠΕΥΘΕΙΑΣ στο κύριο παράθυρο - χρησιμοποίησέ το " +
                "ΠΑΝΤΑ όταν ο χειριστής ζητήσει να δει/φιλτράρει τα email " +
                "του (π.χ. \"δείξε μου τα email του τελευταίου μήνα\", " +
                "\"φέρε τα των τελευταίων 2 εβδομάδων από τον Χ\", \"...που " +
                "αφορούν τιμοκατάλογο\") - το searchText ΣΥΝΘΕΤΕΙ με το " +
                "sinceDate (ΚΑΙ τα δύο μαζί, ΟΧΙ εναλλακτικά). ΜΗΝ " +
                "απαντήσεις ΜΕ ΛΙΣΤΑ email μέσα στο chat - ο χειριστής " +
                "βλέπει ήδη την ενημερωμένη λίστα στο κύριο παράθυρο, απλά " +
                "επιβεβαίωσε ΣΥΝΤΟΜΑ (π.χ. \"Έτοιμο, δείχνω τα email από " +
                "τις... που ταιριάζουν στο 'Χ'\"). Αν χρειάστηκε ΚΑΙ " +
                "query_data/read_email για κάτι που το φίλτρο ΔΕΝ μπορεί " +
                "να εκφράσει (μέτρημα/ομαδοποίηση/ανάλυση), βάλε ΕΚΕΙΝΟ το " +
                "εύρημα στο insight - ΘΑ ΕΜΦΑΝΙΣΤΕΙ ΚΙ ΑΥΤΟ στο κύριο " +
                "παράθυρο, ΟΧΙ στο chat. Δουλεύει ΚΑΙ μέσα από το κύριο " +
                "chat (ΟΧΙ μόνο μέσα στην κουρτίνα Email) - αν η κουρτίνα " +
                "δεν είναι ήδη ανοιχτή, ανοίγει αυτόματα ώστε ο χειριστής " +
                "να δει αμέσως το αποτέλεσμα.",
            input_schema = new
            {
                type = "object",
                properties = new
                {
                    sinceDate = new
                    {
                        type = "string",
                        description = "Νέα ημερομηνία \"Από\" (ISO date, π.χ. '2026-08-01')."
                    },
                    searchText = new
                    {
                        type = "string",
                        description =
                            "Προαιρετικό - φιλτράρει ΕΠΙΠΛΕΟΝ (ΜΑΖΙ με το " +
                            "sinceDate) με βάση κείμενο σε θέμα/αποστολέα " +
                            "(π.χ. όνομα/email αποστολέα, λέξη-κλειδί στο " +
                            "θέμα). Αν ο χειριστής δεν ζήτησε τέτοιο " +
                            "φίλτρο, ΜΗΝ το βάλεις."
                    },
                    insight = new
                    {
                        type = "string",
                        description =
                            "Προαιρετικό - ΜΟΝΟ όταν το αίτημα είχε ΚΑΙ " +
                            "αναλυτικό κομμάτι (μέτρημα/ομαδοποίηση/σύγκριση) " +
                            "που χρειάστηκε ΞΕΧΩΡΙΣΤΟ query_data/read_email " +
                            "πέρα από το απλό φίλτρο - ΣΥΝΤΟΜΟ κείμενο του " +
                            "ευρήματος (π.χ. \"3 από 1208 έχουν μοναδικό " +
                            "θέμα\"). ΘΑ ΕΜΦΑΝΙΣΤΕΙ σε κάρτα ΠΑΝΩ από τη " +
                            "λίστα στο κύριο παράθυρο - ΜΗΝ το ξαναγράψεις " +
                            "ΚΑΙ στο chat reply σου."
                    },
                    // ΝΕΟ 20/08, ρητό αίτημα χρήστη - "είναι πολύ στατικό αυτό...
                    // πρέπει να μπορεί ο χειριστής να έχει και κάποιο δυναμικό
                    // δικαίωμα" (αντικαθιστά το προηγούμενο, στενό "unreadOnly"
                    // boolean). ΓΕΝΙΚΟΣ μηχανισμός - πεδίο/τελεστής/τιμή, ώστε
                    // ΟΠΟΙΟΣΔΗΠΟΤΕ συνδυασμός κριτηρίων (κατάσταση ανάγνωσης,
                    // αποστολέας, εύρος ημερομηνιών, σημαία, συνημμένα, ...) να
                    // εκφράζεται ΧΩΡΙΣ να χρειάζεται νέο named param στο μέλλον.
                    // Client-side φιλτράρισμα στο index.html πάνω στα ΗΔΗ
                    // φερμένα emails (μικρό dataset, βλ. ParamCode 500022 cap).
                    filters = new
                    {
                        type = "array",
                        description =
                            "Προαιρετικό - λίστα κριτηρίων, ΟΛΑ πρέπει να " +
                            "ισχύουν μαζί (AND). Χρησιμοποίησέ το για ΟΤΙΔΗΠΟΤΕ " +
                            "δεν εκφράζεται από sinceDate/searchText - π.χ. " +
                            "\"μόνο αναγνωσμένα\", \"μόνο σημαιωμένα\", \"με " +
                            "συνημμένα\", \"μέχρι τις 15/8\" (εύρος - ΜΑΖΙ με " +
                            "sinceDate ως αρχή). Παράλειψέ το αν δεν χρειάζεται " +
                            "κανένα επιπλέον κριτήριο.",
                        items = new
                        {
                            type = "object",
                            properties = new
                            {
                                field = new
                                {
                                    type = "string",
                                    @enum = new[] { "isRead", "isFlagged", "hasAttachments", "from", "subject", "receivedDateTime" },
                                    description =
                                        "isRead/isFlagged/hasAttachments = boolean πεδία. " +
                                        "from = αποστολέας (όνομα Ή διεύθυνση). " +
                                        "subject = θέμα. receivedDateTime = " +
                                        "ημερομηνία παραλαβής (ISO date, χρησιμοποίησέ " +
                                        "το ΜΟΝΟ για το 'μέχρι' - το 'από' είναι ήδη " +
                                        "το sinceDate)."
                                },
                                op = new
                                {
                                    type = "string",
                                    @enum = new[] { "eq", "contains", "gte", "lte" },
                                    description =
                                        "eq = ίσο (boolean πεδία: value 'true'/'false'). " +
                                        "contains = περιέχει (from/subject, case-insensitive). " +
                                        "gte/lte = >=/<= (μόνο receivedDateTime)."
                                },
                                value = new
                                {
                                    type = "string",
                                    description = "Η τιμή σύγκρισης, ΠΑΝΤΑ σαν string (π.χ. 'true', 'Μυλωνάς', '2026-08-15')."
                                }
                            },
                            required = new[] { "field", "op", "value" }
                        }
                    }
                },
                required = new[] { "sinceDate" }
            }
        };

        public static string ExecuteFilterEmailInbox(JObject input, Action<string, string, string, JArray> onFilterEmailInbox)
        {
            string sinceDate = input?["sinceDate"]?.ToString();
            if (string.IsNullOrWhiteSpace(sinceDate))
                throw new Exception("Λείπει το sinceDate.");
            if (!DateTime.TryParse(sinceDate, out DateTime parsed))
                throw new Exception($"Μη έγκυρη ημερομηνία: {sinceDate}");
            string searchText = input?["searchText"]?.ToString();
            string insight = input?["insight"]?.ToString();
            JArray filters = input?["filters"] as JArray;
            onFilterEmailInbox?.Invoke(parsed.ToString("yyyy-MM-dd"), searchText, insight, filters);
            return JsonConvert.SerializeObject(new { success = true });
        }

        // ΣΗΜΕΙΩΣΗ 17/08: υπήρξε εδώ ΚΑΙ "hideRepeatedSubjects" - ΑΦΑΙΡΕΘΗΚΕ,
        // ρητό αίτημα χρήστη ("άχρηστο checkbox") - βλ. ShowCalendarEntriesToolDefinition
        // πιο κάτω για τη γενικότερη λύση που το αντικατέστησε.
        public static readonly object FilterCalendarToolDefinition = new
        {
            name = "filter_calendar",
            description =
                "Αλλάζει την ημερομηνία (+ προαιρετικά λέξη-κλειδί θέματος) " +
                "του Calendar tab ΚΑΙ το ανανεώνει ΑΠΕΥΘΕΙΑΣ στο κύριο " +
                "παράθυρο - χρησιμοποίησέ το όταν ο χειριστής ζητήσει να " +
                "δει το ημερολόγιο/τις εργασίες μιας ΣΥΓΚΕΚΡΙΜΕΝΗΣ ημέρας " +
                "(π.χ. \"δείξε μου αύριο\", \"τι έχω την Παρασκευή που " +
                "αφορά τον πελάτη Χ\") - το searchText ΣΥΝΘΕΤΕΙ με το date " +
                "(ΚΑΙ τα δύο μαζί, ΟΧΙ εναλλακτικά). ΜΗΝ απαντήσεις ΜΕ " +
                "ΛΙΣΤΑ events/εργασιών μέσα στο chat - ο χειριστής βλέπει " +
                "ήδη το ενημερωμένο ημερολόγιο στο κύριο παράθυρο. Για " +
                "ΠΙΟ ΣΥΝΘΕΤΟ φιλτράρισμα που το searchText ΔΕΝ μπορεί να " +
                "εκφράσει (π.χ. εξαίρεση pattern με μεταβλητό περιεχόμενο), " +
                "χρησιμοποίησε το show_calendar_entries (πιο κάτω) ΑΝΤΙ " +
                "γι' αυτό. Αν χρειάστηκε ΚΑΙ query_data/read_calendar για " +
                "κάτι ΚΑΘΑΡΑ στατιστικό/συγκριτικό (ΟΧΙ λίστα εγγραφών), " +
                "βάλε ΕΚΕΙΝΟ το εύρημα στο insight - ΘΑ ΕΜΦΑΝΙΣΤΕΙ ΚΙ ΑΥΤΟ " +
                "στο κύριο παράθυρο, ΟΧΙ στο chat.",
            input_schema = new
            {
                type = "object",
                properties = new
                {
                    date = new
                    {
                        type = "string",
                        description = "Η νέα ημερομηνία (ISO date, π.χ. '2026-08-19')."
                    },
                    searchText = new
                    {
                        type = "string",
                        description =
                            "Προαιρετικό - φιλτράρει ΕΠΙΠΛΕΟΝ (ΜΑΖΙ με το " +
                            "date) με βάση λέξη-κλειδί στο θέμα " +
                            "εργασίας/ραντεβού. Αν ο χειριστής δεν ζήτησε " +
                            "τέτοιο φίλτρο, ΜΗΝ το βάλεις."
                    },
                    insight = new
                    {
                        type = "string",
                        description =
                            "Προαιρετικό - ΜΟΝΟ όταν το αίτημα είχε ΚΑΙ " +
                            "αναλυτικό κομμάτι (μέτρημα/ομαδοποίηση/σύγκριση) " +
                            "που χρειάστηκε ΞΕΧΩΡΙΣΤΟ query_data/read_calendar " +
                            "πέρα από το απλό φίλτρο - ΣΥΝΤΟΜΟ κείμενο του " +
                            "ευρήματος. ΘΑ ΕΜΦΑΝΙΣΤΕΙ σε κάρτα ΠΑΝΩ από τη " +
                            "λίστα στο κύριο παράθυρο - ΜΗΝ το ξαναγράψεις " +
                            "ΚΑΙ στο chat reply σου."
                    }
                },
                required = new[] { "date" }
            }
        };

        public static string ExecuteFilterCalendar(JObject input, Action<string, string, string> onFilterCalendar)
        {
            string date = input?["date"]?.ToString();
            if (string.IsNullOrWhiteSpace(date))
                throw new Exception("Λείπει το date.");
            if (!DateTime.TryParse(date, out DateTime parsed))
                throw new Exception($"Μη έγκυρη ημερομηνία: {date}");
            string searchText = input?["searchText"]?.ToString();
            string insight = input?["insight"]?.ToString();
            onFilterCalendar?.Invoke(parsed.ToString("yyyy-MM-dd"), searchText, insight);
            return JsonConvert.SerializeObject(new { success = true });
        }

        // show_calendar_entries - ΝΕΟ 17/08, 4ο ζωντανό αίτημα χρήστη
        // (ρητή αλλαγή κατεύθυνσης: "δεν το αποκλείει γιατί το θέμα έχει
        // διαφορετική ώρα... θέλουμε να εξαιρεί ΑΥΤΟΣ [ο Claude] με τις
        // οδηγίες που παίρνει, γιατί το κατάφερε στις προηγούμενες
        // δοκιμές - το μόνο πρόβλημα είναι να είναι εντός του Main
        // παραθύρου"). Root cause του hideRepeatedSubjects bug: το
        // GROUP BY COMMENTS HAVING COUNT(*)=1 υποθέτει byte-ίδιο κείμενο -
        // ΔΕΝ δουλεύει όταν το "επαναλαμβανόμενο" θέμα έχει μεταβλητό
        // περιεχόμενο μέσα του (π.χ. ώρα στον τίτλο) - κάθε γραμμή γίνεται
        // "τεχνικά μοναδική" string, το φίλτρο δεν πιάνει τίποτα. Ο Claude
        // (μέσω query_data, ελεύθερο SQL/λογική) ΗΔΗ το έλυνε σωστά -
        // ΑΝΤΙ να προσπαθούμε να προβλέψουμε ΚΑΘΕ πιθανό pattern με νέα
        // hardcoded SQL params (whack-a-mole), αυτό το tool αφήνει τον
        // Claude να υπολογίσει ΟΠΟΙΑΔΗΠΟΤΕ λογική χρειάζεται μέσω
        // query_data, και απλά ΜΕΤΑΦΕΡΕΙ το ΗΔΗ-υπολογισμένο αποτέλεσμα
        // ΑΠΕΥΘΕΙΑΣ στη λίστα του Calendar tab (index.html
        // renderEmailCalendarList ΑΥΤΟΥΣΙΟ, ΧΩΡΙΣ δικό του νέο fetch/re-
        // filter) - ΓΕΝΙΚΕΥΜΕΝΗ λύση, ΟΧΙ ειδική περίπτωση.
        public static readonly object ShowCalendarEntriesToolDefinition = new
        {
            name = "show_calendar_entries",
            description =
                "Εμφανίζει ΣΥΓΚΕΚΡΙΜΕΝΕΣ SOACTION εγγραφές (που ΗΔΗ βρήκες " +
                "μέσω query_data) ΑΠΕΥΘΕΙΑΣ στη λίστα του Calendar tab, στο " +
                "ΚΥΡΙΟ παράθυρο - ΧΡΗΣΙΜΟΠΟΙΗΣΕ ΤΟ όταν το φιλτράρισμα που " +
                "χρειάζεται ο χειριστής είναι πιο σύνθετο απ' όσο μπορεί " +
                "να εκφράσει το searchText του filter_calendar (π.χ. " +
                "\"εργασίες με μοναδικό/μη επαναλαμβανόμενο θέμα\", " +
                "εξαίρεση ενός pattern θέματος που έχει ΜΕΤΑΒΛΗΤΟ " +
                "περιεχόμενο μέσα του όπως ώρα/ημερομηνία στον ίδιο τον " +
                "τίτλο - ένα απλό LIKE ΔΕΝ πιάνει τέτοιες περιπτώσεις). " +
                "ΡΟΗ: (1) query_data ΠΡΩΤΑ για να " +
                "βρεις ΑΚΡΙΒΩΣ ποιες SOACTION γραμμές θέλεις να δείξεις " +
                "(SELECT SOACTION, COMMENTS, FROMDATE, ACTSTATUS από " +
                "SOACTION, με ΟΠΟΙΑΔΗΠΟΤΕ λογική/pattern-matching " +
                "χρειάζεται - LIKE, SUBSTRING, κ.λπ.), (2) κάλεσε ΑΥΤΟ το " +
                "tool με ΤΑ ΑΠΟΤΕΛΕΣΜΑΤΑ σε entries. ΜΗΝ απαντήσεις ΜΕ " +
                "ΛΙΣΤΑ μέσα στο chat - ο χειριστής βλέπει το αποτέλεσμα " +
                "ΑΠΕΥΘΕΙΑΣ στο κύριο παράθυρο, απλά επιβεβαίωσε ΣΥΝΤΟΜΑ.",
            input_schema = new
            {
                type = "object",
                properties = new
                {
                    date = new
                    {
                        type = "string",
                        description = "Η ημερομηνία που αφορούν οι εγγραφές (ISO date, π.χ. '2026-08-17') - ενημερώνει το date picker του Calendar tab."
                    },
                    entries = new
                    {
                        type = "array",
                        description = "Οι SOACTION εγγραφές που θα εμφανιστούν - ΑΚΡΙΒΩΣ αυτές, καμία επιπλέον επεξεργασία/φιλτράρισμα από το backend.",
                        items = new
                        {
                            type = "object",
                            properties = new
                            {
                                soactionId = new { type = "integer", description = "SOACTION.SOACTION (id)." },
                                subject = new { type = "string", description = "Θέμα (SOACTION.COMMENTS)." },
                                start = new { type = "string", description = "Ώρα/ημερομηνία έναρξης (π.χ. '2026-08-17T09:12')." },
                                end = new { type = "string", description = "Προαιρετικό - ώρα λήξης, αν την ξέρεις (αλλιώς παραλείπεται)." },
                                statusLabel = new { type = "string", description = "Προαιρετικό - ελληνική περιγραφή κατάστασης (π.χ. 'Ολοκληρώθηκε')." }
                            },
                            required = new[] { "soactionId", "subject", "start" }
                        }
                    }
                },
                required = new[] { "date", "entries" }
            }
        };

        public static string ExecuteShowCalendarEntries(JObject input, Action<string, JArray> onShowCalendarEntries)
        {
            string date = input?["date"]?.ToString();
            if (string.IsNullOrWhiteSpace(date))
                throw new Exception("Λείπει το date.");
            if (!DateTime.TryParse(date, out DateTime parsed))
                throw new Exception($"Μη έγκυρη ημερομηνία: {date}");

            var rawEntries = input?["entries"] as JArray ?? new JArray();
            var entries = new JArray();
            foreach (var e in rawEntries)
            {
                entries.Add(new JObject
                {
                    ["source"] = "soft1",
                    ["soactionId"] = e["soactionId"],
                    ["soredir"] = e["soredir"] ?? 3, // default "Task" αν δεν δόθηκε
                    ["typeLabel"] = e["typeLabel"] ?? "Task",
                    ["subject"] = e["subject"],
                    ["start"] = e["start"],
                    ["end"] = e["end"] ?? e["start"],
                    ["statusLabel"] = e["statusLabel"]
                });
            }

            onShowCalendarEntries?.Invoke(parsed.ToString("yyyy-MM-dd"), entries);
            return JsonConvert.SerializeObject(new { success = true, count = entries.Count });
        }

        // ── Calendar (Outlook) - ΝΕΟ 17/08, ρητό αίτημα χρήστη (βλ. README
        // Roadmap #1, "Email curtain" - Calendar tab). Χρειάζεται ΝΕΟ
        // `Calendars.Read` Application permission (ο χρήστης το πρόσθεσε
        // ήδη 17/08 στο ΙΔΙΟ Azure AD App Registration/Application Access
        // Policy με το Mail.Read - βλ. header σχόλιο πιο πάνω). ─────────────

        // calendarView (ΟΧΙ /events) - επεκτείνει αυτόματα recurring events
        // μέσα στο εύρος [start,end), το απλό /events θα έδειχνε μόνο ΜΙΑ
        // εγγραφή σειράς αντί για κάθε επανάληψη. Prefer: outlook.timezone
        // -> οι ώρες γυρνάνε ΗΔΗ σε τοπική ώρα Ελλάδας (ΟΧΙ UTC), ώστε να
        // συγκρίνονται ΑΠΕΥΘΕΙΑΣ με το SOACTION.FROMDATE (τοπική ώρα, χωρίς
        // offset) στο merge (βλ. JarvisShell.HandleEmailGetCalendarAsync).
        // Reusable - καλείται ΚΑΙ από το read_calendar tool (chat, πιο κάτω)
        // ΚΑΙ από το deterministic Calendar tab merge.
        // searchText - ΝΕΟ 17/08, ρητό αίτημα χρήστη, ίδιο idiom με το
        // GetInboxEmailsAsync (client-side μετά το fetch, ΟΧΙ Graph $search).
        public static async Task<JArray> GetCalendarEventsAsync(XSupport xSupport, DateTime start, DateTime end, string searchText = null)
        {
            string userEmail = GetCurrentUserEmail(xSupport);
            if (string.IsNullOrWhiteSpace(userEmail))
                throw new Exception("Δεν βρέθηκε email για τον τρέχοντα χειριστή (PRSN.EMAIL/USERS.EMAIL).");

            string token = await GetAccessTokenAsync(xSupport);

            // ΝΕΟ 17/08, ρητό αίτημα χρήστη - $top ΠΑΡΑΜΕΤΡΙΚΟ (ParamCode
            // 500023, "Jarvis - Calendar Outlook Max Events"), default 100
            // αν λείπει η παράμετρος (ίδιο idiom με το 500022 email inbox).
            int maxEvents = JarvisTools.GetCrmTaskOptionalParam(xSupport, 500023, 100);
            string url = $"https://graph.microsoft.com/v1.0/users/{Uri.EscapeDataString(userEmail)}/calendarView" +
                $"?startDateTime={Uri.EscapeDataString(start.ToString("yyyy-MM-ddTHH:mm:ss"))}" +
                $"&endDateTime={Uri.EscapeDataString(end.ToString("yyyy-MM-ddTHH:mm:ss"))}" +
                "&$select=id,subject,start,end,isAllDay,webLink,location" +
                $"&$orderby=start/dateTime&$top={maxEvents}";

            using (var req = new HttpRequestMessage(HttpMethod.Get, url))
            {
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                // "GTB Standard Time" - Windows TZ id για Ελλάδα (ΟΧΙ IANA
                // "Europe/Athens" - το Graph API θέλει Windows TZ names εδώ).
                req.Headers.Add("Prefer", "outlook.timezone=\"GTB Standard Time\"");
                using (var resp = await _http.SendAsync(req))
                {
                    string body = await resp.Content.ReadAsStringAsync();
                    if (!resp.IsSuccessStatusCode)
                    {
                        DebugLog.Log($"[calendar] Graph API error {(int)resp.StatusCode}: {body}");
                        throw new Exception($"Αποτυχία ανάγνωσης calendar (Graph API {(int)resp.StatusCode}) - " +
                            "πιθανό θέμα δικαιωμάτων (Calendars.Read/Application Access Policy) ή ρύθμισης.");
                    }

                    JObject parsed = JObject.Parse(body);
                    var items = new JArray();
                    foreach (var ev in parsed["value"] as JArray ?? new JArray())
                    {
                        items.Add(new JObject
                        {
                            ["id"] = ev["id"],
                            ["subject"] = ev["subject"],
                            ["start"] = ev["start"]?["dateTime"],
                            ["end"] = ev["end"]?["dateTime"],
                            ["isAllDay"] = ev["isAllDay"],
                            ["webLink"] = ev["webLink"],
                            ["location"] = ev["location"]?["displayName"]
                        });
                    }
                    if (!string.IsNullOrWhiteSpace(searchText))
                    {
                        var filtered = new JArray();
                        foreach (var it in items)
                        {
                            string subj = it["subject"]?.ToString() ?? "";
                            if (subj.IndexOf(searchText, StringComparison.OrdinalIgnoreCase) >= 0)
                                filtered.Add(it);
                        }
                        return filtered;
                    }
                    return items;
                }
            }
        }

        // read_calendar tool - ΝΕΟ 17/08, ίδιο idiom με το read_email
        // (chat frame της κουρτίνας Email, βλ. JarvisAgentClient emailMode).
        public static readonly object ReadCalendarToolDefinition = new
        {
            name = "read_calendar",
            description =
                "Διαβάζει τα ραντεβού/events του Outlook calendar του " +
                "τρέχοντος χειριστή για ένα εύρος ημερομηνιών (π.χ. \"τι έχω " +
                "σήμερα\", \"έχω τίποτα αύριο/αυτή την εβδομάδα\") - ΜΟΝΟ " +
                "ανάγνωση.",
            input_schema = new
            {
                type = "object",
                properties = new
                {
                    startDate = new
                    {
                        type = "string",
                        description = "Αρχή εύρους (ISO date, π.χ. '2026-08-18'). Default: σήμερα."
                    },
                    endDate = new
                    {
                        type = "string",
                        description = "Τέλος εύρους (ISO date, αποκλειστικό). Default: startDate + 1 ημέρα."
                    }
                },
                required = new string[0]
            }
        };

        public static async Task<string> ExecuteReadCalendar(XSupport xSupport, JObject input)
        {
            DateTime start = DateTime.TryParse(input?["startDate"]?.ToString(), out var s) ? s.Date : DateTime.Today;
            DateTime end = DateTime.TryParse(input?["endDate"]?.ToString(), out var e) ? e.Date : start.AddDays(1);
            if (end <= start) end = start.AddDays(1);

            JArray events = await GetCalendarEventsAsync(xSupport, start, end);
            return JsonConvert.SerializeObject(new { success = true, events });
        }

        // ── create_outlook_event (LLM tool) - ΝΕΟ 18/08, ρητό αίτημα
        // χρήστη ("θέλω να μπορώ ως χειριστής να βάζω υπενθυμίσεις...
        // είτε στο Soft1 ως εργασίες, είτε στο Outlook Calendar"). Το
        // Soft1-σκέλος ΗΔΗ υπήρχε (create_crm_task με reminderDate, βλ.
        // JarvisTools.cs) - αυτό εδώ είναι το Outlook-σκέλος. ΠΛΗΡΕΣ event
        // (διάρκεια/τοποθεσία/καλεσμένοι - ρητή επιλογή χρήστη, ΟΧΙ απλή
        // υπενθύμιση). Graph POST /users/{mailbox}/events - ΙΔΙΟ
        // Calendars.ReadWrite permission που ΗΔΗ υπάρχει (ο χρήστης το
        // είχε προσθέσει πριν καν το ζητήσουμε - καμία νέα Azure ρύθμιση
        // χρειάζεται, σε αντίθεση με Mail.Send/Contacts.Read). "GTB
        // Standard Time" - ΙΔΙΟ Windows TZ id με το GetCalendarEventsAsync
        // πιο πάνω, για συνέπεια.
        public static async Task<JObject> CreateOutlookEventAsync(
            XSupport xSupport, string subject, DateTime start, DateTime end,
            string location, List<string> attendeeEmails, string body,
            int reminderMinutesBeforeStart, bool isAllDay)
        {
            if (string.IsNullOrWhiteSpace(subject)) throw new Exception("Λείπει το θέμα του ραντεβού.");

            string userEmail = GetCurrentUserEmail(xSupport);
            if (string.IsNullOrWhiteSpace(userEmail))
                throw new Exception("Δεν βρέθηκε email για τον τρέχοντα χειριστή (PRSN.EMAIL/USERS.EMAIL).");
            string token = await GetAccessTokenAsync(xSupport);

            var payload = new JObject
            {
                ["subject"] = subject,
                ["body"] = new JObject { ["contentType"] = "Text", ["content"] = body ?? "" },
                ["start"] = new JObject { ["dateTime"] = start.ToString("yyyy-MM-ddTHH:mm:ss"), ["timeZone"] = "GTB Standard Time" },
                ["end"] = new JObject { ["dateTime"] = end.ToString("yyyy-MM-ddTHH:mm:ss"), ["timeZone"] = "GTB Standard Time" },
                ["isReminderOn"] = true,
                ["reminderMinutesBeforeStart"] = reminderMinutesBeforeStart,
                ["isAllDay"] = isAllDay
            };
            if (!string.IsNullOrWhiteSpace(location))
                payload["location"] = new JObject { ["displayName"] = location };
            if (attendeeEmails != null && attendeeEmails.Count > 0)
            {
                var arr = new JArray();
                foreach (string email in attendeeEmails)
                {
                    arr.Add(new JObject
                    {
                        ["emailAddress"] = new JObject { ["address"] = email },
                        ["type"] = "required"
                    });
                }
                payload["attendees"] = arr;
            }

            string url = $"https://graph.microsoft.com/v1.0/users/{Uri.EscapeDataString(userEmail)}/events";
            using (var req = new HttpRequestMessage(HttpMethod.Post, url))
            {
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                req.Content = new StringContent(payload.ToString(Formatting.None), System.Text.Encoding.UTF8, "application/json");
                using (var resp = await _http.SendAsync(req))
                {
                    string respBody = await resp.Content.ReadAsStringAsync();
                    if (!resp.IsSuccessStatusCode)
                    {
                        DebugLog.Log($"[calendar-create] Graph API error {(int)resp.StatusCode}: {respBody}");
                        throw new Exception($"Αποτυχία δημιουργίας ραντεβού Outlook (Graph API {(int)resp.StatusCode}) - " +
                            "πιθανό θέμα δικαιωμάτων/ρύθμισης (Calendars.ReadWrite).");
                    }
                    JObject created = JObject.Parse(respBody);
                    return new JObject
                    {
                        ["id"] = created["id"],
                        ["webLink"] = created["webLink"]
                    };
                }
            }
        }

        public static readonly object CreateOutlookEventToolDefinition = new
        {
            name = "create_outlook_event",
            description =
                "Δημιουργεί ΝΕΟ ραντεβού/υπενθύμιση ΣΤΟ Outlook Calendar " +
                "του χειριστή. ΔΙΑΦΟΡΕΤΙΚΟ από το create_crm_task (που " +
                "φτιάχνει εργασία ΣΤΟ Soft1 με δικό του mechanism " +
                "υπενθύμισης) - αν ο χειριστής δεν διευκρινίσει ΠΟΥ θέλει " +
                "την υπενθύμιση (Soft1 εργασία ή Outlook calendar), ΡΩΤΑ " +
                "(❓/> quick-reply). ΑΝ δοθούν attendees (καλεσμένοι), ΘΑ " +
                "σταλούν ΠΡΑΓΜΑΤΙΚΕΣ προσκλήσεις email - ΑΝΕΠΙΣΤΡΕΠΤΗ " +
                "ενέργεια σε αυτή την περίπτωση: δείξε ΠΡΩΤΑ το πλήρες " +
                "draft (θέμα/ώρα/τοποθεσία/καλεσμένοι) και ΠΕΡΙΜΕΝΕ ρητή " +
                "επιβεβαίωση σε ΕΠΟΜΕΝΟ μήνυμα πριν καλέσεις το tool - " +
                "ΙΔΙΟΣ κανόνας με send_email. ΧΩΡΙΣ attendees (προσωπική " +
                "υπενθύμιση/ραντεβού, ΚΑΝΕΙΣ άλλος δεν ειδοποιείται) " +
                "μπορείς να το καλέσεις ΑΠΕΥΘΕΙΑΣ χωρίς επιβεβαίωση - ΙΔΙΟ " +
                "με το create_crm_task. Αν ο χειριστής δώσει ΟΝΟΜΑ (όχι " +
                "email) για καλεσμένο, ψάξε πρώτα PRSN (query_data) ή/και " +
                "search_outlook_contacts για να βρεις το email - ΙΔΙΑ " +
                "λογική με το name-resolution πριν το send_email.",
            input_schema = new
            {
                type = "object",
                properties = new
                {
                    subject = new { type = "string", description = "Θέμα/τίτλος ραντεβού." },
                    start = new { type = "string", description = "Ώρα έναρξης (ISO datetime, π.χ. '2026-08-20T10:00:00')." },
                    end = new { type = "string", description = "Ώρα λήξης (ISO datetime). Αν λείπει, default 30 λεπτά μετά το start." },
                    location = new { type = "string", description = "Προαιρετικό - τοποθεσία." },
                    attendees = new
                    {
                        type = "array",
                        items = new { type = "string" },
                        description = "Προαιρετικό - λίστα email διευθύνσεων καλεσμένων (ΗΔΗ resolved, όχι ονόματα). ΑΝ μη-κενό, στέλνονται πραγματικές προσκλήσεις."
                    },
                    body = new { type = "string", description = "Προαιρετικό - σημειώσεις/περιγραφή του ραντεβού." },
                    reminderMinutesBeforeStart = new { type = "integer", description = "Πόσα λεπτά πριν να ειδοποιήσει το Outlook. Default 15." },
                    isAllDay = new { type = "boolean", description = "true αν είναι ολοήμερο ραντεβού. Default false." }
                },
                required = new[] { "subject", "start" }
            }
        };

        public static async Task<string> ExecuteCreateOutlookEvent(XSupport xSupport, JObject input)
        {
            string subject = input?["subject"]?.ToString();
            if (!DateTime.TryParse(input?["start"]?.ToString(), out DateTime start))
                throw new Exception("Λείπει/άκυρη η ώρα έναρξης του ραντεβού.");
            if (!DateTime.TryParse(input?["end"]?.ToString(), out DateTime end))
                end = start.AddMinutes(30);
            string location = input?["location"]?.ToString();
            string body = input?["body"]?.ToString();
            bool isAllDay = input?["isAllDay"]?.ToObject<bool?>() ?? false;
            int reminderMinutes = input?["reminderMinutesBeforeStart"]?.ToObject<int?>() ?? 15;

            List<string> attendees = null;
            var attendeesArr = input?["attendees"] as JArray;
            if (attendeesArr != null && attendeesArr.Count > 0)
                attendees = attendeesArr.Select(t => t.ToString()).Where(s => !string.IsNullOrWhiteSpace(s)).ToList();

            JObject created = await CreateOutlookEventAsync(
                xSupport, subject, start, end, location, attendees, body, reminderMinutes, isAllDay);
            return JsonConvert.SerializeObject(new
            {
                success = true,
                id = created["id"],
                webLink = created["webLink"]
            });
        }

        // ΝΕΟ 17/08, ρητό αίτημα χρήστη - "θα μπορείς να κατεβάζεις και
        // συνημμένα;". Graph API GET /messages/{id}/attachments - για
        // fileAttachment (ο κοινός τύπος, π.χ. pdf/xlsx/εικόνα) το
        // contentBytes έρχεται ΚΑΤΕΥΘΕΙΑΝ base64-encoded μέσα στην ΙΔΙΑ
        // απάντηση, ΔΕΝ χρειάζεται δεύτερο download request. itemAttachment
        // (forwarded email σαν συνημμένο) / referenceAttachment (OneDrive
        // link) ΑΓΝΟΟΥΝΤΑΙ προς το παρόν - εκτός σκοπείου, ΔΕΝ έχουν
        // contentBytes με τον ίδιο τρόπο.
        public static readonly object DownloadEmailAttachmentToolDefinition = new
        {
            name = "download_email_attachment",
            description =
                "Κατεβάζει τα συνημμένα ΕΝΟΣ email (Office 365) σε τοπικό " +
                "φάκελο (Έγγραφα\\Jarvis Exports) - χρειάζεται το 'id' του " +
                "email (από το read_email, ΜΟΝΟ αν hasAttachments=true). " +
                "Κατεβάζει ΟΛΑ τα συνημμένα εκτός αν δοθεί attachmentName " +
                "για ΕΝΑ συγκεκριμένο.",
            input_schema = new
            {
                type = "object",
                properties = new
                {
                    messageId = new
                    {
                        type = "string",
                        description = "Το id του email (από το read_email)."
                    },
                    attachmentName = new
                    {
                        type = "string",
                        description = "Προαιρετικό - όνομα συγκεκριμένου συνημμένου. Αν λείπει, κατεβάζονται όλα."
                    }
                },
                required = new[] { "messageId" }
            }
        };

        public static async Task<string> ExecuteDownloadEmailAttachment(XSupport xSupport, JObject input)
        {
            string messageId = input?["messageId"]?.ToString();
            if (string.IsNullOrWhiteSpace(messageId))
                throw new Exception("Λείπει το messageId.");
            string attachmentName = input?["attachmentName"]?.ToString();

            string userEmail = GetCurrentUserEmail(xSupport);
            if (string.IsNullOrWhiteSpace(userEmail))
                throw new Exception("Δεν βρέθηκε email για τον τρέχοντα χειριστή (PRSN.EMAIL/USERS.EMAIL).");

            string token = await GetAccessTokenAsync(xSupport);

            string url = $"https://graph.microsoft.com/v1.0/users/{Uri.EscapeDataString(userEmail)}" +
                $"/messages/{Uri.EscapeDataString(messageId)}/attachments";

            using (var req = new HttpRequestMessage(HttpMethod.Get, url))
            {
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                using (var resp = await _http.SendAsync(req))
                {
                    string body = await resp.Content.ReadAsStringAsync();
                    if (!resp.IsSuccessStatusCode)
                    {
                        DebugLog.Log($"[email] attachments Graph API error {(int)resp.StatusCode}: {body}");
                        throw new Exception($"Αποτυχία λήψης συνημμένων (Graph API {(int)resp.StatusCode}).");
                    }

                    JObject parsed = JObject.Parse(body);
                    var saved = new JArray();
                    foreach (var a in parsed["value"] as JArray ?? new JArray())
                    {
                        string odataType = a["@odata.type"]?.ToString();
                        if (odataType != "#microsoft.graph.fileAttachment")
                            continue; // itemAttachment/referenceAttachment - εκτός σκοπείου προς το παρόν

                        string name = a["name"]?.ToString() ?? "attachment";
                        if (!string.IsNullOrWhiteSpace(attachmentName) &&
                            !string.Equals(name, attachmentName, StringComparison.OrdinalIgnoreCase))
                            continue;

                        string contentBytesB64 = a["contentBytes"]?.ToString();
                        if (string.IsNullOrEmpty(contentBytesB64)) continue;

                        byte[] bytes = Convert.FromBase64String(contentBytesB64);
                        string path = BuildAttachmentSavePath(name);
                        File.WriteAllBytes(path, bytes);

                        saved.Add(new JObject { ["name"] = name, ["path"] = path, ["sizeBytes"] = bytes.Length });
                    }

                    if (saved.Count == 0)
                        throw new Exception("Δεν βρέθηκαν συνημμένα σε αυτό το email (ή δεν ταίριαξε το attachmentName).");

                    return JsonConvert.SerializeObject(new { success = true, attachments = saved });
                }
            }
        }

        // Ίδιο path convention με JarvisShell.BuildExportPath/JarvisTools.
        // BuildDirectExportPath (Έγγραφα\Jarvis Exports\{name}_{timestamp}.
        // {ext}) - ΞΕΧΩΡΙΣΤΟ, μικρό αντίγραφο εδώ (ίδιο σκεπτικό: κρατάει
        // το JarvisEmailAccess αυτόνομο για το δικό του write path, ΟΧΙ
        // cross-file εξάρτηση σε private member).
        private static string BuildAttachmentSavePath(string filename)
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "Jarvis Exports");
            Directory.CreateDirectory(dir);

            string safeName = string.Join("_",
                (string.IsNullOrWhiteSpace(filename) ? "attachment" : filename)
                    .Split(Path.GetInvalidFileNameChars()));
            string ext = Path.GetExtension(safeName);
            string nameOnly = Path.GetFileNameWithoutExtension(safeName);
            string stamped = $"{nameOnly}_{DateTime.Now:yyyyMMdd_HHmmss}{ext}";
            return Path.Combine(dir, stamped);
        }
    }
}
