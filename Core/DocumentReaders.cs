using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Xml.Linq;

namespace S1Jarvis.Core
{
    // ══════════════════════════════════════════════════════════════════════
    // DocumentReaders - ΝΕΟ 18/08, ρητό αίτημα χρήστη ("αρχικά πρέπει να
    // καταφέρει να διαβάζει word, excel, pdf, csv, json, txt, xml"). PDF
    // ήδη δουλεύει (native Anthropic document API, βλ. JarvisAgentClient.
    // AskAsync). CSV/JSON/TXT/XML ήδη δουλεύουν με ΜΗΔΕΝ αλλαγή εδώ - είναι
    // ήδη κείμενο, ο υπάρχων "text attachment" μηχανισμός (index.html
    // loadTextAttachment) απλά χρειάζεται να αναγνωρίζει τις καταλήξεις
    // (βλ. isTextAttachmentFile).
    //
    // ΑΥΤΟ το αρχείο καλύπτει ΜΟΝΟ τα δύο δυαδικά (binary) formats που
    // χρειάζονται πραγματικό parsing - .xlsx/.docx (OOXML = ZIP + XML,
    // .NET Framework το χειρίζεται ΧΩΡΙΣ κανένα εξωτερικό NuGet, ίδια
    // φιλοσοφία με το XlsxWriter.cs - "no dependency" αλλά αντίστροφα,
    // READ αντί για WRITE).
    //
    // ΡΗΤΑ ΕΚΤΟΣ ΣΚΟΠΕΙΟΥ (ρητά αναφέρθηκε στον χρήστη, ΟΧΙ σιωπηλή
    // παράλειψη): legacy binary .xls/.xls (pre-2007 OLE format) - ΤΕΛΕΙΩΣ
    // διαφορετική δυαδική μορφή, χρειάζεται πραγματική βιβλιοθήκη. Αν
    // κάποιος ανεβάσει τέτοιο αρχείο, ρητό φιλικό μήνυμα, ΟΧΙ crash/
    // σιωπηλή αποτυχία.
    //
    // ΓΝΩΣΤΟΙ ΠΕΡΙΟΡΙΣΜΟΙ (v1, τεκμηριωμένοι εδώ ώστε να μην ξαναψαχτούν):
    //  - XLSX: διαβάζει ΜΟΝΟ τιμές (κείμενο/αριθμό), ΟΧΙ formulas (παίρνει
    //    την ΗΔΗ cached τιμή <v> - το Excel την αποθηκεύει και για formula
    //    κελιά, ΔΕΝ χρειάζεται re-evaluation). Ημερομηνίες εμφανίζονται ΩΣ
    //    Excel serial numbers (π.χ. "45678"), ΟΧΙ formatted ημερομηνία -
    //    θα χρειαζόταν styles.xml/numFmt lookup, ΕΚΤΟΣ v1 σκοπείου.
    //  - DOCX: παράγραφοι (<w:p>) + πίνακες (<w:tbl>, ανά γραμμή pipe-
    //    separated) - ΟΧΙ headers/footers/footnotes/εικόνες.
    // ══════════════════════════════════════════════════════════════════════
    internal static class DocumentReaders
    {
        // Raised from the SAME deterministic Office-reader pipeline that already
        // proved reliable in production. A subscriber may inspect the parsed
        // workbook and decide whether it is a Jarvis UAT workbook. Ordinary
        // workbooks remain completely unaffected.
        internal static event Action<string, string> XlsxWorkbookRead;

        private static readonly XNamespace SpreadsheetNs =
            "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        private static readonly XNamespace RelationshipsNs =
            "http://schemas.openxmlformats.org/package/2006/relationships";
        private static readonly XNamespace DocRelNs =
            "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
        private static readonly XNamespace WordNs =
            "http://schemas.openxmlformats.org/wordprocessingml/2006/main";

