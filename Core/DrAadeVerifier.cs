using System;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Linq;

namespace S1Jarvis.Core
{
    // ══════════════════════════════════════════════════════════════════════
    // DrAadeVerifier
    //
    // HTTP GET στο einvoice.impact.gr link (από QR code) — Φορολογική
    // Προβολή (2ο tab) που περιέχει τα structured data όπως διαβιβάστηκαν
    // στην ΑΑΔΕ. Parse με Regex χωρίς εξωτερικό NuGet package.
    //
    // Fail-soft: αν δεν υπάρχει QR ή αποτύχει → Score=0, συνεχίζει.
    // ══════════════════════════════════════════════════════════════════════
    public class AadeInvoiceData
    {
        public string IssuerAfm     { get; set; } = "";
        public string RecipientAfm  { get; set; } = "";
        public string DocType       { get; set; } = "";
        public string DocSeries     { get; set; } = "";
        public string DocNumber     { get; set; } = "";
        public string DocDate       { get; set; } = "";
        public double NetTotal      { get; set; }
        public double VatTotal      { get; set; }
        public double GrandTotal    { get; set; }
        public string Mark          { get; set; } = "";   // Μ.Αρ.Κ.
        public List<AadeLineItem> Lines { get; set; } = new List<AadeLineItem>();
        public bool   FetchSuccess  { get; set; }
    }

    public class AadeLineItem
    {
        public string Description { get; set; } = "";
        public double NetValue    { get; set; }
        public double VatAmount   { get; set; }
    }

    public class AadeMatchScore
    {
        public bool   IssuerAfmMatch  { get; set; }
        public bool   AmountsMatch    { get; set; }
        public bool   LineCountMatch  { get; set; }
        public bool   MarkPresent     { get; set; }
        public double Score           { get; set; }   // 0-1
        public string Notes           { get; set; } = "";
    }

    public static class DrAadeVerifier
    {
        private static readonly HttpClient Http = new HttpClient();
        private const double AmountTolerance = 0.02;   // 2% ανοχή στρογγυλοποίησης

        static DrAadeVerifier()
        {
            ServicePointManager.SecurityProtocol =
                SecurityProtocolType.Tls12 | SecurityProtocolType.Tls13;
            Http.Timeout = TimeSpan.FromSeconds(15);
            Http.DefaultRequestHeaders.TryAddWithoutValidation(
                "User-Agent",
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");
            Http.DefaultRequestHeaders.TryAddWithoutValidation("Accept-Language", "el-GR,el;q=0.9");
        }

        // ── Fetch + Parse ─────────────────────────────────────────────────
        public static async Task<AadeInvoiceData> FetchAsync(string aadeLink)
        {
            var result = new AadeInvoiceData();

            if (string.IsNullOrWhiteSpace(aadeLink) || !aadeLink.StartsWith("https://einvoice.impact.gr"))
                return result;

            try
            {
                using (var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(15)))
                using (var resp = await Http.GetAsync(aadeLink, cts.Token).ConfigureAwait(false))
                {
                    string html = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                    ParseHtml(html, result);
                    result.FetchSuccess = !string.IsNullOrEmpty(result.IssuerAfm);
                    S1Jarvis.Core.DebugLog.Log("[AADE] fetch ok issuerAfm=" + result.IssuerAfm +
                        " grandTotal=" + result.GrandTotal + " mark=" + result.Mark);
                }
            }
            catch (Exception ex)
            {
                S1Jarvis.Core.DebugLog.Log("[AADE] fetch failed: " + ex.Message);
            }

            return result;
        }

        // ── Compare with ExtractionResult ─────────────────────────────────
        // extraction: DR JObject shape { issuer:{afm}, totals:{grand_total}, line_items:[...] }
        public static AadeMatchScore Compare(AadeInvoiceData aade, Newtonsoft.Json.Linq.JObject extracted, string issuerAfm)
        {
            var score = new AadeMatchScore();

            if (!aade.FetchSuccess || extracted == null)
            {
                score.Notes = "Δεν υπάρχουν ΑΑΔΕ δεδομένα για αντιπαραβολή.";
                return score;
            }

            string cleanIssuer = new string((issuerAfm ?? "").Where(char.IsDigit).ToArray());
            score.IssuerAfmMatch = !string.IsNullOrEmpty(aade.IssuerAfm) &&
                string.Equals(aade.IssuerAfm, cleanIssuer, StringComparison.Ordinal);

            double extractedTotal = ParseAmount(extracted["totals"]?["grand_total"]?.ToString());
            if (extractedTotal <= 0)
            {
                var items = extracted["line_items"] as Newtonsoft.Json.Linq.JArray;
                if (items != null) extractedTotal = items.Sum(l => ParseAmount(l["line_total"]?.ToString() ?? l["total"]?.ToString()));
            }
            score.AmountsMatch = aade.GrandTotal > 0 && extractedTotal > 0 &&
                Math.Abs(aade.GrandTotal - extractedTotal) / aade.GrandTotal <= AmountTolerance;

            int extractedLines = (extracted["line_items"] as Newtonsoft.Json.Linq.JArray)?.Count ?? 0;
            score.LineCountMatch = aade.Lines.Count > 0 &&
                Math.Abs(aade.Lines.Count - extractedLines) <= 1;   // ανοχή 1 γραμμής

            // 4. Μ.Αρ.Κ. παρόν
            score.MarkPresent = !string.IsNullOrEmpty(aade.Mark);

            // ── Score calculation ─────────────────────────────────────────
            // ΑΦΜ: 50%, Ποσά: 30%, Γραμμές: 10%, Mark: 10%
            score.Score =
                (score.IssuerAfmMatch  ? 0.50 : 0.0) +
                (score.AmountsMatch    ? 0.30 : 0.0) +
                (score.LineCountMatch  ? 0.10 : 0.0) +
                (score.MarkPresent     ? 0.10 : 0.0);

            score.Notes = $"ΑΦΜ:{(score.IssuerAfmMatch?"✓":"✗")} " +
                          $"Ποσά:{(score.AmountsMatch?"✓":"✗")} " +
                          $"Γραμμές:{(score.LineCountMatch?"✓":"✗")} " +
                          $"Μ.Αρ.Κ.:{(score.MarkPresent?"✓":"✗")}";

            S1Jarvis.Core.DebugLog.Log("[AADE] compare score=" + score.Score.ToString("F2") + " " + score.Notes);
            return score;
        }

