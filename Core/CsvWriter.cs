using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace S1Jarvis.Core
{
    // ══════════════════════════════════════════════════════════════════════
    // CsvWriter — γράφει string[] γραμμές (ragged/άνισο μήκος επιτρέπεται,
    // π.χ. 2-στηλες κάρτες + πολύ-στηλους πίνακες στο ίδιο export) σε CSV.
    // ══════════════════════════════════════════════════════════════════════
    internal static class CsvWriter
    {
        // ';' αντί για ',' - στα Ελληνικά Windows/Excel (el-GR locale) η ','
        // είναι το δεκαδικό διαχωριστικό, άρα το Excel περιμένει από
        // προεπιλογή ';' σαν διαχωριστικό στηλών στο CSV.
        private const char Delimiter = ';';

        public static void Write(string path, List<string[]> rows)
        {
            var sb = new StringBuilder();
            foreach (var row in rows)
            {
                if (row == null) continue;
                var cells = new string[row.Length];
                for (int i = 0; i < row.Length; i++) cells[i] = EscapeCell(row[i]);
                sb.Append(string.Join(Delimiter.ToString(), cells)).Append("\r\n");
            }

            // UTF-8 BOM ώστε το Excel να αναγνωρίσει σωστά το encoding και να
            // δείξει σωστά τους ελληνικούς χαρακτήρες (χωρίς BOM τους δείχνει
            // σαν "μπερδεμένα" σύμβολα σε πολλές εκδόσεις Excel).
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
        }

        private static string EscapeCell(string cell)
        {
            cell = cell ?? string.Empty;
            bool needsQuotes = cell.IndexOf(Delimiter) >= 0 || cell.IndexOf('"') >= 0 ||
                                cell.IndexOf('\n') >= 0 || cell.IndexOf('\r') >= 0;
            if (!needsQuotes) return cell;
            return "\"" + cell.Replace("\"", "\"\"") + "\"";
        }
    }
}