        // Dispatch με βάση mimeType/όνομα αρχείου - ΡΗΤΟ fail (με φιλικό
        // μήνυμα ελληνικά) για ΟΤΙΔΗΠΟΤΕ δεν αναγνωρίζεται, ΠΟΤΕ σιωπηλή
        // προσπάθεια parsing κάτι άγνωστου.
        public static string ReadOfficeDocumentAsText(byte[] bytes, string mimeType, string fileName)
        {
            string ext = (fileName != null && fileName.Contains("."))
                ? fileName.Substring(fileName.LastIndexOf('.')).ToLowerInvariant()
                : "";

            bool isXlsx = ext == ".xlsx" ||
                mimeType == "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";
            bool isDocx = ext == ".docx" ||
                mimeType == "application/vnd.openxmlformats-officedocument.wordprocessingml.document";
            bool isLegacyExcel = ext == ".xls" || mimeType == "application/vnd.ms-excel";
            bool isLegacyWord = ext == ".doc" || mimeType == "application/msword";

            if (isLegacyExcel || isLegacyWord)
                throw new Exception(
                    "Η παλιά μορφή αρχείου (" + ext + ") δεν υποστηρίζεται - είναι εντελώς " +
                    "διαφορετική δυαδική μορφή (pre-2007). Αποθήκευσε το σαν " +
                    (isLegacyExcel ? ".xlsx" : ".docx") + " και ξαναδοκίμασε.");

            if (isXlsx)
            {
                string text = ReadXlsxAsText(bytes);

                // Never allow a UAT subscriber failure to break normal XLSX
                // reading. The Office reader remains the authoritative parser;
                // UAT detection is an optional side-channel on top of it.
                try
                {
                    XlsxWorkbookRead?.Invoke(fileName ?? "attachment.xlsx", text);
                }
                catch
                {
                    // Intentionally fail-open for normal document reading.
                }

                return text;
            }

            if (isDocx) return ReadDocxAsText(bytes);

            throw new Exception($"Μη αναγνωρίσιμος τύπος αρχείου για ανάγνωση: {mimeType} ({fileName}).");
        }

        // ── XLSX (ZIP: xl/sharedStrings.xml + xl/workbook.xml +
        // xl/_rels/workbook.xml.rels + xl/worksheets/sheetN.xml) ──────────
        public static string ReadXlsxAsText(byte[] bytes)
        {
            using (var ms = new MemoryStream(bytes))
            using (var archive = new ZipArchive(ms, ZipArchiveMode.Read))
            {
                string[] sharedStrings = ReadSharedStrings(archive);
                var sheets = ReadSheetList(archive);
                if (sheets.Count == 0)
                    throw new Exception("Δεν βρέθηκαν φύλλα μέσα στο .xlsx (μη έγκυρο/κατεστραμμένο αρχείο;).");

                var sb = new StringBuilder();
                foreach (var (name, path) in sheets)
                {
                    ZipArchiveEntry sheetEntry = archive.GetEntry(path);
                    if (sheetEntry == null) continue;

                    sb.AppendLine($"### Φύλλο: {name}");
                    using (Stream s = sheetEntry.Open())
                    {
                        XDocument doc = XDocument.Load(s);
                        XElement sheetData = doc.Root?.Element(SpreadsheetNs + "sheetData");
                        if (sheetData == null) { sb.AppendLine(); continue; }

                        foreach (XElement row in sheetData.Elements(SpreadsheetNs + "row"))
                        {
                            var cells = row.Elements(SpreadsheetNs + "c")
                                .Select(c => ReadCellText(c, sharedStrings))
                                .ToList();
                            if (cells.Any(c => !string.IsNullOrWhiteSpace(c)))
                                sb.AppendLine(string.Join(" | ", cells));
                        }
                    }
                    sb.AppendLine();
                }
                return sb.ToString();
            }
        }

        private static string[] ReadSharedStrings(ZipArchive archive)
        {
            ZipArchiveEntry entry = archive.GetEntry("xl/sharedStrings.xml");
            if (entry == null) return new string[0];
            using (Stream s = entry.Open())
            {
                XDocument doc = XDocument.Load(s);
                return doc.Root?.Elements(SpreadsheetNs + "si")
                    .Select(si => string.Concat(si.Descendants(SpreadsheetNs + "t").Select(t => t.Value)))
                    .ToArray() ?? new string[0];
            }
        }