        // ── HTML Parser ───────────────────────────────────────────────────
        private static void ParseHtml(string html, AadeInvoiceData data)
        {
            // Αφαιρούμε όλα τα HTML tags → καθαρό κείμενο με single spaces.
            // Έτσι τα regex ταιριάζουν ανεξάρτητα από τη δομή του HTML.
            string text = Regex.Replace(html, @"<script[^>]*>.*?</script>", " ", RegexOptions.Singleline | RegexOptions.IgnoreCase);
            text = Regex.Replace(text, @"<style[^>]*>.*?</style>", " ", RegexOptions.Singleline | RegexOptions.IgnoreCase);
            text = Regex.Replace(text, @"<[^>]+>", " ");
            text = System.Net.WebUtility.HtmlDecode(text);
            text = Regex.Replace(text, @"\s+", " ").Trim();

            // Φορολογική Προβολή: "Στοιχεία Εκδότη ΑΦΜ 099356700 Κωδ. Χώρας GR"
            data.IssuerAfm    = ExtractPattern(text, @"Στοιχεία Εκδότη\s+ΑΦΜ\s+(\d{9})");
            data.RecipientAfm = ExtractPattern(text, @"Στοιχεία Πελάτη\s+ΑΦΜ\s+(\d{9})");

            // Fallback από Εμπορική Προβολή: πρώτο ΑΦΜ = εκδότης
            if (string.IsNullOrEmpty(data.IssuerAfm))
                data.IssuerAfm = ExtractPattern(text, @"ΑΦΜ\s+(\d{9})");

            data.DocSeries = ExtractPattern(text, @"Σειρά Παραστατικού\s+(\S+)");
            data.DocNumber = ExtractPattern(text, @"ΑΑ Παραστατικού\s+(\d+)");
            data.DocDate   = ExtractPattern(text, @"Ημερομηνία Έκδοσης\s+(\d{1,2}/\d{1,2}/\d{4})");
            data.DocType   = ExtractPattern(text, @"Είδος Παραστατικού\s+([\d\.]+)");

            data.NetTotal   = ParseAmount(ExtractPattern(text, @"Σύνολο Καθαρής Αξίας\s+([\d\.,]+)"));
            data.VatTotal   = ParseAmount(ExtractPattern(text, @"Σύνολο ΦΠΑ\s+([\d\.,]+)"));
            data.GrandTotal = ParseAmount(ExtractPattern(text, @"Συνολική Αξία\s+([\d\.,]+)"));

            data.Mark = ExtractPattern(text, @"Μοναδικός Αριθμός Καταχώρησης Παραστατικού\s+(\d+)");

            ParseLines(text, data.Lines);
        }

        private static void ParseLines(string text, List<AadeLineItem> lines)
        {
            // Λεπτομέρειες Παραστατικού: "Περιγραφή Είδους ΡΕΥΜΑ ... Καθαρή Αξία 99,96 ... Ποσό ΦΠΑ 23,99"
            var descs = Regex.Matches(text, @"Περιγραφή Είδους\s+(.+?)\s+Επισήμανση");
            var nets  = Regex.Matches(text, @"Επισήμανση\s+\d+\s+Καθαρή Αξία\s+([\d\.,]+)");
            var vats  = Regex.Matches(text, @"Κατηγορία ΦΠΑ\s+\d+\s+Ποσό ΦΠΑ\s+([\d\.,]+)");

            for (int i = 0; i < descs.Count; i++)
            {
                lines.Add(new AadeLineItem
                {
                    Description = descs[i].Groups[1].Value.Trim(),
                    NetValue    = i < nets.Count ? ParseAmount(nets[i].Groups[1].Value) : 0,
                    VatAmount   = i < vats.Count ? ParseAmount(vats[i].Groups[1].Value) : 0,
                });
            }
        }

        private static string ExtractPattern(string text, string pattern)
        {
            var m = Regex.Match(text, pattern, RegexOptions.IgnoreCase);
            return m.Success ? m.Groups[1].Value.Trim() : "";
        }

        private static double ParseAmount(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return 0;
            value = value.Trim();
            if (value.Contains(".") && value.Contains(","))
                value = value.Replace(".", "").Replace(",", ".");
            else if (value.Contains(",") && !value.Contains("."))
                value = value.Replace(",", ".");
            double.TryParse(value,
                System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture,
                out double result);
            return result;
        }
    }
}
