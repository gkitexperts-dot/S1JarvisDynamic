using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;

namespace S1Jarvis.Core
{
    // ══════════════════════════════════════════════════════════════════════
    // XlsxWriter — ελάχιστος, self-contained .xlsx writer (OOXML/SpreadsheetML)
    // πάνω σε System.IO.Compression.ZipArchive (μέρος του .NET Framework 4.5+,
    // ΧΩΡΙΣ κανένα εξωτερικό NuGet dependency - π.χ. ClosedXML/EPPlus). Γράφει
    // ένα single-sheet workbook από μια λίστα string[] γραμμές, ragged/άνισο
    // μήκος επιτρέπεται (π.χ. 2-στηλες κάρτες + πολύ-στηλους πίνακες στο ίδιο
    // export - βλ. blocksToExportRows στο index.html).
    //
    // Αριθμητικά κελιά (π.χ. λίτρα, κιλά) γράφονται σαν πραγματικοί αριθμοί
    // (t="n") ώστε να είναι SUM-άρίσιμα στο Excel - ΕΚΤΟΣ από τιμές με
    // leading zero (π.χ. κωδικός "0234"), που μένουν string για να μη χαθεί
    // το zero.
    // ══════════════════════════════════════════════════════════════════════
    internal static class XlsxWriter
    {
        // Θετικός/αρνητικός ακέραιος ή δεκαδικός, ΧΩΡΙΣ leading zero (εκτός
        // από "0"/"0.x") και ΧΩΡΙΣ διαχωριστικά χιλιάδων - αποφεύγει
        // λανθασμένη ερμηνεία locale-formatted κειμένου (π.χ. "812.340" σαν
        // ελληνικό ακέραιο 812340 ή σαν αγγλικό δεκαδικό 812.34 - διφορούμενο,
        // άρα καλύτερα να μείνει string παρά να μαντέψουμε λάθος).
        private static readonly Regex NumericCell =
            new Regex(@"^-?(0|[1-9]\d*)(\.\d+)?$", RegexOptions.Compiled);

        public static void Write(string path, List<string[]> rows)
        {
            using (var fs = new FileStream(path, FileMode.Create))
            using (var archive = new ZipArchive(fs, ZipArchiveMode.Create))
            {
                WriteEntry(archive, "[Content_Types].xml", ContentTypesXml);
                WriteEntry(archive, "_rels/.rels", RelsXml);
                WriteEntry(archive, "xl/workbook.xml", WorkbookXml);
                WriteEntry(archive, "xl/_rels/workbook.xml.rels", WorkbookRelsXml);
                WriteEntry(archive, "xl/styles.xml", StylesXml);
                WriteEntry(archive, "xl/worksheets/sheet1.xml", BuildSheetXml(rows));
            }
        }

        private static void WriteEntry(ZipArchive archive, string entryName, string content)
        {
            var entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
            using (var stream = entry.Open())
            using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
            {
                writer.Write(content);
            }
        }

        private static string BuildSheetXml(List<string[]> rows)
        {
            var sb = new StringBuilder();
            sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
            sb.Append("<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">");
            sb.Append("<sheetData>");

            for (int r = 0; r < rows.Count; r++)
            {
                var row = rows[r];
                if (row == null || row.Length == 0) continue; // κενή γραμμή-διαχωριστικό blocks

                sb.Append("<row r=\"").Append(r + 1).Append("\">");
                for (int c = 0; c < row.Length; c++)
                {
                    string val = row[c];
                    if (string.IsNullOrEmpty(val)) continue;

                    string cellRef = ColumnLetter(c) + (r + 1);
                    if (NumericCell.IsMatch(val))
                    {
                        sb.Append("<c r=\"").Append(cellRef).Append("\" t=\"n\"><v>")
                          .Append(val).Append("</v></c>");
                    }
                    else
                    {
                        sb.Append("<c r=\"").Append(cellRef).Append("\" t=\"inlineStr\"><is><t xml:space=\"preserve\">")
                          .Append(XmlEscape(val)).Append("</t></is></c>");
                    }
                }
                sb.Append("</row>");
            }

            sb.Append("</sheetData></worksheet>");
            return sb.ToString();
        }

        // 0-based index στηλών → γράμματα Excel (A, B, ..., Z, AA, AB, ...).
        private static string ColumnLetter(int index)
        {
            var sb = new StringBuilder();
            index++;
            while (index > 0)
            {
                int rem = (index - 1) % 26;
                sb.Insert(0, (char)('A' + rem));
                index = (index - 1) / 26;
            }
            return sb.ToString();
        }

        private static string XmlEscape(string s) =>
            s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

        private const string ContentTypesXml =
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
            "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">" +
            "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>" +
            "<Default Extension=\"xml\" ContentType=\"application/xml\"/>" +
            "<Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/>" +
            "<Override PartName=\"/xl/worksheets/sheet1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/>" +
            "<Override PartName=\"/xl/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml\"/>" +
            "</Types>";

        private const string RelsXml =
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
            "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
            "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/>" +
            "</Relationships>";

        private const string WorkbookXml =
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
            "<workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" " +
            "xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\">" +
            "<sheets><sheet name=\"Sheet1\" sheetId=\"1\" r:id=\"rId1\"/></sheets>" +
            "</workbook>";

        private const string WorkbookRelsXml =
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
            "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
            "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet1.xml\"/>" +
            "<Relationship Id=\"rId2\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles\" Target=\"styles.xml\"/>" +
            "</Relationships>";

        // Ελάχιστο έγκυρο styles.xml (1 font/fill/border/cellXf) - απαιτείται
        // από το OOXML schema, το Excel αρνείται να ανοίξει το αρχείο χωρίς
        // αυτό ακόμα κι αν δεν χρειαζόμαστε custom στυλ.
        private const string StylesXml =
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
            "<styleSheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">" +
            "<fonts count=\"1\"><font><sz val=\"11\"/><name val=\"Calibri\"/></font></fonts>" +
            "<fills count=\"1\"><fill><patternFill patternType=\"none\"/></fill></fills>" +
            "<borders count=\"1\"><border/></borders>" +
            "<cellStyleXfs count=\"1\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\"/></cellStyleXfs>" +
            "<cellXfs count=\"1\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\"/></cellXfs>" +
            "</styleSheet>";
    }
}