        // (SheetName, ZipEntryPath) - ΠΡΕΠΕΙ να περάσει από το rels file
        // (r:id -> Target) γιατί η σειρά/ονόματα sheetN.xml στο ZIP ΔΕΝ
        // αντιστοιχούν πάντα 1-προς-1 με τη σειρά εμφάνισης στο workbook.
        private static System.Collections.Generic.List<(string Name, string Path)> ReadSheetList(ZipArchive archive)
        {
            var result = new System.Collections.Generic.List<(string, string)>();
            var relMap = new System.Collections.Generic.Dictionary<string, string>();

            ZipArchiveEntry relsEntry = archive.GetEntry("xl/_rels/workbook.xml.rels");
            if (relsEntry != null)
            {
                using (Stream s = relsEntry.Open())
                {
                    XDocument doc = XDocument.Load(s);
                    foreach (XElement rel in doc.Root?.Elements(RelationshipsNs + "Relationship") ?? Enumerable.Empty<XElement>())
                    {
                        string id = (string)rel.Attribute("Id");
                        string target = (string)rel.Attribute("Target");
                        if (id != null && target != null) relMap[id] = target;
                    }
                }
            }

            ZipArchiveEntry wbEntry = archive.GetEntry("xl/workbook.xml");
            if (wbEntry != null)
            {
                using (Stream s = wbEntry.Open())
                {
                    XDocument doc = XDocument.Load(s);
                    XElement sheetsEl = doc.Root?.Element(SpreadsheetNs + "sheets");
                    foreach (XElement sheet in sheetsEl?.Elements(SpreadsheetNs + "sheet") ?? Enumerable.Empty<XElement>())
                    {
                        string name = (string)sheet.Attribute("name") ?? "Sheet";
                        string rId = (string)sheet.Attribute(DocRelNs + "id");
                        if (rId != null && relMap.TryGetValue(rId, out string target))
                        {
                            string path = target.StartsWith("/") ? target.TrimStart('/') : "xl/" + target;
                            result.Add((name, path));
                        }
                    }
                }
            }
            return result;
        }

        // t="s" -> shared string (index στο <v>), t="inlineStr" -> κείμενο
        // ΜΕΣΑ στο κελί (<is><t>...), t="str" -> αποτέλεσμα formula ως
        // κείμενο (ήδη cached), ΔΕΝ έχει t -> αριθμός (ή κενό).
        private static string ReadCellText(XElement cell, string[] sharedStrings)
        {
            string type = (string)cell.Attribute("t");
            if (type == "inlineStr")
            {
                XElement isEl = cell.Element(SpreadsheetNs + "is");
                return isEl != null ? string.Concat(isEl.Descendants(SpreadsheetNs + "t").Select(t => t.Value)) : "";
            }
            string raw = cell.Element(SpreadsheetNs + "v")?.Value;
            if (raw == null) return "";
            if (type == "s" && int.TryParse(raw, out int idx) && idx >= 0 && idx < sharedStrings.Length)
                return sharedStrings[idx];
            return raw; // αριθμός ή ήδη-κείμενο formula-result
        }

        // ── DOCX (ZIP: word/document.xml - <w:body> με <w:p> παραγράφους
        // και <w:tbl> πίνακες) ───────────────────────────────────────────
        public static string ReadDocxAsText(byte[] bytes)
        {
            using (var ms = new MemoryStream(bytes))
            using (var archive = new ZipArchive(ms, ZipArchiveMode.Read))
            {
                ZipArchiveEntry entry = archive.GetEntry("word/document.xml");
                if (entry == null)
                    throw new Exception("Δεν βρέθηκε word/document.xml μέσα στο .docx (μη έγκυρο/κατεστραμμένο αρχείο;).");

                using (Stream s = entry.Open())
                {
                    XDocument doc = XDocument.Load(s);
                    XElement body = doc.Root?.Element(WordNs + "body");
                    if (body == null) return "";

                    var sb = new StringBuilder();
                    foreach (XElement el in body.Elements())
                    {
                        if (el.Name == WordNs + "p")
                        {
                            string text = string.Concat(el.Descendants(WordNs + "t").Select(t => t.Value));
                            sb.AppendLine(text);
                        }
                        else if (el.Name == WordNs + "tbl")
                        {
                            foreach (XElement row in el.Elements(WordNs + "tr"))
                            {
                                var cells = row.Elements(WordNs + "tc")
                                    .Select(tc => string.Concat(tc.Descendants(WordNs + "t").Select(t => t.Value)));
                                sb.AppendLine(string.Join(" | ", cells));
                            }
                            sb.AppendLine();
                        }
                    }
                    return sb.ToString();
                }
            }
        }
    }
}
