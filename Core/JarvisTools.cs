using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Softone;

namespace S1Jarvis.Core
{
    // ══════════════════════════════════════════════════════════════════════
    // JarvisTools
    //
    // Ορισμοί tools (JSON schema, ανώνυμα objects - ίδιο idiom με το
    // Nexus/ProxyAgentClient) + η εκτέλεσή τους πάνω σε XSupport.
    // ══════════════════════════════════════════════════════════════════════
    internal static class JarvisTools
    {
        // ── query_data: read-only SQL, γενικό (οποιοδήποτε SELECT) ─────────
        public static readonly object QueryDataToolDefinition = new
        {
            name = "query_data",
            description =
                "Εκτελεί ΜΟΝΟ SELECT ερωτήματα SQL Server πάνω στη βάση του " +
                "Soft1 και επιστρέφει τα αποτελέσματα σε JSON. Χρησιμοποίησέ " +
                "το για οποιαδήποτε ερώτηση αφορά δεδομένα (πελάτες, " +
                "φορτώσεις, τιμές, δεξαμενές, παραστατικά κ.λπ.). ΜΟΝΟ " +
                "SELECT — καμία εγγραφή ή τροποποίηση δεδομένων επιτρέπεται.",
            input_schema = new
            {
                type = "object",
                properties = new
                {
                    sql = new
                    {
                        type = "string",
                        description =
                            "Ένα SELECT statement, T-SQL syntax. Χρησιμοποίησε " +
                            "TOP N για να περιορίσεις τα αποτελέσματα " +
                            "(π.χ. SELECT TOP 50 ...)."
                    }
                },
                required = new[] { "sql" }
            }
        };

        // Άμυνα σε βάθος - ίδια λογική με SALDOC.TryFillReceiverFromParam του
        // S1Courier: μόνο SELECT, καμία δεύτερη εντολή, καμία επικίνδυνη
        // λέξη-κλειδί ακόμα κι αν ξεκινάει με SELECT. ΚΟΙΝΟ helper -
        // χρησιμοποιείται ΚΑΙ από το query_data ΚΑΙ από το
        // export_query_to_file (βλ. πιο κάτω) - ΜΙΑ πηγή αλήθειας για το
        // security check, όχι διπλασιασμένο/ενδεχομένως out-of-sync.
        private static string ValidateSelectOnly(string sql)
        {
            if (string.IsNullOrWhiteSpace(sql))
                throw new Exception("Λείπει το ερώτημα SQL.");

            string trimmed = sql.Trim();
            if (!trimmed.StartsWith("SELECT", StringComparison.OrdinalIgnoreCase))
                throw new Exception("Επιτρέπονται μόνο SELECT ερωτήματα.");

            string noTrailing = trimmed.TrimEnd(';', ' ', '\r', '\n', '\t');
            if (noTrailing.Contains(";"))
                throw new Exception("Δεν επιτρέπονται πολλαπλές εντολές SQL.");

            // ΠΡΟΣΟΧΗ: whole-word matching (\b...\b), ΟΧΙ IndexOf/Contains -
            // το IndexOf έπιανε "EXEC" μέσα σε ονόματα στηλών σαν
            // "Executiondate" (substring, όχι λέξη) και μπλόκαρε νόμιμα
            // queries. Το "\b" σε regex σταματάει σε κάθε non-word char
            // (κενό, τελεία, παρένθεση...), άρα "Executiondate" δεν ταιριάζει
            // πια με "\bEXEC\b" αφού δεν υπάρχει όριο λέξης μετά το "Exec".
            // Τα sp_/xp_ είναι προθέματα (όχι πλήρεις λέξεις) - \b μόνο πριν,
            // ώστε να πιάνει "sp_help" αλλά όχι κάτι σαν "wasp_thing".
            string[] forbiddenWords =
            {
                "INSERT", "UPDATE", "DELETE", "DROP", "ALTER",
                "EXEC", "EXECUTE", "TRUNCATE", "MERGE", "GRANT", "CREATE"
            };
            foreach (var word in forbiddenWords)
            {
                if (Regex.IsMatch(noTrailing, $@"\b{word}\b", RegexOptions.IgnoreCase))
                    throw new Exception(
                        $"Το ερώτημα περιέχει μη επιτρεπτή λέξη-κλειδί: {word}");
            }

            string[] forbiddenPrefixes = { "sp_", "xp_" };
            foreach (var prefix in forbiddenPrefixes)
            {
                if (Regex.IsMatch(noTrailing, $@"\b{prefix}", RegexOptions.IgnoreCase))
                    throw new Exception(
                        $"Το ερώτημα περιέχει μη επιτρεπτή λέξη-κλειδί: {prefix}");
            }

            return noTrailing;
        }

        public static string ExecuteQueryData(XSupport xSupport, string sql)
        {
            string noTrailing = ValidateSelectOnly(sql);
            XTable result = xSupport.GetSQLDataSet(noTrailing);

            // CreateDataTable(true): πραγματικό System.Data.DataTable, με
            // Columns/Rows γενικά - δεν χρειάζεται να ξέρουμε τα ονόματα
            // στηλών εκ των προτέρων (το SQL είναι δυναμικό, από το Claude).
            DataTable dt = result.CreateDataTable(true);

            var rows = new List<Dictionary<string, object>>();
            foreach (DataRow row in dt.Rows)
            {
                var dict = new Dictionary<string, object>();
                foreach (DataColumn col in dt.Columns)
                {
                    var val = row[col];
                    dict[col.ColumnName] = (val == DBNull.Value) ? null : val;
                }
                rows.Add(dict);
            }

            // Μην πνίξουμε το context του Claude με τεράστια αποτελέσματα.
            // ΔΙΟΡΘΩΘΗΚΕ 15/08: totalRowCount ΞΕΧΩΡΙΣΤΟ από rowCount - πριν,
            // το "rowCount" ήταν ΗΔΗ το κομμένο πλήθος (≤200) ακόμα κι όταν
            // υπήρχαν περισσότερες πραγματικά· ο Jarvis δεν είχε τρόπο να
            // πει σωστά "βρέθηκαν Χ εγγραφές" όταν Χ>200 (θα έλεγε "200").
            int totalRowCount = rows.Count;
            const int maxRows = 200;
            bool truncated = rows.Count > maxRows;
            if (truncated) rows = rows.Take(maxRows).ToList();

            var payload = new { rowCount = rows.Count, totalRowCount, truncated, rows };
            return JsonConvert.SerializeObject(payload);
        }

        // ══════════════════════════════════════════════════════════════════
        // export_query_to_file - ΝΕΟ 15/08 (βλ. README "Preview + ρητή
        // επιλογή αποθήκευσης"): τρέχει SELECT ΑΠΕΥΘΕΙΑΣ στη βάση και γράφει
        // το αποτέλεσμα ΑΠΕΥΘΕΙΑΣ σε αρχείο (Excel/CSV) - τα δεδομένα ΔΕΝ
        // περνάνε ποτέ από το context του Claude (σε αντίθεση με το
        // query_data, που έχει hard cap 200 ΓΙΑ ΝΑ προστατεύει το context).
        // Έτσι μπορεί να εξάγει πολύ περισσότερες γραμμές - μέχρι το δικό
        // του, ξεχωριστό, παραμετρικό όριο (0 = χωρίς όριο).
        // ══════════════════════════════════════════════════════════════════

        public static readonly object ExportQueryToFileToolDefinition = new
        {
            name = "export_query_to_file",
            description =
                "Εκτελεί ΜΟΝΟ SELECT ερώτημα SQL Server και γράφει τα " +
                "αποτελέσματα ΑΠΕΥΘΕΙΑΣ σε αρχείο (Excel ή CSV) στο δίσκο, " +
                "ΧΩΡΙΣ να περάσουν τα δεδομένα από το context σου - " +
                "χρησιμοποίησέ το ΜΟΝΟ όταν ο χειριστής έχει ήδη ζητήσει " +
                "ρητά να αποθηκευτούν ΟΛΑ τα αποτελέσματα ενός μεγάλου " +
                "ερωτήματος (μετά από preview με query_data). Υπάρχει " +
                "παραμετρικό όριο πλήθους γραμμών - αν ξεπεραστεί, " +
                "εξάγονται μόνο οι πρώτες Ν και το tool result σου το λέει.",
            input_schema = new
            {
                type = "object",
                properties = new
                {
                    sql = new
                    {
                        type = "string",
                        description = "Το ΙΔΙΟ (ή ισοδύναμο) SELECT statement με αυτό του preview."
                    },
                    format = new
                    {
                        type = "string",
                        @enum = new[] { "xlsx", "csv" },
                        description = "Μορφή αρχείου."
                    },
                    filename = new
                    {
                        type = "string",
                        description = "Περιγραφικό όνομα αρχείου, ΧΩΡΙΣ επέκταση."
                    }
                },
                required = new[] { "sql", "format", "filename" }
            }
        };

        // ParamCode 500011 ("Μέγιστες Γραμμές σε Απευθείας Εξαγωγή Αρχείου
        // AI") - ΔΕΝ επιβεβαιωμένο ακόμα (πρόταση, ίδιο μοτίβο με
        // 500009/500010). 0 = ΧΩΡΙΣ όριο (εξάγει ΟΛΑ), αλλιώς μέγιστο
        // πλήθος γραμμών. Default 5000 αν λείπει η παράμετρος - ρητή τιμή
        // του χρήστη "προς το παρόν".
        private const int DefaultDirectExportMaxRows = 5000;

        public static int GetDirectExportMaxRows(XSupport xSupport)
        {
            try
            {
                XTable t = xSupport.GetSQLDataSet(
                    "SELECT ParamValue FROM cccParams WHERE ParamCode=500011");
                if (t == null || t.Count == 0) return DefaultDirectExportMaxRows;

                int value = Convert.ToInt32(t.Current["ParamValue"]);
                // 0 = σκόπιμα "χωρίς όριο" (έγκυρη τιμή) - μόνο αρνητικό
                // θεωρείται λάθος/τυχαία τιμή.
                return value >= 0 ? value : DefaultDirectExportMaxRows;
            }
            catch (Exception ex)
            {
                DebugLog.Log("[export] GetDirectExportMaxRows EXCEPTION, fallback σε default: " + ex);
                return DefaultDirectExportMaxRows;
            }
        }

        // Ίδιο path convention με JarvisShell.BuildExportPath (Έγγραφα\
        // Jarvis Exports\{filename}_{timestamp}.{ext}) - ΞΕΧΩΡΙΣΤΟ,
        // μικρό αντίγραφο εδώ (private, ~10 γραμμές) αντί να εκθέτουμε
        // cross-file το private member του JarvisShell· κρατάει το
        // JarvisTools αυτόνομο για το δικό του write path.
        private static string BuildDirectExportPath(string filename, string ext)
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "Jarvis Exports");
            Directory.CreateDirectory(dir);

            string safeName = string.Join("_",
                (string.IsNullOrWhiteSpace(filename) ? "Jarvis_export" : filename)
                    .Split(Path.GetInvalidFileNameChars()));
            string stamped = $"{safeName}_{DateTime.Now:yyyyMMdd_HHmmss}{ext}";
            return Path.Combine(dir, stamped);
        }

        public static string ExecuteExportQueryToFile(XSupport xSupport, JObject input)
        {
            string sql = input?["sql"]?.ToString();
            string format = input?["format"]?.ToString();
            string filename = input?["filename"]?.ToString();

            if (format != "xlsx" && format != "csv")
                throw new Exception("Μη έγκυρη μορφή αρχείου (μόνο 'xlsx' ή 'csv').");

            string validatedSql = ValidateSelectOnly(sql);

            XTable result = xSupport.GetSQLDataSet(validatedSql);
            DataTable dt = result.CreateDataTable(true);

            // Κόψιμο ΣΕ MEMORY (C#), ΟΧΙ με SQL wrapper (π.χ. "SELECT TOP N
            // * FROM (...) AS x") - ένα SELECT με δικό του ORDER BY δεν
            // επιτρέπεται ως subquery χωρίς ΔΙΚΟ ΤΟΥ TOP/OFFSET, θα έσκαγε
            // σε αρκετά νόμιμα queries. Το SQL Server ήδη επέστρεψε όλα τα
            // αποτελέσματα - το μόνο κόστος είναι λίγη επιπλέον μνήμη/χρόνο
            // για γνήσια μεγάλα reports, αποδεκτό tradeoff για μια
            // περιστασιακή "αποθήκευσε τα όλα" ενέργεια.
            int maxRows = GetDirectExportMaxRows(xSupport);
            int totalFound = dt.Rows.Count;
            int rowsToWrite = (maxRows > 0) ? Math.Min(maxRows, totalFound) : totalFound;
            bool wasCapped = maxRows > 0 && totalFound > maxRows;

            var columnNames = dt.Columns.Cast<DataColumn>().Select(c => c.ColumnName).ToArray();
            var exportRows = new List<string[]> { columnNames };
            for (int i = 0; i < rowsToWrite; i++)
            {
                DataRow row = dt.Rows[i];
                exportRows.Add(columnNames.Select(c =>
                    row[c] == DBNull.Value ? "" : Convert.ToString(row[c])).ToArray());
            }

            string ext = format == "xlsx" ? ".xlsx" : ".csv";
            string path = BuildDirectExportPath(filename, ext);

            if (format == "xlsx") XlsxWriter.Write(path, exportRows);
            else CsvWriter.Write(path, exportRows);

            DebugLog.Log($"[export_query_to_file] path={path} rowsWritten={rowsToWrite} " +
                $"totalFound={totalFound} wasCapped={wasCapped}");

            var payload = new
            {
                success = true,
                path,
                rowsWritten = rowsToWrite,
                totalFound,
                wasCapped
            };
            return JsonConvert.SerializeObject(payload);
        }

        // ══════════════════════════════════════════════════════════════════
        // Help mode Q&A log → SOACTION (βλ. README.md "Phase 2c" - πλήρες
        // σχέδιο/field mapping εκεί). ΟΧΙ raw SQL INSERT - το SOACTION είναι
        // business object, write μέσω XModule/XTable (επιβεβαιωμένο pattern
        // από SoftoneCore/SoftoneCommands.cs + επίσημα SoftOne SDK examples).
        // ══════════════════════════════════════════════════════════════════

        // "ΛΕΞΕΙΣ-ΚΛΕΙΔΙΑ: .../ΠΕΡΙΛΗΨΗ ΑΙΤΗΜΑΤΟΣ: .../ΛΥΣΗ:\n1. ...\n2. ..."
        // - βλ. README "Mapping πεδίων". Singleline ώστε η ΠΕΡΙΛΗΨΗ (μπορεί
        // να είναι 2 γραμμές) και η ΛΥΣΗ (πολλαπλά βήματα) να πιάνονται
        // σωστά με το '.' να περνάει πάνω από newlines.
        private static readonly Regex QaMarkerRegex = new Regex(
            @"ΛΕΞΕΙΣ-ΚΛΕΙΔΙΑ:\s*(?<keywords>.+?)\r?\n" +
            @"\s*ΠΕΡΙΛΗΨΗ ΑΙΤΗΜΑΤΟΣ:\s*(?<summary>.+?)\r?\n" +
            @"\s*ΛΥΣΗ:\s*\r?\n(?<steps>.+)",
            RegexOptions.Singleline);

        public class QaMarkerResult
        {
            public string Keywords;
            public string RequestSummary;
            public string SolutionSteps;
        }

        // Ψάχνει το marker ΟΠΟΥΔΗΠΟΤΕ μέσα στο κείμενο (ο Jarvis μπορεί να
        // έχει γράψει και εισαγωγική πρόταση πριν) - αν δεν βρεθεί, ΔΕΝ
        // είναι "η λύση δόθηκε" (απλή ενδιάμεση ερώτηση/απάντηση μέσα στο
        // Help mode), το loop συνεχίζει κανονικά.
        public static bool TryParseQaMarker(string text, out QaMarkerResult result)
        {
            result = null;
            if (string.IsNullOrEmpty(text)) return false;

            var m = QaMarkerRegex.Match(text);
            if (!m.Success) return false;

            result = new QaMarkerResult
            {
                Keywords = m.Groups["keywords"].Value.Trim(),
                RequestSummary = m.Groups["summary"].Value.Trim(),
                SolutionSteps = m.Groups["steps"].Value.Trim()
            };
            return true;
        }

        // ParamCode 500008 ("s1Jarvice - Knowledge Base Series") -
        // επιβεβαιωμένο 15/08, ΤΩΡΑ ParamValue=30000, ΔΕΝ hardcoded.
        private static int GetQaLogSeries(XSupport xSupport)
        {
            XTable t = xSupport.GetSQLDataSet(
                "SELECT ParamValue FROM cccParams WHERE ParamCode=500008");
            if (t == null || t.Count == 0)
                throw new Exception(
                    "Δεν βρέθηκε η παράμετρος 500008 (Σειρά Knowledge Base) στο cccParams.");
            return Convert.ToInt32(t.Current["ParamValue"]);
        }

        // ΝΕΟ 15/08: ParamCode 500009 ("Πλήθος Δεκαδικών σε Reports AI") -
        // ΔΕΝ επιβεβαιωμένο ακόμα (πρόταση, ίδιο πνεύμα με το αρχικό
        // "50008" πριν επιβεβαιωθεί το πραγματικό 500008 - βλ. README
        // πίνακα παραμέτρων). ΣΕ ΑΝΤΙΘΕΣΗ με το SERIES (απαιτούμενο, throw
        // αν λείπει), εδώ είναι καθαρά προτίμηση μορφοποίησης - αν λείπει
        // η παράμετρος, ΔΕΝ σπάει το chat, γυρνάει ασφαλές default (2,
        // στάνταρ λογιστική πρακτική) και το καταγράφει στο DebugLog.
        private const int DefaultReportDecimalPlaces = 2;

        public static int GetReportDecimalPlaces(XSupport xSupport)
        {
            try
            {
                XTable t = xSupport.GetSQLDataSet(
                    "SELECT ParamValue FROM cccParams WHERE ParamCode=500009");
                if (t == null || t.Count == 0) return DefaultReportDecimalPlaces;

                int value = Convert.ToInt32(t.Current["ParamValue"]);
                // Λογικό εύρος - προστασία από τυχαία/λάθος τιμή στη βάση
                // (π.χ. 0 μηδενισμένο πεδίο) που θα έκανε τις αναφορές
                // άχρηστες.
                return (value >= 0 && value <= 6) ? value : DefaultReportDecimalPlaces;
            }
            catch (Exception ex)
            {
                DebugLog.Log("[decimals] GetReportDecimalPlaces EXCEPTION, fallback σε default: " + ex);
                return DefaultReportDecimalPlaces;
            }
        }

        // ΝΕΟ 19/08, ζωντανό bug report χρήστη ("δεν είναι αποδεκτό δεν
        // γίνεται να μην καταλαβαίνει ποιος είναι ο User που του μιλάει
        // ενώ στην αρχή του session τον έχει χαιρετίσει με το όνομά
        // του"): το greeting ("Γεια σου, Χ!") ήταν ΚΑΘΑΡΑ cosmetic UI
        // text (JarvisShell.GetDisplayName -> window.setGreeting JS,
        // ΠΟΤΕ δεν έφτανε στο system prompt) - ο ίδιος ο Jarvis δεν
        // "ήξερε" ποτέ ποιος του μιλάει, γι' αυτό ρωτούσε ξανά όταν
        // χρειαζόταν actorUserId (π.χ. "βάλε εργασία σε μένα"). ΙΔΙΟ
        // fallback chain (PRSN.NAME -> USERS.NAME -> null) με το
        // JarvisShell.GetDisplayName - static εδώ ώστε να το
        // ξαναχρησιμοποιεί ΚΑΙ το JarvisAgentClient.BuildSystemPrompt
        // (Τρέχον context, ΠΑΝΤΑ unconditional) χωρίς διπλό κώδικα.
        public static string GetCurrentUserDisplayName(XSupport xSupport)
        {
            int userId = xSupport.ConnectionInfo.UserId;

            try
            {
                XTable prsn = xSupport.GetSQLDataSet("SELECT NAME FROM PRSN WHERE USERS = :1", userId);
                var prsnName = prsn?.Current["NAME"]?.ToString();
                if (!string.IsNullOrWhiteSpace(prsnName)) return prsnName.Trim();
            }
            catch (Exception ex)
            {
                DebugLog.Log("[user] GetCurrentUserDisplayName PRSN EXCEPTION: " + ex);
            }

            try
            {
                XTable users = xSupport.GetSQLDataSet("SELECT NAME FROM USERS WHERE USERS = :1", userId);
                var usersName = users?.Current["NAME"]?.ToString();
                if (!string.IsNullOrWhiteSpace(usersName)) return usersName.Trim();
            }
            catch (Exception ex)
            {
                DebugLog.Log("[user] GetCurrentUserDisplayName USERS EXCEPTION: " + ex);
            }

            return null;
        }

        // INSERT - καλείται από JarvisShell όταν βρεθεί το marker (βλ.
        // TryParseQaMarker) στην απάντηση Help mode. Επιστρέφει το νέο
        // SOACTION id, ώστε το UI να μπορεί μετά να καλέσει RateQaLogSoAction
        // πάνω στο ΙΔΙΟ record (δεύτερο, ξεχωριστό write - UPDATE, όχι νέο
        // INSERT - βλ. README).
        public static int CreateQaLogSoAction(
            XSupport xSupport, string keywords, string requestSummary, string solutionSteps)
        {
            int series = GetQaLogSeries(xSupport);

            XModule m = xSupport.CreateModule("SOTASK");
            XTable soaction = m.GetTable("SOACTION");
            try
            {
                m.InsertData();
                soaction.Current["SERIES"] = series;
                // ΔΙΟΡΘΩΘΗΚΕ 15/08: ΟΧΙ SOACTIONCODE χειροκίνητα - γεμίζει
                // μόνο του (κανένα από τα working παραδείγματα στο
                // SoftoneCommands.cs δεν το όριζε ποτέ). Το SERIES=30000
                // (μοναδικό, δεσμευμένο ΜΟΝΟ για μας - βλ. ParamCode
                // 500008) είναι ήδη αρκετό tag για να ξεχωρίζουμε τις
                // δικές μας εγγραφές, δεν χρειάζεται δεύτερο πεδίο.
                soaction.Current["COMMENTS"] = "Jarvis Q&A";
                soaction.Current["REMARKS"] = Truncate(keywords, 2000);
                soaction.Current["cccInitRequest"] = requestSummary;
                soaction.Current["cccFinalResp"] = solutionSteps;
                soaction.Current["ACTOR"] = xSupport.ConnectionInfo.UserId;
                soaction.Current["ORDEREDBY"] = xSupport.ConnectionInfo.UserId;
                // "Ολοκληρωμένο" - ίδια τιμή με τα υπάρχοντα historic logs
                // στο SoftoneCommands.cs (InsertScannHistoric κ.ά.).
                soaction.Current["ACTSTATUS"] = 3;
                return m.PostData();
            }
            finally
            {
                soaction.Dispose();
                m.Dispose();
            }
        }

        // UPDATE - καλείται όταν ο χειριστής κλικάρει ⭐ (1-5) πάνω στο ήδη
        // καταχωρημένο SOACTION id. .Current.Edit(recordId) με το
        // ΠΡΑΓΜΑΤΙΚΟ id (όχι row index) - επιβεβαιωμένο 15/08 από τον
        // χρήστη, βλ. README "Cross-check με τα επίσημα SoftOne SDK
        // παραδείγματα".
        public static void RateQaLogSoAction(XSupport xSupport, int soactionId, int rating)
        {
            if (rating < 1 || rating > 5)
                throw new Exception("Η βαθμολογία πρέπει να είναι 1-5.");

            XModule m = xSupport.CreateModule("SOTASK");
            XTable soaction = m.GetTable("SOACTION");
            try
            {
                m.LocateData(soactionId);
                soaction.Current.Edit(soactionId);
                soaction.Current["SOSMALLINT"] = rating;
                m.PostData();
            }
            finally
            {
                soaction.Dispose();
                m.Dispose();
            }
        }

        // ΝΕΟ 17/08, ρητό αίτημα χρήστη - "εκπαίδευση" του Jarvis από τη
        // διαδικασία create_order (βλ. πιο κάτω): κάθε ΕΠΙΤΥΧΗΜΕΝΗ
        // καταχώρηση παραγγελίας καταγράφεται εδώ - ΙΔΙΟ idiom με
        // CreateQaLogSoAction/GetQaLogSeries πιο πάνω, αλλά ΞΕΧΩΡΙΣΤΗ,
        // δεσμευμένη σειρά (ρητό αίτημα - "δεσμεύσουμε μια σειρά Prompt",
        // "στο CRM"). ParamCode 500017 - επιβεβαιωμένο ζωντανά από τον
        // χρήστη 17/08 (ParamValue=30002, "500017 - 500014 - Jarvis Prompt
        // Records" - το "500014 -" στην περιγραφή είναι artifact
        // αντιγραφής, ΔΕΝ επηρεάζει τον κώδικα, διαβάζουμε μόνο το
        // ParamValue). Το REMARKS περιέχει ΤΗΝ ΙΔΙΑ την οδηγία (prompt)
        // του χειριστή + [doc:SOSOURCE:findocId] link - ώστε ο Jarvis να
        // μπορεί ΑΡΓΟΤΕΡΑ (query_data πάνω σε αυτή τη σειρά) να ανατρέξει
        // ΚΑΙ στο ΤΙ ζητήθηκε ΚΑΙ στο πραγματικό παραστατικό που προέκυψε
        // (πραγματικά δεδομένα, ΟΧΙ μόνο το prompt).
        private static int GetOrderPromptLogSeries(XSupport xSupport)
        {
            XTable t = xSupport.GetSQLDataSet(
                "SELECT ParamValue FROM cccParams WHERE ParamCode=500017");
            if (t == null || t.Count == 0)
                throw new Exception(
                    "Δεν βρέθηκε η παράμετρος 500017 (Σειρά Prompt Log παραγγελιών) στο cccParams.");
            return Convert.ToInt32(t.Current["ParamValue"]);
        }

        // Best-effort - ΔΕΝ πρέπει ΠΟΤΕ να ρίξει exception προς τα έξω (το
        // ίδιο το παραστατικό έχει ΉΔΗ καταχωρηθεί επιτυχώς όταν καλείται
        // αυτό - το logging είναι δευτερεύον, δεν πρέπει να "χαλάσει" μια
        // ήδη επιτυχημένη καταχώρηση). Επιστρέφει το νέο SOACTION id (ή -1
        // σε αποτυχία) - ΝΕΟ 17/08, ρητό αίτημα χρήστη: "να βάλουμε rating
        // όπως στο Help" - ο Jarvis γράφει '[⭐ Βαθμολόγησε](rate:id)' στο
        // τέλος της απάντησής του (βλ. BuildSystemPrompt), το UI το
        // μετατρέπει σε 5 κλικαριστά αστέρια -> RateQaLogSoAction (ήδη
        // υπάρχον, γενικό για ΟΠΟΙΟΔΗΠΟΤΕ SOACTION id, ΔΕΝ χρειάστηκε
        // τίποτα νέο εκεί).
        private static int LogOrderEntryPrompt(
            XSupport xSupport, int sosource, string circuitLabel, string sourceInstruction, int findocId)
        {
            try
            {
                int series = GetOrderPromptLogSeries(xSupport);
                XModule m = xSupport.CreateModule("SOTASK");
                XTable soaction = m.GetTable("SOACTION");
                try
                {
                    m.InsertData();
                    soaction.Current["SERIES"] = series;
                    soaction.Current["COMMENTS"] = "Jarvis Order Prompt - " + (circuitLabel ?? ("sosource " + sosource));
                    soaction.Current["REMARKS"] = Truncate(
                        (sourceInstruction ?? "") + "\n\n[doc:" + sosource + ":" + findocId + "]", 2000);
                    soaction.Current["ACTOR"] = xSupport.ConnectionInfo.UserId;
                    soaction.Current["ORDEREDBY"] = xSupport.ConnectionInfo.UserId;
                    soaction.Current["ACTSTATUS"] = 3;
                    return m.PostData();
                }
                finally
                {
                    soaction.Dispose();
                    m.Dispose();
                }
            }
            catch (Exception ex)
            {
                DebugLog.Log("[order_entry] LogOrderEntryPrompt EXCEPTION (μη κρίσιμο, το παραστατικό ΉΔΗ καταχωρήθηκε): " + ex);
                return -1;
            }
        }

        private static string Truncate(string s, int max) =>
            string.IsNullOrEmpty(s) || s.Length <= max ? s : s.Substring(0, max);

        // ══════════════════════════════════════════════════════════════════
        // open_url - ΝΕΟ 15/08 (βλ. README "Browser mode"). ΔΕΝ χρειάζεται
        // XSupport - καθαρά UI ενέργεια (navigate το δεύτερο WebView2 του
        // JarvisShell, βλ. onNavigate callback στο AskAsync/ExecuteTool).
        // Δεν επιστρέφει περιεχόμενο σελίδας - μόνο επιβεβαίωση επιτυχίας.
        // ══════════════════════════════════════════════════════════════════

        public static readonly object OpenUrlToolDefinition = new
        {
            name = "open_url",
            description =
                "Ανοίγει μια σελίδα ΑΠΕΥΘΕΙΑΣ στο browser pane που βλέπει ο " +
                "χειριστής (γράφει και τη διεύθυνση στο πεδίο). ΔΕΝ επιστρέφει " +
                "το περιεχόμενο της σελίδας - μόνο την ανοίγει. Χρησιμοποίησέ " +
                "το ΜΟΝΟ όταν ο χειριστής ζητήσει ρητά να δει/επισκεφτεί μια " +
                "σελίδα.",
            input_schema = new
            {
                type = "object",
                properties = new
                {
                    url = new
                    {
                        type = "string",
                        description =
                            "Πλήρες URL (με https://) ή domain (π.χ. " +
                            "example.com - το https:// προστίθεται αυτόματα)."
                    }
                },
                required = new[] { "url" }
            }
        };

        public static string ExecuteOpenUrl(string url, Action<string> onNavigate)
        {
            if (string.IsNullOrWhiteSpace(url))
                throw new Exception("Λείπει το URL.");
            if (onNavigate == null)
                throw new Exception("Το browser pane δεν είναι διαθέσιμο αυτή τη στιγμή.");

            onNavigate(url);

            var payload = new { success = true, url };
            return JsonConvert.SerializeObject(payload);
        }

        // ══════════════════════════════════════════════════════════════════
        // read_page_content - ΝΕΟ 15/08 (βλ. README "Browser mode"). Πριν, ο
        // Jarvis μπορούσε ΜΟΝΟ να ανοίξει σελίδες (open_url), όχι να δει τι
        // δείχνουν - το system prompt το έλεγε ρητά. Τώρα διαβάζει το ΟΡΑΤΟ
        // κείμενο (document.body.innerText, ΟΧΙ raw HTML - λιγότερος
        // θόρυβος/tags/scripts, πιο κοντά σε αυτό που βλέπει ο χειριστής)
        // της σελίδας που είναι ΤΩΡΑ φορτωμένη στο browserView, μέσω
        // ExecuteScriptAsync. Async (σε αντίθεση με τα υπόλοιπα tools) -
        // βλ. ExecuteTool στο JarvisAgentClient (έγινε async).
        // ══════════════════════════════════════════════════════════════════

        public static readonly object ReadPageContentToolDefinition = new
        {
            name = "read_page_content",
            description =
                "Διαβάζει το ΟΡΑΤΟ κείμενο (ΟΧΙ HTML tags/scripts) της " +
                "σελίδας που είναι ΤΩΡΑ ανοιχτή στο browser pane του " +
                "χειριστή. Χρησιμοποίησέ το ΜΕΤΑ από open_url (ή αν ο " +
                "χειριστής λέει ότι έχει ήδη μια σελίδα ανοιχτή) για να " +
                "καταλάβεις τι δείχνει, ΠΡΙΝ απαντήσεις σχετικά με το " +
                "περιεχόμενό της. Το κείμενο μπορεί να κοπεί αν είναι πολύ " +
                "μεγάλο (δες το πεδίο truncated στο αποτέλεσμα).",
            input_schema = new
            {
                type = "object",
                properties = new { },
                required = new string[0]
            }
        };

        // Μέγιστο πλήθος χαρακτήρων που περνάνε στο context του Claude -
        // ίδιο σκεπτικό με το maxRows του query_data (μην πνίξουμε το
        // context με ολόκληρη τη σελίδα). ΝΕΟ 18/08, ρητό αίτημα χρήστη
        // (ζωντανό παράδειγμα: σελίδα με 48 προϊόντα έκοβε στο #37, καμία
        // δυνατότητα "συνέχειας" - βλ. extract_page_tables για πραγματικά
        // <table> δεδομένα, αυτό εδώ είναι το raw-text fallback) - ΤΩΡΑ
        // ParamCode (500025), ΟΧΙ hardcoded 8000 - το πραγματικό context
        // window του Claude είναι τεράστιο (1M tokens), το 8000 ήταν
        // αυθαίρετα συντηρητικό, καμία ανάγκη να μείνει τόσο μικρό.
        // Default 40000 αν λείπει η παράμετρος (5x το παλιό όριο).
        private const int ParamPageContentMaxChars = 500025;
        private const int DefaultPageContentMaxChars = 40000;

        public static async Task<string> ExecuteReadPageContent(XSupport xSupport, Func<Task<string>> onReadPage)
        {
            if (onReadPage == null)
                throw new Exception("Το browser pane δεν είναι διαθέσιμο αυτή τη στιγμή.");

            int maxChars = GetCrmTaskOptionalParam(xSupport, ParamPageContentMaxChars, DefaultPageContentMaxChars);
            string text = await onReadPage() ?? "";
            bool truncated = text.Length > maxChars;
            string capped = truncated ? text.Substring(0, maxChars) : text;

            var payload = new { text = capped, truncated };
            return JsonConvert.SerializeObject(payload);
        }

        // ── extract_page_tables (LLM tool) - ΝΕΟ 18/08, ρητό αίτημα χρήστη:
        // "scraping δεδομένων από ιστοσελίδες". Το read_page_content πιο
        // πάνω δίνει ΩΜΟ κείμενο (innerText) - καλό για πρόζα, ΑΝΑΞΙΟΠΙΣΤΟ
        // για πραγματικά tabular δεδομένα (χάνεται η στοίχιση
        // στηλών/γραμμών). Αυτό εδώ διαβάζει ΚΑΤΕΥΘΕΙΑΝ τα πραγματικά
        // <table> elements της σελίδας (DOM, ΟΧΙ regex πάνω σε κείμενο) -
        // δομημένο header+rows, ΙΔΙΟ σχήμα με το query_data. Ο Claude το
        // ξαναγράφει σαν ΚΑΝΟΝΙΚΟ markdown table στην απάντησή του -
        // "δωρεάν" παίρνει ΟΛΟ το ήδη υπάρχον μηχανισμό (rendering + Excel/
        // CSV/PDF export toolbar, ΚΑΙ optional ```chart αν βολεύει) - καμία
        // ξεχωριστή export λογική χρειάστηκε εδώ. Για σύγκριση με Soft1
        // δεδομένα, ο Claude απλά καλεί ΚΑΙ query_data στην ΙΔΙΑ συζήτηση -
        // κανένα ειδικό "compare" tool δεν χρειάζεται, είναι απλά
        // reasoning πάνω σε δύο ήδη γνωστά σύνολα δεδομένων.
        //
        // Δύο βήματα (ΙΔΙΟ σχεδιασμό με το get_courier_voucher_data - μαζεύω
        // στοιχεία ΠΡΙΝ αποφασίσω): χωρίς tableIndex -> ΜΟΝΟ περίληψη (πόσοι
        // πίνακες υπάρχουν, μέγεθος/header ο καθένας) - ο Claude διαλέγει
        // ΠΟΙΟΝ θέλει ΧΩΡΙΣ να ξοδέψει context σε άσχετους/τεράστιους
        // πίνακες (π.χ. navigation/layout tables σε πολύπλοκα sites). ΜΕ
        // tableIndex -> πλήρη δεδομένα ΜΟΝΟ για εκείνον.
        //
        // ΓΝΩΣΤΟΣ ΠΕΡΙΟΡΙΣΜΟΣ v1: μόνο πραγματικά semantic <table> elements -
        // πολλά σύγχρονα sites φτιάχνουν "πίνακες" με <div>, ΔΕΝ πιάνονται
        // εδώ (θα χρειαστεί γενίκευση αργότερα αν χρειαστεί).
        public static readonly object ExtractPageTablesToolDefinition = new
        {
            name = "extract_page_tables",
            description =
                "Διαβάζει τα πραγματικά <table> elements (ΟΧΙ raw κείμενο) " +
                "της σελίδας που είναι ΤΩΡΑ ανοιχτή στο browser pane - " +
                "χρησιμοποίησέ το ΑΝΤΙ για read_page_content όταν ο " +
                "χειριστής ζητήσει δεδομένα από πίνακα/λίστα τιμών/σύγκριση " +
                "(π.χ. \"φέρε μου τις τιμές από αυτή τη σελίδα\"). ΡΟΗ: (1) " +
                "κάλεσέ το ΧΩΡΙΣ tableIndex πρώτα - επιστρέφει ΜΟΝΟ " +
                "περίληψη (πόσοι πίνακες, μέγεθος/header ο καθένας), ΟΧΙ " +
                "δεδομένα. (2) Διάλεξε ΠΟΙΟΝ πίνακα θέλεις (συνήθως ο " +
                "μεγαλύτερος με ουσιαστικό header) και ξανακάλεσέ το ΜΕ " +
                "tableIndex - ΤΩΡΑ επιστρέφει τα πραγματικά δεδομένα " +
                "(header+rows). Μετά, ΞΑΝΑΓΡΑΨΕ τα σαν ΚΑΝΟΝΙΚΟ markdown " +
                "table στην απάντησή σου (ο χειριστής παίρνει αυτόματα " +
                "Excel/CSV/PDF export κουμπιά) - ΜΗΝ τα παραθέσεις σαν λίστα " +
                "κειμένου. Για σύγκριση με εσωτερικά δεδομένα Soft1, κάλεσε " +
                "ΚΑΙ query_data στην ΙΔΙΑ συζήτηση.",
            input_schema = new
            {
                type = "object",
                properties = new
                {
                    tableIndex = new { type = "integer", description = "Index πίνακα (0-based) από το αποτέλεσμα χωρίς tableIndex - παράλειψέ το την ΠΡΩΤΗ φορά." }
                },
                required = new string[0]
            }
        };

        // Το row-cap (200 γραμμές, ίδιο σκεπτικό με MaxPageContentChars πιο
        // πάνω) γίνεται ΜΕΣΑ στο JS extraction script (βλ. JarvisShell.
        // ExtractBrowserPageTablesAsync) - πιο αποδοτικό να κοπεί ΕΚΕΙ
        // (πριν καν φύγει από τη σελίδα) παρά να στείλει τεράστιο JSON εδώ
        // και να κοπεί μετά.
        public static async Task<string> ExecuteExtractPageTables(JObject input, Func<int?, Task<string>> onExtractPageTables)
        {
            if (onExtractPageTables == null)
                throw new Exception("Το browser pane δεν είναι διαθέσιμο αυτή τη στιγμή.");

            int? tableIndex = (int?)input?["tableIndex"];
            string json = await onExtractPageTables(tableIndex) ?? "[]";
            return json;
        }

        // ══════════════════════════════════════════════════════════════════
        // open_document - ανοίγει ΑΠΕΥΘΕΙΑΣ την οθόνη ενός Designer object
        // μέσα στο ΙΔΙΟ το Soft1 (ΟΧΙ στο browser pane - τα SALDOC/PURDOC/
        // κλπ είναι native screens του ERP), είτε εντοπίζοντας ΕΝΑ ΥΠΑΡΧΟΝ
        // παραστατικό (AUTOLOCATE=id) είτε ανοίγοντας ΑΔΕΙΑ φόρμα για ΝΕΑ
        // καταχώρηση (BROWSERONLY=1). Επιβεβαιωμένος μηχανισμός από το
        // επίσημο SDK παράδειγμα (OutProcess/Example1/Form1.cs):
        //   Prg.ExecS1Command("CUSTOMER[AUTOLOCATE=69]", null);
        // όπου Prg είναι XSupport - fire-and-forget, ΔΕΝ χρειάζεται callback
        // (σε αντίθεση με το open_url που στοχεύει το browser pane μέσω
        // onNavigate) - το ExecS1Command τρέχει ΑΠΕΥΘΕΙΑΣ πάνω στο xSupport
        // που το JarvisTools ήδη έχει σαν παράμετρο.
        // ══════════════════════════════════════════════════════════════════

        private sealed class DocumentObjectInfo
        {
            public string ObjectName;
            public string Description;
        }

        // Αντιστοίχιση SOSOURCE (κύκλωμα παραστατικού) -> Designer object
        // name. ΜΟΝΟ επιβεβαιωμένα entries - ΕΠΙΤΗΔΕΣ ΔΕΝ μαντεύουμε άγνωστα
        // SOSOURCE (λάθος object name θα άνοιγε λάθος οθόνη μέσα στο Soft1
        // του χειριστή). Επιβεβαιώθηκαν ζωντανά 15/08:
        //  - 1351 Πωλήσεις/Τιμολόγια        -> SALDOC
        //  - 1353 Παροχή Υπηρεσιών (πωλ.)   -> LINCUSDOC (ΔΙΑΦΟΡΕΤΙΚΟ object
        //                                       από το 1351 - ΔΙΟΡΘΩΘΗΚΕ)
        //  - 1251 Παραλαβή/ΔΑ Προμηθευτή    -> PURDOC
        //  - 1253 Παροχή Υπηρεσιών (αγορ.)  -> LINSUPDOC (ΔΙΑΦΟΡΕΤΙΚΟ object
        //                                       από το 1251 - ΔΙΟΡΘΩΘΗΚΕ)
        //  - 5151 Ενδοδιακίνηση/Παραγωγή    -> ITEITEDOC
        //  - 1412 Έμβασμα σε προμηθευτή     -> BFNSUPDOC
        //  - 1413 Έμβασμα από πελάτη        -> BFNCUSDOC
        //  - 2021 Εργασία CRM (SOACTION)    -> SOTASK (ρητά 15/08 - το id
        //                                       για AUTOLOCATE είναι το
        //                                       soactionId, ΟΧΙ FINDOC -
        //                                       βλ. ExecuteCreateCrmTask)
        private static readonly Dictionary<int, DocumentObjectInfo> DocumentObjectsBySosource =
            new Dictionary<int, DocumentObjectInfo>
            {
                [1351] = new DocumentObjectInfo { ObjectName = "SALDOC", Description = "Πωλήσεις/Τιμολόγια" },
                [1353] = new DocumentObjectInfo { ObjectName = "LINCUSDOC", Description = "Παροχή Υπηρεσιών (πωλήσεις)" },
                [1251] = new DocumentObjectInfo { ObjectName = "PURDOC", Description = "Παραλαβή/ΔΑ Προμηθευτή" },
                [1253] = new DocumentObjectInfo { ObjectName = "LINSUPDOC", Description = "Παροχή Υπηρεσιών (αγορές)" },
                [5151] = new DocumentObjectInfo { ObjectName = "ITEITEDOC", Description = "Ενδοδιακίνηση/Παραγωγή" },
                [1412] = new DocumentObjectInfo { ObjectName = "BFNSUPDOC", Description = "Έμβασμα σε προμηθευτή" },
                [1413] = new DocumentObjectInfo { ObjectName = "BFNCUSDOC", Description = "Έμβασμα από πελάτη" },
                [2021] = new DocumentObjectInfo { ObjectName = "SOTASK", Description = "Εργασία CRM" },
            };

        public static readonly object OpenDocumentToolDefinition = new
        {
            name = "open_document",
            description =
                "Ανοίγει ΑΠΕΥΘΕΙΑΣ μέσα στο Soft1 (ΟΧΙ στο chat) την οθόνη " +
                "ενός παραστατικού - είτε εντοπίζοντας ΕΝΑ ΥΠΑΡΧΟΝ " +
                "(mode=locate, με το id του) είτε ανοίγοντας ΑΔΕΙΑ φόρμα για " +
                "ΝΕΑ καταχώρηση (mode=insert, χωρίς id). Υποστηρίζονται ΜΟΝΟ " +
                "οι παρακάτω κατηγορίες (sosource): 1351=Πωλήσεις/Τιμολόγια, " +
                "1353=Παροχή Υπηρεσιών (πωλήσεις), 1251=Παραλαβή/ΔΑ " +
                "Προμηθευτή, 1253=Παροχή Υπηρεσιών (αγορές), " +
                "5151=Ενδοδιακίνηση/Παραγωγή, 1412=Έμβασμα σε προμηθευτή, " +
                "1413=Έμβασμα από πελάτη, 2021=Εργασία CRM (το id εδώ είναι " +
                "το soactionId, ΟΧΙ FINDOC). Για οποιαδήποτε άλλη κατηγορία " +
                "ΜΗΝ το χρησιμοποιήσεις - πες στον χειριστή ότι δεν " +
                "υποστηρίζεται ακόμα.",
            input_schema = new
            {
                type = "object",
                properties = new
                {
                    sosource = new
                    {
                        type = "integer",
                        @enum = new[] { 1351, 1353, 1251, 1253, 5151, 1412, 1413, 2021 },
                        description = "Κωδικός κατηγορίας παραστατικού (SOSOURCE)."
                    },
                    mode = new
                    {
                        type = "string",
                        @enum = new[] { "locate", "insert" },
                        description =
                            "'locate' για να ανοίξει ΥΠΑΡΧΟΝ παραστατικό " +
                            "(χρειάζεται id) - 'insert' για άδεια φόρμα ΝΕΑΣ " +
                            "καταχώρησης (ΧΩΡΙΣ id)."
                    },
                    id = new
                    {
                        type = "integer",
                        description = "Το id του παραστατικού - ΜΟΝΟ όταν mode='locate'."
                    }
                },
                required = new[] { "sosource", "mode" }
            }
        };

        public static string ExecuteOpenDocument(XSupport xSupport, JObject input)
        {
            int? sosource = (int?)input?["sosource"];
            string mode = input?["mode"]?.ToString();

            if (sosource == null)
                throw new Exception("Λείπει το sosource (κατηγορία παραστατικού).");
            if (mode != "locate" && mode != "insert")
                throw new Exception("Το mode πρέπει να είναι 'locate' ή 'insert'.");

            if (!DocumentObjectsBySosource.TryGetValue(sosource.Value, out var docInfo))
            {
                string supported = string.Join(", ", DocumentObjectsBySosource
                    .Select(kv => $"{kv.Key}={kv.Value.Description}"));
                throw new Exception(
                    $"Δεν υποστηρίζεται ακόμα η κατηγορία sosource={sosource}. " +
                    $"Υποστηριζόμενες κατηγορίες: {supported}.");
            }

            string command;
            if (mode == "locate")
            {
                int? id = (int?)input?["id"];
                if (id == null)
                    throw new Exception("Λείπει το id (απαιτείται όταν mode='locate').");

                // Προσωποποιημένη προβολή (FORM=) - ΝΕΟ 15/08, βλ.
                // GetPersonalizedFormName πιο κάτω. null όταν ο χειριστής
                // δεν έχει ρυθμισμένη custom view γι' αυτό το SOSOURCE -
                // ΑΚΡΙΒΩΣ η ίδια συμπεριφορά με πριν (default view),
                // μηδενικό ρίσκο regression. ΝΕΟ 17/08, ρητό αίτημα χρήστη -
                // αν ο χειριστής ΔΕΝ έχει προσωπική ρύθμιση, fallback στην
                // παραμετρική προβολή (ParamCode 500018, βλ.
                // GetConfiguredFormName) - χρήσιμο ΕΙΔΙΚΑ για κυκλώματα
                // (π.χ. 1412/1413 εμβάσματα) που δεν είναι καν εγγράψιμα
                // από το create_order, άρα ΜΟΝΟ εδώ έχει νόημα η ρύθμισή τους.
                string formName = GetPersonalizedFormName(xSupport, xSupport.ConnectionInfo.UserId, sosource.Value)
                    ?? GetConfiguredFormName(xSupport, sosource.Value);
                command = string.IsNullOrEmpty(formName)
                    ? $"{docInfo.ObjectName}[AUTOLOCATE={id.Value}]"
                    : $"{docInfo.ObjectName}[AUTOLOCATE={id.Value},FORM={formName}]";
            }
            else
            {
                command = $"{docInfo.ObjectName}[BROWSERONLY=1]";
            }

            xSupport.ExecS1Command(command, null);
            DebugLog.Log($"[open_document] sosource={sosource} mode={mode} command={command}");

            var payload = new { success = true, objectName = docInfo.ObjectName, command };
            return JsonConvert.SerializeObject(payload);
        }

        // ══════════════════════════════════════════════════════════════════
        // DR (Document Reader) - Στάδιο 3α: ταυτοποίηση εκδότη + άνοιγμα
        // συναλλασσόμενου, ΝΕΟ 16/08 (βλ. README Roadmap #6, session notes).
        // ΡΗΤΑ περιορισμένο σκοπείο (απόφαση χρήστη): φτάνουμε μέχρι το
        // άνοιγμα του συναλλασσόμενου - ιστορικό σειράς/ΑΑΔΕ auto-create/
        // Αγορά-Δαπάνη ΕΙΝΑΙ επόμενο βήμα, ΔΕΝ χτίζονται ακόμα εδώ.
        //
        // ΚΡΙΣΙΜΟ, επιβεβαιωμένο ζωντανά 16/08: ο εκδότης ΔΕΝ είναι πάντα
        // πελάτης/προμηθευτής - μπορεί να είναι ΚΑΙ χρεώστης/πιστωτής, ΟΛΟΙ
        // ζουν στον ΙΔΙΟ πίνακα TRDR, διαφοροποιούνται ΜΟΝΟ από το SODTYPE.
        // Η αναζήτηση παρακάτω είναι ΕΠΙΤΗΔΕΣ χωρίς φίλτρο SODTYPE - βρίσκει
        // τον συναλλασσόμενο όποιο SODTYPE κι αν έχει. Το ΑΝΟΙΓΜΑ όμως
        // χρειάζεται να ξέρει ΠΟΙΟ Designer object να καλέσει - μόνο τα
        // 4 παρακάτω είναι επιβεβαιωμένα. Οποιοδήποτε ΑΛΛΟ SODTYPE βρεθεί
        // -> αναφέρεται ως "άγνωστος τύπος", objectName=null, ΚΑΝΕΝΑ άνοιγμα
        // (ΜΗΝ μαντέψεις object name - λάθος όνομα θα άνοιγε λάθος οθόνη).
        private static readonly Dictionary<int, string> TraderObjectsBySodType = new Dictionary<int, string>
        {
            [12] = "SUPPLIER",  // Προμηθευτής
            [13] = "CUSTOMER",  // Πελάτης
            [15] = "DEBTOR",    // Χρεώστης
            [16] = "CREDITOR",  // Πιστωτής
        };

        // ── find_trader_by_afm (LLM tool) - ΝΕΟ 18/08, ρητό αίτημα χρήστη:
        // "θέλω να το κάνουμε να λειτουργεί και για πελάτες και για
        // προμηθευτές με ελεύθερη συζήτηση, να μπορεί ο Jarvis να ανοίγει
        // συναλλασσόμενο" - reuse ΑΥΤΟΥΣΙΩΝ των ExecuteFindTraderByAfm/
        // ExecuteFindTraderByAfmAndSodType (ήδη έτοιμα από το standalone
        // CREATEAADEAFM command/DR flow, ΚΑΜΙΑ δεύτερη λογική εδώ).
        // Entitlement (JARVISDOCREADER) ελέγχεται στο JarvisAgentClient.
        // ExecuteTool, ΟΧΙ εδώ - ίδιο σκεπτικό με τα courier chat tools.
        public static readonly object FindTraderByAfmToolDefinition = new
        {
            name = "find_trader_by_afm",
            description =
                "Ψάχνει αν υπάρχει ήδη συναλλασσόμενος (Προμηθευτής/Πελάτης) " +
                "με συγκεκριμένο ΑΦΜ. Χρησιμοποίησέ το ΠΡΩΤΟ όταν ο χειριστής " +
                "ζητήσει να βρεις/ανοίξεις/δημιουργήσεις συναλλασσόμενο με " +
                "ΑΦΜ (π.χ. \"άνοιξέ μου σαν προμηθευτή το ΑΦΜ...\", \"υπάρχει " +
                "ήδη πελάτης με ΑΦΜ...\"). Αν βρεθεί, μπορείς να δώσεις " +
                "κλικαριστό link '[άνοιγμα](trader:OBJECTNAME:trdrId)' " +
                "(βλ. objectName/trdrId στο αποτέλεσμα - ΞΕΧΩΡΙΣΤΟ scheme " +
                "από τα 'doc:' links των παραστατικών, ΠΡΟΣΟΧΗ μην τα " +
                "μπερδέψεις). Αν ΔΕΝ βρεθεί, κάλεσε get_aade_data για να " +
                "προτείνεις δημιουργία.",
            input_schema = new
            {
                type = "object",
                properties = new
                {
                    afm = new { type = "string", description = "9-ψήφιο ΑΦΜ." },
                    sodType = new { type = "integer", description = "12=Προμηθευτής, 13=Πελάτης - βάλ' το ΜΟΝΟ αν ο χειριστής έχει διευκρινίσει ρητά τον τύπο. Παράλειψέ το για γενική αναζήτηση (οποιοσδήποτε τύπος - χρήσιμο όταν δεν έχει διευκρινιστεί, ή όταν ψάχνεις αν υπάρχει ΗΔΗ με οποιονδήποτε τρόπο πριν προτείνεις δημιουργία)." }
                },
                required = new[] { "afm" }
            }
        };

        public static string ExecuteFindTraderByAfmTool(XSupport xSupport, JObject input)
        {
            string afm = input?["afm"]?.ToString();
            int? sodType = (int?)input?["sodType"];
            return sodType.HasValue
                ? ExecuteFindTraderByAfmAndSodType(xSupport, afm, sodType.Value)
                : ExecuteFindTraderByAfm(xSupport, afm);
        }

        // Αναζήτηση συναλλασσόμενου με ΑΦΜ (χωρίς φίλτρο SODTYPE - βλ. πάνω).
        // ΔΕΝ πετάει exception σε "δεν βρέθηκε" - το caller (JarvisShell) το
        // στέλνει σαν κανονικό, μη-σφαλματικό αποτέλεσμα στο UI.
        public static string ExecuteFindTraderByAfm(XSupport xSupport, string afm)
        {
            if (string.IsNullOrWhiteSpace(afm))
                return JsonConvert.SerializeObject(new { found = false });

            int company = xSupport.ConnectionInfo.CompanyId;
            XTable t = xSupport.GetSQLDataSet(
                "SELECT TOP 1 TRDR, CODE, NAME, AFM, SODTYPE FROM TRDR " +
                "WHERE COMPANY=:1 AND AFM=:2 AND ISACTIVE=1",
                company, afm);

            if (t == null || t.Count == 0)
                return JsonConvert.SerializeObject(new { found = false });

            int trdrId = Convert.ToInt32(t.Current["TRDR"]);
            int sodType = Convert.ToInt32(t.Current["SODTYPE"]);
            string name = t.Current["NAME"] == DBNull.Value ? null : t.Current["NAME"].ToString();
            string code = t.Current["CODE"] == DBNull.Value ? null : t.Current["CODE"].ToString();
            string objectName = TraderObjectsBySodType.TryGetValue(sodType, out var obj) ? obj : null;

            return JsonConvert.SerializeObject(new
            {
                found = true,
                trdrId,
                sodType,
                name,
                code,
                objectName // null -> άγνωστος τύπος, το JS ΔΕΝ φτιάχνει link
            });
        }

        // Αναζήτηση συναλλασσόμενου με ΑΦΜ ΣΕ ΣΥΓΚΕΚΡΙΜΕΝΟ κύκλωμα (ΟΧΙ σε
        // ΟΠΟΙΟΔΗΠΟΤΕ SODTYPE όπως το ExecuteFindTraderByAfm πιο πάνω) -
        // ΝΕΟ 16/08, για την εντολή CREATEAADEAFM (index.html): ο χειριστής
        // δηλώνει ΡΗΤΑ ποιο κύκλωμα ελέγχει (Προμηθευτής/Πελάτης) - το ΙΔΙΟ
        // ΑΦΜ μπορεί κάλλιστα να υπάρχει ΚΑΙ ως τα δύο ταυτόχρονα στο
        // Soft1, δεν είναι σφάλμα, είναι δύο ΞΕΧΩΡΙΣΤΕΣ εγγραφές.
        public static string ExecuteFindTraderByAfmAndSodType(XSupport xSupport, string afm, int sodType)
        {
            if (string.IsNullOrWhiteSpace(afm))
                return JsonConvert.SerializeObject(new { found = false });

            int company = xSupport.ConnectionInfo.CompanyId;
            XTable t = xSupport.GetSQLDataSet(
                "SELECT TOP 1 TRDR, CODE, NAME, AFM FROM TRDR " +
                "WHERE COMPANY=:1 AND AFM=:2 AND SODTYPE=:3 AND ISACTIVE=1",
                company, afm, sodType);

            if (t == null || t.Count == 0)
                return JsonConvert.SerializeObject(new { found = false });

            int trdrId = Convert.ToInt32(t.Current["TRDR"]);
            string name = t.Current["NAME"] == DBNull.Value ? null : t.Current["NAME"].ToString();
            string code = t.Current["CODE"] == DBNull.Value ? null : t.Current["CODE"].ToString();
            string objectName = TraderObjectsBySodType.TryGetValue(sodType, out var obj) ? obj : null;

            return JsonConvert.SerializeObject(new { found = true, trdrId, sodType, name, code, objectName });
        }

        // ══════════════════════════════════════════════════════════════════
        // Στάδιο 3γ - ΑΑΔΕ auto-create όταν ΔΕΝ βρέθηκε συναλλασσόμενος, ΝΕΟ
        // 16/08. Reuse ΑΥΤΟΥΣΙΟ pattern από S1DocReader.Soft1.Soft1Bridge
        // (GetAfmDataFromAade/CreateTrader, proven) - ΜΟΝΟ SUPPLIER (SODTYPE
        // 12), ίδιο σκεπτικό με το S1DocReader (default/μόνο mode εκεί ήταν
        // Suppliers - το DR διαβάζει έγγραφα ΠΟΥ ΕΛΑΒΕ ο χειριστής, ο
        // εκδότης είναι λογικά πάντα προμηθευτής σε αυτό το context).
        // ══════════════════════════════════════════════════════════════════

        // CODE πρόταση - ΡΗΤΗ οδηγία χρήστη 16/08: "θα κοιτάς πώς στο
        // SODTYPE έχουν ήδη ανοιχτεί εγγραφές και θα βγάζεις το πιο
        // συμβατό συμπέρασμα" - ΟΧΙ σταθερή υπόθεση μορφής.
        //
        // Ο πυρήνας είναι ΕΠΙΣΗΜΗ τεχνική από το BlackBook (X.SQL
        // reference, Example 1) - επιβεβαιωμένο ζωντανά 16/08 από τον
        // χρήστη σε πραγματικό παράδειγμα:
        //   SELECT ISNULL((SELECT MAX(ISNULL(TRY_PARSE(CODE AS INT),0))
        //   FROM ...),0) + 1
        // Το TRY_PARSE αγνοεί ΜΕ ΑΣΦΑΛΕΙΑ μη-αριθμητικούς κωδικούς
        // (επιστρέφει NULL, το MAX τους προσπερνάει) - λύνει καθαρά το
        // πρόβλημα ενός varchar CODE που ΔΕΝ ταξινομείται αριθμητικά
        // ("9" > "10" αλφαβητικά). Πάνω σε αυτό, δεύτερο πέρασμα ελέγχει
        // ΜΟΝΟ το zero-padding format (δείγμα των 50 πιο πρόσφατων,
        // ORDER BY TRDR DESC - internal id, ΟΧΙ CODE) - ΑΝ όλοι οι
        // ΑΡΙΘΜΗΤΙΚΟΙ κωδικοί του δείγματος έχουν το ΙΔΙΟ μήκος, η
        // πρόταση γίνεται pad σε αυτό το μήκος, αλλιώς μένει "ωμός"
        // αριθμός. nextNum<=1 (κανένας προηγούμενος αριθμητικός κωδικός
        // στο SODTYPE) -> null, ασφαλέστερο να μην προτείνουμε παρά να
        // μαντέψουμε "1" λάθος.
        private static string SuggestNextTraderCode(XSupport xSupport, int sodType)
        {
            try
            {
                int company = xSupport.ConnectionInfo.CompanyId;

                XTable next = xSupport.GetSQLDataSet(
                    "SELECT ISNULL((SELECT MAX(ISNULL(TRY_PARSE(CODE AS INT),0)) " +
                    "FROM TRDR WHERE COMPANY=:1 AND SODTYPE=:2),0) + 1 AS NEXTCODE",
                    company, sodType);
                if (next == null || next.Count == 0) return null;
                long nextNum = Convert.ToInt64(next.Current["NEXTCODE"]);
                if (nextNum <= 1) return null;
                string nextStr = nextNum.ToString();

                XTable sample = xSupport.GetSQLDataSet(
                    "SELECT TOP 50 CODE FROM TRDR WHERE COMPANY=:1 AND SODTYPE=:2 ORDER BY TRDR DESC",
                    company, sodType);
                if (sample == null || sample.Count == 0) return nextStr;

                var numericCodes = new List<string>();
                DataTable dt = sample.CreateDataTable(true);
                foreach (DataRow row in dt.Rows)
                {
                    string c = row["CODE"] == DBNull.Value ? null : row["CODE"].ToString()?.Trim();
                    if (!string.IsNullOrEmpty(c) && c.All(char.IsDigit)) numericCodes.Add(c);
                }
                if (numericCodes.Count == 0) return nextStr;

                int width = numericCodes[0].Length;
                bool sameWidth = numericCodes.All(c => c.Length == width);
                return sameWidth && nextStr.Length <= width ? nextStr.PadLeft(width, '0') : nextStr;
            }
            catch (Exception ex)
            {
                DebugLog.Log("[dr] SuggestNextTraderCode EXCEPTION: " + ex);
                return null;
            }
        }

        // ── get_aade_data (LLM tool) - ΝΕΟ 18/08, ίδιο αίτημα με πιο πάνω.
        // Wraps το ExecuteGetAadeData (Κλήση 1) - καλείται ΜΟΝΟ αφού το
        // find_trader_by_afm επιβεβαιώσει ότι ΔΕΝ υπάρχει ήδη, ΠΡΙΝ την
        // ΤΕΛΙΚΗ δημιουργία (create_trader_from_aade) - βλ. system prompt
        // για την ΥΠΟΧΡΕΩΤΙΚΗ σειρά/επιβεβαίωση.
        public static readonly object GetAadeDataToolDefinition = new
        {
            name = "get_aade_data",
            description =
                "Φέρνει στοιχεία (επωνυμία/διεύθυνση/πόλη/ΔΟΥ/ΤΚ) από την " +
                "ΑΑΔΕ για ένα ΑΦΜ, ΚΑΙ προτεινόμενο επόμενο κωδικό - " +
                "χρησιμοποίησέ το ΜΟΝΟ αφού το find_trader_by_afm " +
                "επιβεβαίωσε ότι ΔΕΝ υπάρχει ήδη συναλλασσόμενος με αυτό " +
                "το ΑΦΜ/τύπο. Δείξε τα αποτελέσματα στον χειριστή και ζήτα " +
                "ΡΗΤΗ επιβεβαίωση (❓/> quick-reply) ΠΡΙΝ καλέσεις το " +
                "create_trader_from_aade.",
            input_schema = new
            {
                type = "object",
                properties = new
                {
                    afm = new { type = "string", description = "9-ψήφιο ΑΦΜ." },
                    sodType = new { type = "integer", description = "12=Προμηθευτής, 13=Πελάτης - ΥΠΟΧΡΕΩΤΙΚΟ, ό,τι διευκρίνισε/επιβεβαίωσε ο χειριστής." }
                },
                required = new[] { "afm", "sodType" }
            }
        };

        // Κλήση 1 - ΑΑΔΕ lookup (ίδιο ΑΥΤΟΥΣΙΟ pattern με Soft1Bridge.
        // GetAfmDataFromAade: CreateModule("GsisCmpAfmData")/GetTable(
        // "CMPAFMDATA")/InsertData/set AFM/PostData - προκαλεί το ίδιο το
        // Soft1 να κάνει το πραγματικό web service call στην ΑΑΔΕ, ΔΕΝ
        // υλοποιούμε εμείς HTTP προς την ΑΑΔΕ απευθείας). ΔΕΝ πετάει
        // exception σε αποτυχία - επιστρέφει success=false με μήνυμα, το
        // UI αφήνει τον χειριστή να συμπληρώσει χειροκίνητα.
        // sodType - ΝΕΟ 16/08 (πριν hardcoded 12/SUPPLIER) - το CREATEAADEAFM
        // command (index.html) το περνάει ρητά (12 default/Προμηθευτής,
        // 13 αν CUS/Πελάτης, βλ. εκεί) - ο file-based DR flow στέλνει
        // πάντα 12 (ίδια συμπεριφορά με πριν, βλ. σχόλιο section πάνω).
        public static string ExecuteGetAadeData(XSupport xSupport, string afm, int sodType)
        {
            if (string.IsNullOrWhiteSpace(afm))
                return JsonConvert.SerializeObject(new { success = false, message = "Λείπει το ΑΦΜ." });

            try
            {
                XModule obj = xSupport.CreateModule("GsisCmpAfmData");
                XTable ds = obj.GetTable("CMPAFMDATA");
                try
                {
                    obj.InsertData();
                    ds.Current["AFM"] = afm;
                    obj.PostData();

                    string name = ds.Current["NAME"] == DBNull.Value ? "" : ds.Current["NAME"].ToString();
                    string address = ds.Current["ADDRESS"] == DBNull.Value ? "" : ds.Current["ADDRESS"].ToString();
                    string city = ds.Current["CITY"] == DBNull.Value ? "" : ds.Current["CITY"].ToString();
                    string doy = ds.Current["DNAME"] == DBNull.Value ? "" : ds.Current["DNAME"].ToString();
                    string zip = ds.Current["ZIP"] == DBNull.Value ? "" : ds.Current["ZIP"].ToString();
                    string jobType = ds.Current["JOBTYPETRD"] == DBNull.Value ? "" : ds.Current["JOBTYPETRD"].ToString();

                    if (string.IsNullOrWhiteSpace(name))
                        return JsonConvert.SerializeObject(new
                        { success = false, message = "Η ΑΑΔΕ δεν επέστρεψε στοιχεία για το ΑΦΜ " + afm + "." });

                    string suggestedCode = SuggestNextTraderCode(xSupport, sodType);

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        afm,
                        sodType,
                        name,
                        address,
                        city,
                        doy,
                        zip,
                        jobType,
                        suggestedCode
                    });
                }
                finally { ds.Dispose(); obj.Dispose(); }
            }
            catch (Exception ex)
            {
                DebugLog.Log("[dr] ExecuteGetAadeData EXCEPTION: " + ex);
                return JsonConvert.SerializeObject(new
                { success = false, message = "Αποτυχία αναζήτησης ΑΑΔΕ: " + ex.Message });
            }
        }

        // ── create_trader_from_aade (LLM tool) - ΝΕΟ 18/08, ίδιο αίτημα με
        // πιο πάνω. ΑΝΕΠΙΣΤΡΕΠΤΗ ενέργεια (πραγματική δημιουργία εγγραφής
        // TRDR) - χρησιμοποίησέ το ΜΟΝΟ αφού ο χειριστής επιβεβαίωσε ΡΗΤΑ
        // σε ΕΠΟΜΕΝΟ μήνυμα ΜΕΤΑ το get_aade_data (βλ. system prompt).
        public static readonly object CreateTraderFromAadeToolDefinition = new
        {
            name = "create_trader_from_aade",
            description =
                "Δημιουργεί ΝΕΟ συναλλασσόμενο (Προμηθευτή/Πελάτη) με τα " +
                "στοιχεία που έφερε το get_aade_data - χρησιμοποίησέ το " +
                "ΜΟΝΟ αφού ο χειριστής επιβεβαίωσε ΡΗΤΑ σε ΕΠΟΜΕΝΟ μήνυμα " +
                "(ΠΟΤΕ στο ίδιο turn με το get_aade_data). Μετά την " +
                "επιτυχία, δώσε κλικαριστό link " +
                "'[άνοιγμα](trader:OBJECTNAME:trdrId)' - χρησιμοποίησε " +
                "ΑΚΡΙΒΩΣ τα objectName/trdrId του αποτελέσματος (ΞΕΧΩΡΙΣΤΟ " +
                "scheme από τα 'doc:' links των παραστατικών).",
            input_schema = new
            {
                type = "object",
                properties = new
                {
                    afm = new { type = "string" },
                    name = new { type = "string", description = "Επωνυμία (από το get_aade_data)." },
                    code = new { type = "string", description = "Κωδικός - το suggestedCode του get_aade_data, ΕΚΤΟΣ αν ο χειριστής ζήτησε ρητά άλλον." },
                    sodType = new { type = "integer", description = "12=Προμηθευτής, 13=Πελάτης." },
                    address = new { type = "string" },
                    city = new { type = "string" },
                    doy = new { type = "string" },
                    zip = new { type = "string" },
                    jobType = new { type = "string" }
                },
                required = new[] { "afm", "name", "code", "sodType" }
            }
        };

        // Κλήση 2 - δημιουργία Προμηθευτή/Πελάτη (ίδιο ΑΥΤΟΥΣΙΟ pattern με
        // Soft1Bridge.CreateTrader) - ΜΟΝΟ SODTYPE 12/13 (ΟΧΙ 15/16 -
        // χρεώστης/πιστωτής δημιουργία ΔΕΝ ζητήθηκε, ΕΠΙΤΗΔΕΣ εκτός
        // σκοπείου προς το παρόν - βλ. TraderObjectsBySodType). Duplicate
        // check ΠΡΙΝ το insert ΜΟΝΟ αν ο χειριστής άλλαξε το προτεινόμενο
        // CODE - ρητή οδηγία χρήστη 16/08 ("να μη φας τα μούτρα σου
        // τσάμπα"), ΤΩΡΑ σωστά scoped στο ΙΔΙΟ sodType (πριν hardcoded 12).
        public static string ExecuteCreateTraderFromAade(XSupport xSupport, JObject input)
        {
            string afm = (string)input["afm"];
            string name = (string)input["name"];
            string code = (string)input["code"];
            int sodType = (int?)input["sodType"] ?? 12;
            if (string.IsNullOrWhiteSpace(afm) || string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(code))
                throw new Exception("Λείπουν στοιχεία (ΑΦΜ/επωνυμία/κωδικός) για δημιουργία συναλλασσόμενου.");
            if (sodType != 12 && sodType != 13)
                throw new Exception("Αυτόματη δημιουργία υποστηρίζεται μόνο για Προμηθευτή/Πελάτη (SODTYPE 12/13).");

            string objectName = TraderObjectsBySodType[sodType]; // ασφαλές - ήδη επικυρώθηκε 12/13 πάνω
            int company = xSupport.ConnectionInfo.CompanyId;

            XTable dup = xSupport.GetSQLDataSet(
                "SELECT COUNT(*) AS CNT FROM TRDR WHERE COMPANY=:1 AND SODTYPE=:2 AND CODE=:3",
                company, sodType, code);
            if (dup != null && dup.Count > 0 && Convert.ToInt32(dup.Current["CNT"]) > 0)
                throw new Exception($"Ο κωδικός \"{code}\" υπάρχει ήδη σε άλλον συναλλασσόμενο (ίδιο κύκλωμα) - διάλεξε άλλον.");

            XModule m = xSupport.CreateModule(objectName);
            XTable TRDR = m.GetTable("TRDR");
            try
            {
                m.InsertData();
                TRDR.Current["CODE"] = code;
                TRDR.Current["AFM"] = afm;
                TRDR.Current["NAME"] = name;

                string address = (string)input["address"];
                string city = (string)input["city"];
                string doy = (string)input["doy"];
                string zip = (string)input["zip"];
                string jobType = (string)input["jobType"];
                if (!string.IsNullOrEmpty(address)) TRDR.Current["ADDRESS"] = address;
                if (!string.IsNullOrEmpty(city)) TRDR.Current["CITY"] = city;
                if (!string.IsNullOrEmpty(doy)) TRDR.Current["IRSDATA"] = doy;
                if (!string.IsNullOrEmpty(zip)) TRDR.Current["ZIP"] = zip;
                if (!string.IsNullOrEmpty(jobType)) TRDR.Current["JOBTYPETRD"] = jobType;

                int trdrId = m.PostData();
                if (trdrId <= 0)
                    throw new Exception("Αποτυχία δημιουργίας συναλλασσόμενου (PostData επέστρεψε 0).");

                DebugLog.Log($"[dr] ExecuteCreateTraderFromAade OK -> trdrId={trdrId} sodType={sodType} code={code} afm={afm}");

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    trdrId,
                    sodType,
                    objectName,
                    code,
                    name
                });
            }
            finally { TRDR.Dispose(); m.Dispose(); }
        }

        // ── show_contact_results (LLM tool) - ΝΕΟ 18/08, ρητό αίτημα
        // χρήστη ("να φτιάξουμε μια εντολή που θα δουλεύει και ως κείμενο
        // περιγραφικά ... θα του επιστρέφει σε modal τα στοιχεία της
        // επαφής"). ΙΔΙΟ idiom "Claude υπολογίζει, το tool ΜΕΤΑΦΕΡΕΙ" με
        // show_courier_documents/show_calendar_entries - ο Claude βρίσκει
        // τις επαφές μόνος του (query_data στο PRSN, βλ.
        // [[soft1-prsn-contacts-table]]/BuildSystemPrompt, + προαιρετικά
        // search_outlook_contacts) και ΑΠΛΑ τις περνάει εδώ - το tool ΔΕΝ
        // ψάχνει τίποτα μόνο του, μόνο πυροδοτεί το modal στο κύριο
        // παράθυρο. Διαθέσιμο σε general/browserMode/emailMode (ΙΔΙΑ
        // branches με send_email/reply_email, βλ. JarvisAgentClient).
        public static readonly object ShowContactResultsToolDefinition = new
        {
            name = "show_contact_results",
            description =
                "Εμφανίζει ΣΥΓΚΕΚΡΙΜΕΝΕΣ επαφές (που ΗΔΗ βρήκες μέσω " +
                "query_data στο PRSN, ΚΑΙ προαιρετικά search_outlook_" +
                "contacts) ΑΠΕΥΘΕΙΑΣ σε modal - χρησιμοποίησέ το ΟΤΑΝ ο " +
                "χειριστής ΡΗΤΑ ζητήσει να βρεις/δεις στοιχεία επαφής " +
                "(π.χ. \"βρες μου τα στοιχεία του Γιώργου Παπαδόπουλου\", " +
                "\"ψάξε επαφή με τηλέφωνο 210...\", \"ποιο είναι το email " +
                "της Μαρίας\"). ΜΗΝ το καλέσεις όταν η αναζήτηση έγινε " +
                "ΜΟΝΟ ως ενδιάμεσο βήμα πριν send_email/reply_email με " +
                "όνομα παραλήπτη (εκεί ο χειριστής βλέπει το email ήδη " +
                "μέσα στο draft - modal εκεί θα ήταν περιττό). ΜΗΝ " +
                "απαντήσεις ΜΕ ΛΙΣΤΑ μέσα στο chat - ο χειριστής βλέπει το " +
                "αποτέλεσμα ΑΠΕΥΘΕΙΑΣ στο modal, απλά επιβεβαίωσε ΣΥΝΤΟΜΑ " +
                "(π.χ. \"Βρήκα 2 επαφές, δες τα στοιχεία δίπλα.\").",
            input_schema = new
            {
                type = "object",
                properties = new
                {
                    contacts = new
                    {
                        type = "array",
                        description = "Οι επαφές που θα εμφανιστούν - ΑΚΡΙΒΩΣ αυτές, καμία επιπλέον επεξεργασία/φιλτράρισμα από το backend.",
                        items = new
                        {
                            type = "object",
                            properties = new
                            {
                                name = new { type = "string", description = "Ονοματεπώνυμο (PRSN.NAME/NAME2 ή Outlook displayName)." },
                                email = new { type = "string", description = "Κύριο email, αν υπάρχει." },
                                email2 = new { type = "string", description = "Δεύτερο email, αν υπάρχει (π.χ. PRSN.EMAIL1)." },
                                phone = new { type = "string", description = "Σταθερό τηλέφωνο, αν υπάρχει." },
                                mobile = new { type = "string", description = "Κινητό, αν υπάρχει." },
                                address = new { type = "string", description = "Διεύθυνση/πόλη, αν υπάρχει (ελεύθερο κείμενο)." },
                                title = new { type = "string", description = "Θέση/τμήμα/εταιρία, αν υπάρχει (π.χ. PRSN.SOTITLENAME ή Outlook jobTitle/companyName)." },
                                source = new { type = "string", description = "'soft1' ή 'outlook' - από πού βρέθηκε η επαφή." }
                            },
                            required = new[] { "name", "source" }
                        }
                    }
                },
                required = new[] { "contacts" }
            }
        };

        public static string ExecuteShowContactResults(JObject input, Action<JArray> onShowContactResults)
        {
            var contacts = input?["contacts"] as JArray ?? new JArray();
            onShowContactResults?.Invoke(contacts);
            return JsonConvert.SerializeObject(new { success = true, count = contacts.Count });
        }

        // ── export_shown_table (LLM tool) - ΝΕΟ 19/08, ρητό αίτημα χρήστη:
        // "το κουμπί PDF στο παράθυρο της λίστας πρέπει να είναι οδηγία για
        // τον agent, όχι απλά κουμπί. Το ίδιο και για τα υπόλοιπα (CSV,
        // XLSX)". ΙΔΙΟ idiom "Claude/UI υπολογίζει, το tool ΜΕΤΑΦΕΡΕΙ" με
        // show_contact_results - το tool ΔΕΝ κάνει καμία δουλειά, απλά
        // προωθεί το format στο JS (window.triggerTableExport, index.html)
        // που τρέχει ΤΟΝ ΙΔΙΟ κώδικα με το κλικ στο κουμπί, πάνω στο
        // ΤΕΛΕΥΤΑΙΟ πίνακα που ΗΔΗ δείχτηκε - ΚΑΜΙΑ ξαναδημιουργία
        // δεδομένων. v1: ΜΟΝΟ κύριο chat (ΟΧΙ κουρτίνες ακόμα).
        public static readonly object ExportShownTableToolDefinition = new
        {
            name = "export_shown_table",
            description =
                "Εξάγει σε αρχείο (CSV/Excel/PDF) τον ΠΙΟ ΠΡΟΣΦΑΤΟ πίνακα " +
                "που ΗΔΗ έδειξες σε αυτή τη συζήτηση - ΙΔΙΟ αποτέλεσμα με " +
                "το να πατήσει ο χειριστής το αντίστοιχο κουμπί (Excel/" +
                "CSV/PDF) κάτω από τον πίνακα. Χρησιμοποίησέ το ΟΤΑΝ ο " +
                "χειριστής ζητήσει να αποθηκευτεί/εξαχθεί ΩΣ ΑΡΧΕΙΟ ό,τι " +
                "ΜΟΛΙΣ δείχτηκε (π.χ. \"κάν' το PDF\", \"θέλω το σε " +
                "Excel\") - ΜΗΝ του πεις να πατήσει το κουμπί μόνος του, " +
                "ΚΑΛΕΣΕ το tool. ΜΟΝΟ αν ΗΔΗ έδειξες πίνακα ΣΕ ΑΥΤΟ το " +
                "turn ή σε ΠΡΟΗΓΟΥΜΕΝΟ ΚΟΝΤΙΝΟ turn της ΙΔΙΑΣ συζήτησης - " +
                "αν δεν υπάρχει πρόσφατος πίνακας, πες το ΚΑΘΑΡΑ αντί να " +
                "καλέσεις το tool. ΝΕΟ 19/08 - προαιρετικό rowIndices: αν " +
                "ο χειριστής ζητήσει ΜΟΝΟ ΜΕΡΙΚΕΣ γραμμές (π.χ. \"μόνο " +
                "τους πρώτους 10\", \"μόνο αυτούς πάνω από 1000€\"), ΕΣΥ " +
                "υπολογίζεις ΠΟΙΕΣ γραμμές (0-based δείκτες ΣΤΙΣ γραμμές " +
                "δεδομένων του πίνακα που ΜΟΛΙΣ έδειξες, ΟΧΙ την επικεφαλίδα " +
                "- η πρώτη γραμμή δεδομένων είναι δείκτης 0) - ΗΔΗ ξέρεις " +
                "τα δεδομένα, τα έγραψες εσύ. ΧΩΡΙΣ rowIndices -> ΟΛΕΣ οι " +
                "γραμμές.",
            input_schema = new
            {
                type = "object",
                properties = new
                {
                    format = new
                    {
                        type = "string",
                        @enum = new[] { "csv", "xlsx", "pdf" },
                        description = "Μορφή αρχείου."
                    },
                    rowIndices = new
                    {
                        type = "array",
                        items = new { type = "integer" },
                        description = "ΠΡΟΑΙΡΕΤΙΚΟ - 0-based δείκτες ΓΡΑΜΜΩΝ ΔΕΔΟΜΕΝΩΝ " +
                            "(ΟΧΙ header) του τελευταίου πίνακα, ΜΟΝΟ αν ο χειριστής " +
                            "ζήτησε ΜΕΡΙΚΕΣ γραμμές. Χωρίς αυτό -> όλες οι γραμμές."
                    }
                },
                required = new[] { "format" }
            }
        };

        // ΔΙΟΡΘΩΘΗΚΕ 19/08 - ζωντανή διευκρίνιση χρήστη ("σε εκείνο το
        // σημείο έχει φτιάξει το αρχείο και ξέρει και σε ποιο path, βήμα
        // 2 το επισυνάπτει στο email"): async, ΠΕΡΙΜΕΝΕΙ το πραγματικό
        // path (ΟΧΙ fire-and-forget) - το tool_result ΤΩΡΑ έχει το path
        // ώστε ο Jarvis να μπορεί ΜΕΤΑ να καλέσει send_email με
        // attachmentFilePath=path (πραγματικά bytes, ΟΧΙ κείμενο).
        // rowIndices - ΝΕΟ 19/08, ρητό αίτημα χρήστη ("επιλογή γραμμών
        // μέσω οδηγίας") - int[] (ΟΧΙ JArray) στο delegate ώστε το
        // JarvisShell.xaml.cs να μην χρειάζεται να ξέρει τίποτα για
        // Newtonsoft, απλό .NET array αρκετό για JSON.stringify.
        public static async Task<string> ExecuteExportShownTable(
            JObject input, Func<string, int[], Task<string>> onExportShownTable)
        {
            string format = input?["format"]?.ToString();
            if (format != "csv" && format != "xlsx" && format != "pdf")
                throw new Exception("Άγνωστη μορφή εξαγωγής - δεκτά: csv/xlsx/pdf.");
            int[] rowIndices = (input?["rowIndices"] as JArray)?.Select(t => t.ToObject<int>()).ToArray();
            string path = onExportShownTable != null ? await onExportShownTable(format, rowIndices) : null;
            if (string.IsNullOrWhiteSpace(path))
                throw new Exception("Δεν υπάρχει πρόσφατος πίνακας στη συζήτηση για εξαγωγή (ή απέτυχε η αποθήκευση).");
            return JsonConvert.SerializeObject(new { success = true, format, path });
        }

        // Στάδιο 3β - ιστορικό σειράς + carry-over υποψήφια (Έργο/
        // Εγκατάσταση/Υποκατάστημα), ΝΕΟ 16/08. SQL/λογική disambiguation
        // ΡΗΤΑ δοσμένα από τον χρήστη ζωντανά: SELECT SERIES,FINCODE,
        // SOSOURCE FROM FINDOC JOIN TRDR ON TRDR.TRDR=FINDOC.TRDR WHERE
        // TRDR.AFM=... (εδώ πάνω σε trdrId αντί για AFM - το ΗΔΗ έχουμε από
        // το ExecuteFindTraderByAfm, ισοδύναμο αποτέλεσμα, πιο άμεσο FK
        // join) - "αν επιστρέψει περισσότερες από μία, θα συγκρίνεις τον
        // αριθμό του παραστατικού με το FINCODE και θα καταλάβεις πού
        // ταιριάζει" -> εδώ: ομαδοποίηση ανά (SERIES,SOSOURCE), best match
        // = η ομάδα της οποίας το FINCODE μοιράζεται το ΙΔΙΟ πρόθεμα με τον
        // docType που αναγνώρισε το AI (π.χ. "ΤΔΑ..." vs docType="ΤΔΑ").
        // Αν ΔΕΝ βρεθεί ΑΚΡΙΒΩΣ μία τέτοια ομάδα -> fallback στην πιο
        // πρόσφατη χρησιμοποιημένη σειρά. ΚΑΘΑΡΑ ΠΛΗΡΟΦΟΡΙΑΚΟ προς το παρόν
        // (Στάδιο 3, καμία καταχώρηση δεν γίνεται ακόμα) - το carry-over
        // (PRJC/INST/TRDBRANCH) απλά ΕΜΦΑΝΙΖΕΤΑΙ, η πραγματική ερώτηση στον
        // χειριστή ("θες να περάσουν;") μπαίνει στο Στάδιο καταχώρησης.
        // ΑΦΜ της δικής μας εταιρίας - χρειάζεται από το ExtractDocumentLinesAsync
        // (JarvisAgentClient) για τον κανόνα διαχωρισμού εκδότη/παραλήπτη στο
        // prompt (ίδιο ΑΥΤΟΥΣΙΟ σκεπτικό με Soft1Bridge.GetCompanyAfm).
        public static string GetCompanyAfm(XSupport xSupport)
        {
            try
            {
                XTable t = xSupport.GetSQLDataSet(
                    "SELECT AFM FROM COMPANY WHERE COMPANY=:1", xSupport.ConnectionInfo.CompanyId);
                if (t == null || t.Count == 0) return null;
                return t.Current["AFM"] == DBNull.Value ? null : t.Current["AFM"].ToString();
            }
            catch (Exception ex)
            {
                DebugLog.Log("[dr] GetCompanyAfm EXCEPTION: " + ex);
                return null;
            }
        }

        // ══════════════════════════════════════════════════════════════════
        // Στάδιο 5 (#22) - duplicate-check ΠΡΙΝ την καταχώρηση, ΝΕΟ 16/08.
        // ΡΗΤΗ οδηγία χρήστη ζωντανά: "πριν φτάσουμε στην καταχώρηση πρέπει
        // να ελέγξουμε αν το παραστατικό έχει ήδη ξανανέβει" - ψάχνει ΔΥΟ
        // πηγές (FINDOC απευθείας + TRDTRN - "μπορεί να το λέει διαφορετικά"
        // το FINDOC column εκεί, επιβεβαιώθηκε στο schema: ΤΟ TRDTRN ΕΧΕΙ
        // ΚΑΙ ΑΥΤΟ στήλη "FINDOC" ΙΔΙΟ όνομα - FK πίσω στο ΙΔΙΟ FINDOC.FINDOC.
        // ΔΕΝ έχει INSDATE/REMARKS στο TRDTRN - μόνο TRNDATE/COMMENTS/
        // FINCODE/TRDR/SOSOURCE εκεί, δεν το μαντέψαμε, επιβεβαιώθηκε στο
        // schema CSV). Ταύτιση FINCODE (LIKE, περιέχει τον αριθμό που
        // αναγνώρισε το AI - ανεκτικό σε διαφορές πρόθεμα/zero-padding) ΣΕ
        // ΣΥΝΔΥΑΣΜΟ ΜΕ ΤΗΝ ΗΜΕΡΟΜΗΝΙΑ (μόνο η ημερομηνία, ΟΧΙ ώρα) - και τα
        // δύο ΑΥΣΤΗΡΑ μαζί, ρητή οδηγία χρήστη - η σύγκριση ημερομηνίας
        // γίνεται ΣΕ C# (όχι SQL) μιας και δεν ξέρουμε εκ των προτέρων τη
        // μορφή που θα γράψει το AI την ημερομηνία στο extraction JSON.
        // ══════════════════════════════════════════════════════════════════
        private sealed class DuplicateCandidate
        {
            public int FinDoc;
            public string FinCode;
            public DateTime? DocDate;
            public int SoSource;
        }

        private static DateTime? ParseFlexibleDate(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;
            string[] formats = { "dd/MM/yyyy", "d/M/yyyy", "dd/M/yyyy", "d/MM/yyyy",
                "yyyy-MM-dd", "dd-MM-yyyy", "dd.MM.yyyy", "d.M.yyyy" };
            if (DateTime.TryParseExact(raw.Trim(), formats, CultureInfo.InvariantCulture,
                DateTimeStyles.None, out DateTime dt))
                return dt;
            if (DateTime.TryParse(raw.Trim(), CultureInfo.InvariantCulture, DateTimeStyles.None, out dt))
                return dt;
            return null;
        }

        public static string ExecuteCheckDuplicateDocument(
            XSupport xSupport, int trdrId, string docNumber, string docDateRaw)
        {
            // Χωρίς αριθμό/ημερομηνία δεν μπορούμε να ελέγξουμε αξιόπιστα -
            // ασφαλέστερο να ΜΗΝ εμποδίσουμε την καταχώρηση παρά να δώσουμε
            // ψευδές "δεν βρέθηκε" με χαλαρά κριτήρια.
            DateTime? targetDate = ParseFlexibleDate(docDateRaw);
            if (string.IsNullOrWhiteSpace(docNumber) || targetDate == null || trdrId <= 0)
                return JsonConvert.SerializeObject(new { found = false });

            int company = xSupport.ConnectionInfo.CompanyId;
            string likePattern = "%" + docNumber.Trim() + "%";
            var candidates = new List<DuplicateCandidate>();

            try
            {
                XTable t1 = xSupport.GetSQLDataSet(
                    "SELECT TOP 10 FINDOC, FINCODE, INSDATE, SOSOURCE FROM FINDOC " +
                    "WHERE COMPANY=:1 AND TRDR=:2 AND FINCODE LIKE :3 AND ISCANCEL=0",
                    company, trdrId, likePattern);
                if (t1 != null && t1.Count > 0)
                {
                    DataTable dt = t1.CreateDataTable(true);
                    foreach (DataRow row in dt.Rows)
                    {
                        candidates.Add(new DuplicateCandidate
                        {
                            FinDoc = Convert.ToInt32(row["FINDOC"]),
                            FinCode = row["FINCODE"] == DBNull.Value ? null : row["FINCODE"].ToString(),
                            DocDate = row["INSDATE"] == DBNull.Value ? (DateTime?)null : Convert.ToDateTime(row["INSDATE"]),
                            SoSource = Convert.ToInt32(row["SOSOURCE"])
                        });
                    }
                }
            }
            catch (Exception ex) { DebugLog.Log("[dr] ExecuteCheckDuplicateDocument FINDOC EXCEPTION: " + ex); }

            try
            {
                XTable t2 = xSupport.GetSQLDataSet(
                    "SELECT TOP 10 FINDOC, FINCODE, TRNDATE, SOSOURCE FROM TRDTRN " +
                    "WHERE COMPANY=:1 AND TRDR=:2 AND FINCODE LIKE :3",
                    company, trdrId, likePattern);
                if (t2 != null && t2.Count > 0)
                {
                    DataTable dt = t2.CreateDataTable(true);
                    foreach (DataRow row in dt.Rows)
                    {
                        candidates.Add(new DuplicateCandidate
                        {
                            FinDoc = Convert.ToInt32(row["FINDOC"]),
                            FinCode = row["FINCODE"] == DBNull.Value ? null : row["FINCODE"].ToString(),
                            DocDate = row["TRNDATE"] == DBNull.Value ? (DateTime?)null : Convert.ToDateTime(row["TRNDATE"]),
                            SoSource = Convert.ToInt32(row["SOSOURCE"])
                        });
                    }
                }
            }
            catch (Exception ex) { DebugLog.Log("[dr] ExecuteCheckDuplicateDocument TRDTRN EXCEPTION: " + ex); }

            var match = candidates.FirstOrDefault(c => c.DocDate.HasValue && c.DocDate.Value.Date == targetDate.Value.Date);
            if (match == null)
                return JsonConvert.SerializeObject(new { found = false });

            string objectName = DocumentObjectsBySosource.TryGetValue(match.SoSource, out var docInfo) ? docInfo.ObjectName : null;

            return JsonConvert.SerializeObject(new
            {
                found = true,
                findoc = match.FinDoc,
                fincode = match.FinCode,
                sosource = match.SoSource,
                objectName // null -> άγνωστο SOSOURCE, το UI δείχνει μήνυμα χωρίς clickable link
            });
        }

        // Στάδιο 4 - αντιστοίχιση εξαγμένων γραμμών με είδη ΤΟΥ ΔΙΚΟΥ ΜΑΣ
        // καταλόγου, μέσω MTRSUPCODE (κωδικός ΤΟΥ ΕΚΔΟΤΗ -> δικό μας MTRL) -
        // ίδιο ΑΥΤΟΥΣΙΟ pattern με Soft1Bridge.FindItemBySupplierCode. ΝΕΟ
        // 16/08. ΚΑΘΑΡΑ read-only - ΔΕΝ γράφει τίποτα, μόνο επισημαίνει
        // matched/unmatched για το review. JOIN με MTRUNIT για το SHORTCUT
        // (M.MTRUNIT1 είναι FK σε MTRUNIT.MTRUNIT, ΟΧΙ κείμενο μονάδας -
        // επιβεβαιωμένο στο schema, ΔΕΝ το μαντέψαμε).
        public static string ExecuteMatchExtractedItems(XSupport xSupport, int trdrId, JArray lineItems)
        {
            int company = xSupport.ConnectionInfo.CompanyId;
            var results = new JArray();

            foreach (var lineToken in lineItems ?? new JArray())
            {
                JObject line = lineToken as JObject ?? new JObject();
                string supplierCode = line["code"]?.ToString();

                JObject matched = null;
                if (!string.IsNullOrWhiteSpace(supplierCode))
                {
                    try
                    {
                        XTable t = xSupport.GetSQLDataSet(
                            "SELECT TOP 1 M.MTRL, M.CODE, M.NAME, U.SHORTCUT AS UNITNAME " +
                            "FROM MTRSUPCODE S " +
                            "INNER JOIN MTRL M ON M.MTRL = S.MTRL " +
                            "LEFT JOIN MTRUNIT U ON U.COMPANY = M.COMPANY AND U.MTRUNIT = M.MTRUNIT1 " +
                            "WHERE S.COMPANY=:1 AND S.TRDR=:2 AND S.MTRSUPCODE=:3 AND S.ISACTIVE=1 AND M.ISACTIVE=1",
                            company, trdrId, supplierCode);
                        if (t != null && t.Count > 0)
                        {
                            matched = new JObject
                            {
                                ["mtrlId"] = Convert.ToInt32(t.Current["MTRL"]),
                                ["code"] = t.Current["CODE"] == DBNull.Value ? null : t.Current["CODE"].ToString(),
                                ["name"] = t.Current["NAME"] == DBNull.Value ? null : t.Current["NAME"].ToString(),
                                ["unit"] = t.Current["UNITNAME"] == DBNull.Value ? null : t.Current["UNITNAME"].ToString()
                            };
                        }
                    }
                    catch (Exception ex)
                    {
                        DebugLog.Log("[dr] ExecuteMatchExtractedItems lookup EXCEPTION (code=" + supplierCode + "): " + ex);
                    }
                }

                JObject row = (JObject)line.DeepClone();
                row["matched"] = matched; // null -> ΔΕΝ βρέθηκε mapping, το UI δείχνει ⚠
                results.Add(row);
            }

            return JsonConvert.SerializeObject(new { results });
        }

        private sealed class TraderDocHistoryRow
        {
            public int Series;
            public string FinCode;
            public int Sosource;
            public DateTime TrnDate;
            public int? Prjc;
            public int? Inst;
            public int? TrdBranch;
        }

        public static string ExecuteFindTraderSeriesHistory(XSupport xSupport, int trdrId, string docType)
        {
            int company = xSupport.ConnectionInfo.CompanyId;
            XTable t = xSupport.GetSQLDataSet(
                "SELECT SERIES, FINCODE, SOSOURCE, TRNDATE, PRJC, INST, TRDBRANCH " +
                "FROM FINDOC WHERE COMPANY=:1 AND TRDR=:2 AND ISCANCEL=0 ORDER BY TRNDATE DESC",
                company, trdrId);

            if (t == null || t.Count == 0)
                return JsonConvert.SerializeObject(new { hasHistory = false });

            var rows = new List<TraderDocHistoryRow>();
            DataTable dt = t.CreateDataTable(true);
            foreach (DataRow row in dt.Rows)
            {
                rows.Add(new TraderDocHistoryRow
                {
                    Series = Convert.ToInt32(row["SERIES"]),
                    FinCode = row["FINCODE"] == DBNull.Value ? "" : row["FINCODE"].ToString(),
                    Sosource = Convert.ToInt32(row["SOSOURCE"]),
                    TrnDate = Convert.ToDateTime(row["TRNDATE"]),
                    Prjc = row["PRJC"] == DBNull.Value ? (int?)null : Convert.ToInt32(row["PRJC"]),
                    Inst = row["INST"] == DBNull.Value ? (int?)null : Convert.ToInt32(row["INST"]),
                    TrdBranch = row["TRDBRANCH"] == DBNull.Value ? (int?)null : Convert.ToInt32(row["TRDBRANCH"]),
                });
            }

            // Ομαδοποίηση ανά (SERIES,SOSOURCE) - Latest = πιο πρόσφατη
            // εγγραφή ΜΕΣΑ σε κάθε ομάδα (δείγμα FINCODE + carry-over πεδία).
            var groups = rows
                .GroupBy(r => new { r.Series, r.Sosource })
                .Select(g => new
                {
                    g.Key.Series,
                    g.Key.Sosource,
                    Count = g.Count(),
                    Latest = g.OrderByDescending(r => r.TrnDate).First()
                })
                .OrderByDescending(g => g.Latest.TrnDate)
                .ToList();

            var matchByPrefix = !string.IsNullOrWhiteSpace(docType)
                ? groups.Where(g => !string.IsNullOrEmpty(g.Latest.FinCode) &&
                    g.Latest.FinCode.StartsWith(docType, StringComparison.OrdinalIgnoreCase)).ToList()
                : groups.Where(g => false).ToList();

            var best = matchByPrefix.Count == 1 ? matchByPrefix[0] : groups.FirstOrDefault();

            // Ονόματα σειρών (φιλικό display) - ΔΙΟΡΘΩΘΗΚΕ 16/08 (ζωντανό
            // bug, χρήστης εντόπισε): ο ΙΔΙΟΣ αριθμός SERIES μπορεί να
            // υπάρχει σε ΠΟΛΛΑ κυκλώματα (διαφορετικό SOSOURCE, διαφορετικό
            // NAME το καθένα) - το query ΠΡΙΝ ψάχνει μόνο "SERIES IN (...)"
            // χωρίς SOSOURCE, οπότε το dictionary seriesNames[series]=name
            // αντικαθιστούσε τυχαία με όνομα από ΛΑΘΟΣ κύκλωμα. Το κλειδί
            // είναι τώρα το ΖΕΥΓΟΣ (series, sosource) μαζί.
            var seriesSosourcePairs = groups.Select(g => (g.Series, g.Sosource)).Distinct().ToList();
            var seriesNames = new Dictionary<(int series, int sosource), string>();
            if (seriesSosourcePairs.Count > 0)
            {
                string seriesIdList = string.Join(",", seriesSosourcePairs.Select(p => p.Series).Distinct());
                string sosourceIdList = string.Join(",", seriesSosourcePairs.Select(p => p.Sosource).Distinct());
                XTable names = xSupport.GetSQLDataSet(
                    $"SELECT SERIES, SOSOURCE, NAME FROM SERIES WHERE COMPANY={company} " +
                    $"AND SERIES IN ({seriesIdList}) AND SOSOURCE IN ({sosourceIdList})");
                if (names != null && names.Count > 0)
                {
                    DataTable namesDt = names.CreateDataTable(true);
                    foreach (DataRow row in namesDt.Rows)
                    {
                        seriesNames[(Convert.ToInt32(row["SERIES"]), Convert.ToInt32(row["SOSOURCE"]))] =
                            row["NAME"] == DBNull.Value ? null : row["NAME"].ToString();
                    }
                }
            }

            var candidates = groups.Select(g => new
            {
                series = g.Series,
                sosource = g.Sosource,
                name = seriesNames.TryGetValue((g.Series, g.Sosource), out var n) ? n : null,
                count = g.Count,
                sampleFinCode = g.Latest.FinCode,
                lastUsed = g.Latest.TrnDate.ToString("yyyy-MM-dd")
            }).ToList();

            return JsonConvert.SerializeObject(new
            {
                hasHistory = true,
                candidates,
                bestGuess = best == null ? null : new { series = best.Series, sosource = best.Sosource },
                prjc = best?.Latest.Prjc,
                inst = best?.Latest.Inst,
                trdBranch = best?.Latest.TrdBranch
            });
        }

        // ══════════════════════════════════════════════════════════════════
        // Στάδιο 5 (#22) - καταχώρηση παραστατικού. ΝΕΟ 16/08.
        //
        // Line table ανά SOSOURCE - 100% ΕΠΙΒΕΒΑΙΩΜΕΝΟ ζωντανά 16/08 μέσω
        // Soft1 Web Services getObjectTables() (βλ. S1_HeaderLines_Mapping.xlsx):
        //  - SALDOC(1351)/PURDOC(1251): ITELINES (dbname=MTRLINES)
        //  - LINCUSDOC(1353)/LINSUPDOC(1253): LINLINES (ΟΧΙ SRVLINES - αρχική
        //    υπόθεση διορθώθηκε από το ζωντανό API response)
        //  - ITEITEDOC(5151): ITELINES
        // ΠΡΟΣΟΧΗ: μικτά παραστατικά με ΚΑΙ items ΚΑΙ υπηρεσίες μέσα σε
        // SALDOC/PURDOC (θα χρειαζόταν SRVLINES) ΔΕΝ υποστηρίζονται ακόμα σε
        // αυτή την πρώτη υλοποίηση - όλες οι matched γραμμές γράφονται σε
        // ITELINES/LINLINES ανάλογα το object. Εκτός σκοπείου: ASSLINES
        // (πάγια) και HEADERLINE (παραγόμενο είδος ITEITEDOC).
        //
        // Write mechanism - ΕΠΙΒΕΒΑΙΩΜΕΝΟ ζωντανό precedent από το ΙΔΙΟ
        // S1DocReader (χρήστης 16/08, CreateDocument): GetTable(...).Add() +
        // Current["FIELD"]=value ανά πεδίο + Current.Post() ΑΝΑ ΓΡΑΜΜΗ μέσα σε
        // loop, τελικό module.PostData() μία φορά στο τέλος. ΔΕΝ
        // χρησιμοποιούμε το SBSL COPY/PASTE μηχανισμό (obj.COPY/obj.PASTE) -
        // αν και υπάρχει ζωντανά στο XModule.NET SDK (επιβεβαιώθηκε via
        // reflection: Copy()/Paste()/LocateData()/InsertData()), ΔΕΝ έχουμε
        // κανένα επιβεβαιωμένο precedent για το πώς επεξεργάζεσαι/αλλάζεις
        // τις ΗΔΗ αντιγραμμένες γραμμές μετά το Paste (καμία μέθοδος
        // First/Next/EOF στο XTable.NET, σε αντίθεση με το SBSL idiom) -
        // ΕΠΙΤΗΔΕΣ δεν το μαντεύουμε. Αντ' αυτού: το "Strategy A" (template
        // παραστατικό) διαβάζει το ΙΣΤΟΡΙΚΟ προφίλ της γραμμής (INST/PRJC/
        // CNTR/BUSUNITS) με SQL directly πάνω στο MTRLINES (real dbname πίσω
        // από ITELINES/LINLINES/SRVLINES - επιβεβαιώθηκε ζωντανά μέσω
        // getObjectTables ότι όλα κάνουν map στο ΙΔΙΟ physical table) και μετά
        // γράφει ΝΕΑ γραμμή με το ΙΔΙΟ proven Add()/Post() μηχανισμό - ΔΕΝ
        // κάνει literal Copy/Paste. Το αποτέλεσμα είναι επιχειρησιακά το ίδιο
        // (ίδιοι κωδικοί/λογαριασμοί όπως το ιστορικό, νέες τιμές), απλά
        // χωρίς το αβέβαιο edit-after-paste βήμα.
        //
        // Matching coefficient (ζητήθηκε ρητά 16/08, "μήπως να φτιάξουμε
        // συντελεστή ταιριάσματος;") - συνδυάζει:
        //   0.5 × format-match κωδικού παραστατικού (σκελετός με ψηφία->#)
        //   0.35 × επικάλυψη ειδών (Jaccard, matched MTRL της τρέχουσας
        //          εξαγωγής έναντι γραμμών του candidate)
        //   0.15 × πρόσφατο (φθίνουσα συνάρτηση μηνών)
        // Threshold 0.3 - κάτω από αυτό θεωρείται "δεν βρέθηκε αξιόπιστο
        // ταίριασμα" (χρήστης 16/08: "όχι πάντα το πιο πρόσφατο").
        // ══════════════════════════════════════════════════════════════════

        private static readonly Dictionary<int, string> LineTableBySosource =
            new Dictionary<int, string>
            {
                [1351] = "ITELINES", // SALDOC
                [1251] = "ITELINES", // PURDOC
                [1353] = "LINLINES", // LINCUSDOC
                [1253] = "LINLINES", // LINSUPDOC
                [5151] = "ITELINES", // ITEITEDOC
            };

        // ΝΕΟ 16/08, ζητήθηκε ρητά: αντί για hardcoded C# ιδιότητες
        // (Inst/Prjc/Cntr/BusUnits σκόρπια σε if-statements), τα "extra"
        // πεδία που αξίζει να κουβαλάμε από το ιστορικό είναι ΜΙΑ comma-
        // delimited παράμετρος ανά PHYSICAL πίνακα (ΟΧΙ virtual name - το
        // SQL read πάει πάντα στο πραγματικό dbname, π.χ. MTRLINES πίσω από
        // ITELINES/LINLINES/SRVLINES - επιβεβαιωμένο ζωντανά μέσω
        // getObjectTables). Static v1 - όχι ακόμα live schema-discovery
        // (SELECT * + diff από default) - αυτό μένει ρητά για το #27 αν
        // χρειαστεί ποτέ πραγματική multi-tenant genericity. Επέκταση: απλά
        // πρόσθεσε ένα όνομα πεδίου στο CSV string, ΔΕΝ χρειάζεται άλλη
        // αλλαγή κώδικα.
        private static readonly Dictionary<string, string> CarryOverFieldsByPhysicalTable =
            new Dictionary<string, string>
            {
                ["MTRLINES"] = "INST,PRJC,CNTR,BUSUNITS",
            };

        private static string[] GetCarryOverFields(string physicalTable) =>
            CarryOverFieldsByPhysicalTable.TryGetValue(physicalTable, out string csv)
                ? csv.Split(',').Select(f => f.Trim()).Where(f => f.Length > 0).ToArray()
                : Array.Empty<string>();

        private static string FincodeSkeleton(string s) =>
            string.IsNullOrEmpty(s) ? "" : Regex.Replace(s, "[0-9]+", "#").ToUpperInvariant();

        private static string LeadingPrefix(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var m = Regex.Match(s, @"^[^\d]*");
            return m.Value.ToUpperInvariant();
        }

        private static double ScoreFormatMatch(string candidateFinCode, string currentDocNumber)
        {
            if (string.IsNullOrWhiteSpace(candidateFinCode) || string.IsNullOrWhiteSpace(currentDocNumber))
                return 0;
            if (FincodeSkeleton(candidateFinCode) == FincodeSkeleton(currentDocNumber))
                return 1.0;
            string prefA = LeadingPrefix(candidateFinCode), prefB = LeadingPrefix(currentDocNumber);
            if (!string.IsNullOrEmpty(prefA) && prefA == prefB)
                return 0.5;
            return 0;
        }

        private static double ScoreRecency(DateTime candidateDate)
        {
            double monthsAgo = (DateTime.Today - candidateDate).TotalDays / 30.0;
            return 1.0 / (1.0 + Math.Max(0, monthsAgo));
        }

        private sealed class MtrLineRow
        {
            public int MtrL;
            public double Qty1;
            public double Price;
            // Γενικό - ΟΧΙ πια fixed Inst/Prjc/Cntr/BusUnits ιδιότητες.
            // Key = όνομα πεδίου (από CarryOverFieldsByPhysicalTable),
            // Value = η τιμή του ΣΤΟ ιστορικό (null αν η γραμμή δεν είχε τιμή
            // εκεί - ΔΕΝ γράφεται τότε στη νέα γραμμή, βλ. write loop).
            public Dictionary<string, object> Extra = new Dictionary<string, object>();
        }

        // Διαβάζει τις γραμμές ΕΝΟΣ συγκεκριμένου FINDOC απευθείας από το
        // MTRLINES (physical table - ΟΧΙ virtual name ITELINES/LINLINES, το
        // GetSQLDataSet τρέχει raw SQL πάνω στο πραγματικό schema). Οι
        // "extra" στήλες διαβάζονται δυναμικά από
        // CarryOverFieldsByPhysicalTable["MTRLINES"] - πρόσθεσε πεδίο εκεί
        // αν χρειαστεί, ΔΕΝ χρειάζεται αλλαγή εδώ.
        private static List<MtrLineRow> ReadMtrLines(XSupport xSupport, int company, int findoc)
        {
            var rows = new List<MtrLineRow>();
            string[] extraFields = GetCarryOverFields("MTRLINES");
            string extraSelect = extraFields.Length > 0 ? ", " + string.Join(", ", extraFields) : "";
            try
            {
                XTable t = xSupport.GetSQLDataSet(
                    $"SELECT MTRL, QTY1, PRICE{extraSelect} FROM MTRLINES " +
                    "WHERE COMPANY=:1 AND FINDOC=:2", company, findoc);
                if (t != null && t.Count > 0)
                {
                    DataTable dt = t.CreateDataTable(true);
                    foreach (DataRow row in dt.Rows)
                    {
                        var line = new MtrLineRow
                        {
                            MtrL = Convert.ToInt32(row["MTRL"]),
                            Qty1 = row["QTY1"] == DBNull.Value ? 0 : Convert.ToDouble(row["QTY1"]),
                            Price = row["PRICE"] == DBNull.Value ? 0 : Convert.ToDouble(row["PRICE"]),
                        };
                        foreach (string f in extraFields)
                            line.Extra[f] = row[f] == DBNull.Value ? null : row[f];
                        rows.Add(line);
                    }
                }
            }
            catch (Exception ex) { DebugLog.Log($"[dr] ReadMtrLines EXCEPTION findoc={findoc}: " + ex); }
            return rows;
        }

        // ══════════════════════════════════════════════════════════════════
        // ΑΝΑΘΕΩΡΗΘΗΚΕ 16/08 (ζωντανό test + ρητή οδηγία χρήστη): "ο Jarvis
        // να αντιλαμβάνεται το PATTERN καταχώρησης ανά συναλλασσόμενο και να
        // βγάζει ΠΟΣΟΣΤΟ ΑΣΦΑΛΕΙΑΣ" - ΟΧΙ πια "πάρε την 1 καλύτερη candidate
        // εγγραφή μόνη της" (FindBestTemplate, ΑΝΤΙΚΑΤΑΣΤΑΘΗΚΕ). Νέα λογική:
        // κοιτάμε ΟΛΟ το pool ιστορικών εγγραφών ίδιου trader+series+sosource
        // και ελέγχουμε αν ΣΥΜΦΩΝΟΥΝ μεταξύ τους (πόσες έχουν ΑΚΡΙΒΩΣ 1
        // γραμμή ΚΑΙ μοιράζονται τον ΙΔΙΟ κωδικό είδους - "dominant" ομάδα).
        // Λίγα δείγματα ΔΕΝ μηδενίζουν τη σιγουριά, απλά τη μειώνουν
        // αναλογικά (sampleAdequacy) - ζητήθηκε ρητά "για πιο safe
        // συμπέρασμα βρίσκει πολλές εγγραφές".
        // ══════════════════════════════════════════════════════════════════
        private sealed class PatternAnalysis
        {
            public int SampleSize;           // σύνολο ιστορικών εγγραφών βρέθηκαν
            public int SingleLineSampleSize; // πόσες από αυτές έχουν ΑΚΡΙΒΩΣ 1 γραμμή
            public double Confidence;        // 0..1 - τελικό ποσοστό ασφάλειας
            public int? BestFindoc;
            public List<MtrLineRow> BestLines;
        }

        // ΣΗΜΑΝΤΙΚΗ ΣΗΜΕΙΩΣΗ 16/08 (ρητή οδηγία χρήστη): αυτό το confidence
        // ΔΕΝ είναι μόνο για το σημερινό interactive UI gate - είναι
        // σχεδιασμένο να ξαναχρησιμοποιηθεί ΑΥΤΟΥΣΙΟ αργότερα από ΕΝΑΝ
        // ΜΕΛΛΟΝΤΙΚΟ scheduler που θα καταχωρεί παραστατικά ΧΩΡΙΣ χειριστή
        // (πλήρως αυτόματο background job). Το threshold εδώ είναι το ΙΔΙΟ
        // "πόσο σίγουρος είμαι" που θα αποφασίζει τότε αν προχωράει μόνο του
        // ή περιμένει άνθρωπο - γι' αυτό μένει ΞΕΧΩΡΙΣΤΗ, ονομασμένη σταθερά
        // (όχι magic number μέσα στη ροή) και το AnalyzeTraderPattern
        // επιστρέφει πλήρες, αυτόνομο αντικείμενο (PatternAnalysis) που δεν
        // εξαρτάται από UI context - μπορεί να κληθεί απευθείας από
        // μελλοντικό scheduler χωρίς αλλαγές.
        private const double PATTERN_CONFIDENCE_THRESHOLD = 0.55; // ΠΡΟΤΑΣΗ v1 - tunable

        private static PatternAnalysis AnalyzeTraderPattern(
            XSupport xSupport, int company, int trdrId, int series, int sosource, string currentDocNumber)
        {
            var result = new PatternAnalysis();
            XTable t = xSupport.GetSQLDataSet(
                "SELECT TOP 20 FINDOC, FINCODE, TRNDATE FROM FINDOC " +
                "WHERE COMPANY=:1 AND TRDR=:2 AND SERIES=:3 AND SOSOURCE=:4 AND ISCANCEL=0 " +
                "ORDER BY TRNDATE DESC", company, trdrId, series, sosource);
            if (t == null || t.Count == 0) return result; // SampleSize=0, Confidence=0

            var candidates = new List<(int findoc, string fincode, DateTime trndate, List<MtrLineRow> lines)>();
            DataTable dt = t.CreateDataTable(true);
            foreach (DataRow row in dt.Rows)
            {
                int findoc = Convert.ToInt32(row["FINDOC"]);
                string fincode = row["FINCODE"] == DBNull.Value ? "" : row["FINCODE"].ToString();
                DateTime trndate = Convert.ToDateTime(row["TRNDATE"]);
                candidates.Add((findoc, fincode, trndate, ReadMtrLines(xSupport, company, findoc)));
            }
            result.SampleSize = candidates.Count;

            var singleLine = candidates.Where(c => c.lines.Count == 1).ToList();
            result.SingleLineSampleSize = singleLine.Count;
            if (singleLine.Count == 0) return result; // κανένα single-line pattern - Confidence=0

            // "Dominant" ομάδα - ο ΠΙΟ ΣΥΧΝΟΣ κωδικός είδους ανάμεσα στις
            // single-line ιστορικές εγγραφές (αυτό ΕΙΝΑΙ το πραγματικό
            // pattern του trader, όχι απλά η πιο πρόσφατη εγγραφή).
            var groups = singleLine.GroupBy(c => c.lines[0].MtrL).OrderByDescending(g => g.Count()).ToList();
            var dominant = groups[0];
            double consistencyRatio = (double)dominant.Count() / singleLine.Count;

            const int MIN_SAMPLE = 3;
            double sampleAdequacy = Math.Min(1.0, (double)singleLine.Count / MIN_SAMPLE);

            var best = dominant.OrderByDescending(c => ScoreFormatMatch(c.fincode, currentDocNumber))
                                .ThenByDescending(c => c.trndate).First();
            double formatOrRecency = Math.Max(
                ScoreFormatMatch(best.fincode, currentDocNumber), ScoreRecency(best.trndate));

            // Σταθμισμένο άθροισμα, κάθε όρος ανεξάρτητα ερμηνεύσιμος:
            //   0.5 × πόσο ΚΥΡΙΑΡΧΟ είναι το pattern μέσα στο pool
            //   0.3 × πόσο ΕΠΑΡΚΕΣ είναι το δείγμα (>=3 -> γεμίζει στο 1.0)
            //   0.2 × πόσο ΠΙΘΑΝΟ είναι ΑΥΤΟ το τρέχον PDF να ανήκει εδώ
            result.Confidence = 0.5 * consistencyRatio + 0.3 * sampleAdequacy + 0.2 * formatOrRecency;
            result.BestFindoc = best.findoc;
            result.BestLines = best.lines;
            return result;
        }

        // Strategy B - για ΕΝΑ matched είδος, ψάχνει το πιο αξιόπιστο
        // ιστορικό προφίλ (INST/PRJC/CNTR/BUSUNITS) ανάμεσα σε προηγούμενες
        // γραμμές ΤΟΥ ΙΔΙΟΥ trader + ΙΔΙΟΥ mtrl, σκοραρισμένο με format-match
        // του γονικού FINCODE (ζητήθηκε ρητά 16/08 - "ίδιος trader με τον
        // ίδιο κωδικό παραστατικού ως format").
        private static MtrLineRow FindItemHistoryProfile(
            XSupport xSupport, int company, int trdrId, int mtrlId, string currentDocNumber)
        {
            string[] extraFields = GetCarryOverFields("MTRLINES");
            string extraSelect = extraFields.Length > 0 ? string.Join(", ", extraFields.Select(f => "L." + f)) + ", " : "";
            try
            {
                XTable t = xSupport.GetSQLDataSet(
                    $"SELECT TOP 10 {extraSelect}F.FINCODE, F.TRNDATE " +
                    "FROM MTRLINES L INNER JOIN FINDOC F ON F.COMPANY=L.COMPANY AND F.FINDOC=L.FINDOC " +
                    "WHERE L.COMPANY=:1 AND F.TRDR=:2 AND L.MTRL=:3 AND F.ISCANCEL=0 " +
                    "ORDER BY F.TRNDATE DESC", company, trdrId, mtrlId);
                if (t == null || t.Count == 0) return null;

                DataTable dt = t.CreateDataTable(true);
                double bestScore = -1;
                DataRow best = null;
                foreach (DataRow row in dt.Rows)
                {
                    string fincode = row["FINCODE"] == DBNull.Value ? "" : row["FINCODE"].ToString();
                    double score = ScoreFormatMatch(fincode, currentDocNumber);
                    if (score > bestScore) { bestScore = score; best = row; }
                }
                if (best == null || bestScore < 0.3) return null;

                var line = new MtrLineRow { MtrL = mtrlId };
                foreach (string f in extraFields)
                    line.Extra[f] = best[f] == DBNull.Value ? null : best[f];
                return line;
            }
            catch (Exception ex)
            {
                DebugLog.Log($"[dr] FindItemHistoryProfile EXCEPTION mtrl={mtrlId}: " + ex);
                return null;
            }
        }

        private static double ParseInvariantDouble(JToken tok)
        {
            if (tok == null) return 0;
            string s = tok.ToString().Replace(",", ".").Trim();
            return double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out double d) ? d : 0;
        }

        // ΑΝΑΘΕΩΡΗΘΗΚΕ 16/08 - ζωντανό test αποκάλυψε ότι η αρχική υπόθεση
        // ("AUTONUMBER+FINCODEGENERATE μαζί -> auto, αλλιώς manual") ήταν
        // ΜΙΣΗ αλήθεια - ο χρήστης εξήγησε το ΠΡΑΓΜΑΤΙΚΟ Soft1 UI behavior,
        // ΤΡΕΙΣ περιπτώσεις (ίδιο idiom με το χειροκίνητο Manual Entry του
        // Soft1, αναπαράγουμε ΑΚΡΙΒΩΣ αυτό):
        //  - AUTONUMBER=1, FINCODEGENERATE=1: Soft1 φτιάχνει ΟΛΟΚΛΗΡΟ το
        //    FINCODE μόνο του - ο αναγνωρισμένος κωδικός πάει στο COMMENTS.
        //  - AUTONUMBER=1, FINCODEGENERATE=0: το Soft1 UI προσυμπληρώνει το
        //    FINCODE με ΜΟΝΟ το SERIES.CODE (π.χ. "ΤΠΥ") και περιμένει ο
        //    χειριστής να "κολλήσει" τον αριθμό με το χέρι - αναπαράγουμε
        //    ΑΥΤΟ: FINCODE = SERIES.CODE + " " + αριθμός. ΣΚΟΠΙΜΑ
        //    χρησιμοποιούμε το SERIES.CODE από το ΙΔΙΟ το Soft1 (ΟΧΙ το
        //    δικό μας AI-αναγνωρισμένο "τύπο") - ζωντανό crash έδειξε ότι
        //    όταν στέλνουμε ΔΙΚΟ ΜΑΣ text συνδυασμένο με τον αριθμό σε αυτή
        //    την περίπτωση, το Soft1 αλλοιώνει/πετάει κομμάτια.
        //  - AUTONUMBER=0: καμία αυτόματη συμπλήρωση - εμείς γράφουμε
        //    ολόκληρο τον κωδικό (τύπο+αριθμό) όπως τον αναγνωρίσαμε.
        // Κενό ανάμεσα σε CODE+αριθμό - ρητή επιλογή χρήστη 16/08 ("ίσως στο
        // μέλλον το κάνουμε παράμετρο" - ΔΕΝ έγινε ακόμα, hardcoded v1).
        // ΣΗΜΕΙΩΣΗ χρήστη 16/08: αυτά τα 3 cases είναι v1 - πιθανό στο
        // μέλλον να χρειαστεί switch πάνω σε ΠΕΡΙΣΣΟΤΕΡΟΥΣ συνδυασμούς
        // SERIES παραμέτρων (όχι μόνο AUTONUMBER/FINCODEGENERATE) - το enum
        // είναι σκόπιμα ξεχωριστό από το SQL query ώστε να προστεθούν νέα
        // cases χωρίς να αλλάξει η επάνω δομή (GetFincodeMode/callers).
        private enum FincodeMode { AutoFull, AutoPrefixOnly, Manual }

        private static (FincodeMode mode, string seriesCode) GetFincodeMode(
            XSupport xSupport, int company, int series, int sosource)
        {
            try
            {
                XTable t = xSupport.GetSQLDataSet(
                    "SELECT AUTONUMBER, FINCODEGENERATE, CODE FROM SERIES WHERE COMPANY=:1 AND SERIES=:2 AND SOSOURCE=:3",
                    company, series, sosource);
                if (t == null || t.Count == 0) return (FincodeMode.Manual, null); // άγνωστο -> ασφαλέστερο manual

                bool autonumber = Convert.ToInt32(t.Current["AUTONUMBER"]) != 0;
                bool fincodeGenerate = Convert.ToInt32(t.Current["FINCODEGENERATE"]) != 0;
                string code = t.Current["CODE"] == DBNull.Value ? null : t.Current["CODE"].ToString();

                if (autonumber && fincodeGenerate) return (FincodeMode.AutoFull, code);
                if (autonumber) return (FincodeMode.AutoPrefixOnly, code);
                return (FincodeMode.Manual, code);
            }
            catch (Exception ex)
            {
                DebugLog.Log($"[dr] GetFincodeMode EXCEPTION series={series} sosource={sosource}: " + ex);
                return (FincodeMode.Manual, null);
            }
        }

        // ΝΕΟ 16/08, ρητό αίτημα χρήστη - επόμενο βήμα στο ανοιχτό θέμα
        // αλλοίωσης ελληνικών (AutoFull/Manual modes, βλ. FincodeMode). Οι 5
        // γνωστοί τύποι παραστατικού (ίδιο enum με το AI extraction schema -
        // JarvisAgentClient.ExtractDocumentLinesAsync: "ΤΙΜ|ΤΔΑ|ΤΠΥ|ΔΑ|ΑΠΔ")
        // συντίθενται εδώ ΓΡΑΜΜΑ-ΓΡΑΜΜΑ από explicit Unicode code points
        // (\uXXXX), ΟΧΙ literal ελληνικό κείμενο μέσα στο .cs αρχείο -
        // αποκλείει ΟΠΟΙΟΔΗΠΟΤΕ πιθανό source-file encoding θέμα (ακόμα κι
        // αν το κύριο πρόβλημα αποδείχτηκε ζωντανά ότι είναι στο write-layer
        // του Softone.Lib, ΟΧΙ στην κατασκευή του string - βλ. σχόλιο στο
        // SetGreekSafeString/ExecuteRegisterDrDocument). Άγνωστος τύπος (δεν
        // ταιριάζει σε αυτούς τους 5) -> επιστρέφεται αυτούσιος, ΔΕΝ
        // μαντεύουμε άλλους.
        // ΣΗΜΕΙΩΣΗ υλοποίησης: αρχικά δοκιμάστηκε literal \uXXXX escape
        // sequences μέσα σε string literals (όπως ζητήθηκε ρητά) - ΔΕΝ ήταν
        // εφικτό να γραφτούν με αξιοπιστία (το ίδιο το εργαλείο/pipeline
        // μετέτρεπε αυτόματα το escape sequence στον αντίστοιχο χαρακτήρα
        // πριν καν φτάσει στο αρχείο - αδύνατο να διαφοροποιηθεί από το να
        // γραφτεί ο χαρακτήρας απευθείας). Αντ' αυτού: κατασκευή από
        // ΑΡΙΘΜΗΤΙΚΑ hex code points (int -> char), που είναι απλοί αριθμοί
        // (0x03A4 κλπ) - ΙΔΙΟΣ σκοπός (αποκλείει ΟΠΟΙΟΔΗΠΟΤΕ πιθανό source-
        // file encoding θέμα), χωρίς να χρειάζεται literal ελληνικό κείμενο
        // Ή escape sequence μέσα στο .cs αρχείο.
        private static string FromCodePoints(params int[] codePoints) =>
            new string(codePoints.Select(cp => (char)cp).ToArray());

        // Greek capital letters (Unicode block 0x0391-0x03A9): Α=0391
        // Δ=0394 Ι=0399 Μ=039C Π=03A0 Τ=03A4 Υ=03A5 - επιβεβαιωμένο από το
        // επίσημο Unicode Greek and Coptic block.
        private static readonly Dictionary<string, string> KnownDocTypesUnicodeSafe = new Dictionary<string, string>
        {
            ["ΤΙΜ"] = FromCodePoints(0x03A4, 0x0399, 0x039C), // Τ Ι Μ
            ["ΤΔΑ"] = FromCodePoints(0x03A4, 0x0394, 0x0391), // Τ Δ Α
            ["ΤΠΥ"] = FromCodePoints(0x03A4, 0x03A0, 0x03A5), // Τ Π Υ
            ["ΔΑ"] = FromCodePoints(0x0394, 0x0391),          // Δ Α
            ["ΑΠΔ"] = FromCodePoints(0x0391, 0x03A0, 0x0394), // Α Π Δ
        };

        private static string NormalizeDocType(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return raw;
            string trimmed = raw.Trim();
            return KnownDocTypesUnicodeSafe.TryGetValue(trimmed, out string safe) ? safe : raw;
        }

        // ΠΕΙΡΑΜΑ 16/08 (ζωντανό bug - ελληνικά strings αλλοιώνονται στο
        // write, ΕΠΙΒΕΒΑΙΩΜΕΝΟ ότι ΔΕΝ είναι θέμα collation βάσης -
        // SQL_Latin1_General_CP1253_CI_AI περιέχει σωστό Greek codepage, ο
        // χρήστης το επιβεβαίωσε). Η υπόθεση είναι ότι ο generic object-
        // typed indexer (Current[field]=value, -> set_Item(object)) περνάει
        // από COM/VARIANT marshaling που αλλοιώνει Unicode ελληνικά, ενώ το
        // strongly-typed XTable.SetAsString(rowid, fieldname, value)
        // (επιβεβαιωμένο via reflection ότι υπάρχει στο πραγματικό
        // Softone.Lib.dll) ίσως περνάει σωστά. ΔΕΝ έχω τεκμηρίωση για το
        // ακριβές rowid - δοκιμάζουμε XRow.RecNo (get_RecNo βρέθηκε via
        // reflection) - fallback στο ΠΑΛΙΟ mechanism (Current[field]=value)
        // αν σκάσει, ΔΕΝ σπάει τίποτα αν η υπόθεση είναι λάθος.
        private static void SetGreekSafeString(XTable table, string fieldname, string value)
        {
            try
            {
                table.SetAsString(table.Current.RecNo, fieldname, value);
            }
            catch (Exception ex)
            {
                DebugLog.Log($"[dr] SetGreekSafeString SetAsString EXCEPTION field={fieldname}, fallback σε Current[...]: " + ex);
                table.Current[fieldname] = value;
            }
        }

        // ΝΕΟ - διορθώνει ζωντανό crash (InvalidCastException στο Softone.
        // XTable.set_Item): τιμές από DataTable κρατάνε τον ΑΚΡΙΒΗ SQL τύπο
        // τους (π.χ. BUSUNITS=smallint -> boxed Int16), αλλά το Soft1 SDK
        // περιμένει αυστηρά Int32 σε αριθμητικά πεδία - .NET unboxing δεν
        // κάνει implicit widening, σκάει. Στενεύει ΜΟΝΟ ακέραιους τύπους σε
        // Int32 (safe - καλύπτει όλα τα σημερινά CarryOverFieldsByPhysicalTable
        // πεδία: INST/PRJC/CNTR/BUSUNITS), αφήνει string/DateTime/double κλπ
        // ως έχουν.
        private static object NormalizeNumeric(object raw)
        {
            if (raw is short || raw is byte || raw is sbyte || raw is long)
                return Convert.ToInt32(raw);
            return raw;
        }

        // Κύρια είσοδος Σταδίου 5. input: trdrId, sosource, series, docDate,
        // docNumber, lineItems, mode ("auto"/"manualPerLine"/
        // "manualConsolidate" - default "auto"). ΑΝΑΘΕΩΡΗΘΗΚΕ 16/08 - ρητή
        // οδηγία χρήστη: όταν το AnalyzeTraderPattern ΔΕΝ βγάζει αρκετή
        // σιγουριά ΚΑΙ καμία γραμμή δεν ταυτοποιήθηκε μέσω MTRSUPCODE, ΔΕΝ
        // πετάμε exception πια - επιστρέφουμε needsManualInput:true ώστε το
        // UI να προσφέρει τον "semi-manual" οδηγό (κύκλωμα/σειρά χειροκίνητα
        // + Ανά Γραμμή/Σύμπτυξη, βλ. index.html). Unmatched γραμμές (mode
        // "auto") ΔΕΝ μπλοκάρουν - επιστρέφονται σε pendingLines (#23
        // deferred, δημιουργία ΝΕΟΥ είδους παραμένει εκτός σκοπείου - το
        // manualPerLine/manualConsolidate συνδέουν με ΥΠΑΡΧΟΝ είδος μόνο).
        public static string ExecuteRegisterDrDocument(XSupport xSupport, JObject input)
        {
            int trdrId = (int?)input["trdrId"] ?? 0;
            int sosource = (int?)input["sosource"] ?? 0;
            int series = (int?)input["series"] ?? 0;
            string docDateRaw = input["docDate"]?.ToString();
            // docNumber = ΜΟΝΟ το ψηφίο/αριθμός (π.χ. "8748") - docType = ΜΟΝΟ
            // το πρόθεμα τύπου (π.χ. "ΤΠΥ") - ΞΕΧΩΡΙΣΤΑ πλέον (ΝΕΟ 16/08,
            // ζωντανό bug) γιατί το FINCODE construction διαφέρει ανά
            // GetFincodeMode - βλ. εκεί για την ακριβή λογική συνδυασμού.
            string docNumber = input["docNumber"]?.ToString();
            string docType = input["docType"]?.ToString();
            JArray lineItems = input["lineItems"] as JArray ?? new JArray();
            string mode = input["mode"]?.ToString() ?? "auto";

            if (trdrId <= 0 || series <= 0)
                throw new Exception("Λείπει trdrId ή series για καταχώρηση παραστατικού.");
            if (!LineTableBySosource.TryGetValue(sosource, out string lineTableName))
                throw new Exception($"Δεν υποστηρίζεται ακόμα καταχώρηση για sosource={sosource}.");
            if (!DocumentObjectsBySosource.TryGetValue(sosource, out var docInfo))
                throw new Exception($"Άγνωστο object για sosource={sosource}.");

            DateTime? docDate = ParseFlexibleDate(docDateRaw);
            int company = xSupport.ConnectionInfo.CompanyId;
            string objectName = docInfo.ObjectName;

            // Ξεχωρίζουμε matched/unmatched γραμμές (μέσω MTRSUPCODE, βλ.
            // ExecuteMatchExtractedItems - ΙΔΙΟ και στα 3 modes).
            var matchedLines = new List<(JObject raw, int mtrlId, double qty, double price)>();
            var pendingLines = new JArray();
            foreach (var tok in lineItems)
            {
                JObject line = tok as JObject ?? new JObject();
                JObject matched = line["matched"] as JObject;
                if (matched != null && matched["mtrlId"] != null)
                {
                    matchedLines.Add((
                        line,
                        matched["mtrlId"].Value<int>(),
                        ParseInvariantDouble(line["quantity"]),
                        ParseInvariantDouble(line["unit_price"])));
                }
                else
                {
                    pendingLines.Add(line);
                }
            }

            string strategyUsed;
            var linesToWrite = new List<MtrLineRow>();
            PatternAnalysis pattern = null;

            if (mode == "manualConsolidate")
            {
                // Χειριστής διάλεξε ΡΗΤΑ ΕΝΑ είδος/λογαριασμό (βλ.
                // renderDrManualWizard "Σύμπτυξη") - αθροίζουμε ΟΛΕΣ τις
                // γραμμές εκεί, ίδια λογική με το αυτόματο Strategy A αλλά
                // το mtrl δίνεται απευθείας, ΟΧΙ από ιστορικό template.
                int consolidateMtrlId = (int?)input["consolidateMtrlId"] ?? 0;
                if (consolidateMtrlId <= 0)
                    throw new Exception("Λείπει consolidateMtrlId για συγκεντρωτική καταχώρηση.");
                double totalQty = 0, totalValue = 0;
                foreach (var tok in lineItems)
                {
                    double qty = ParseInvariantDouble(tok["quantity"]);
                    double price = ParseInvariantDouble(tok["unit_price"]);
                    totalQty += qty;
                    totalValue += qty * price;
                }
                MtrLineRow profile = FindItemHistoryProfile(xSupport, company, trdrId, consolidateMtrlId, docNumber);
                var newLine = new MtrLineRow
                {
                    MtrL = consolidateMtrlId,
                    Qty1 = totalQty,
                    Price = totalQty > 0 ? (totalValue / totalQty) : 0,
                };
                if (profile != null)
                    foreach (var kv in profile.Extra) newLine.Extra[kv.Key] = kv.Value;
                linesToWrite.Add(newLine);
                strategyUsed = "manualConsolidate";
                pendingLines = new JArray(); // όλα απορροφήθηκαν στη μία γραμμή
            }
            else if (mode == "manualPerLine")
            {
                // Χειριστής αντιστοίχισε ΚΑΘΕ γραμμή ο ίδιος (βλ.
                // renderDrManualWizard "Ανά Γραμμή") - κάθε lineItem φέρει
                // "manualMtrlId" (JS το πρόσθεσε) ΕΠΙΠΛΕΟΝ ή ΑΝΤΙ του
                // matched.mtrlId. Ό,τι δεν έχει ΚΑΝΕΝΑ από τα δύο παραμένει
                // pending (ο χειριστής το άφησε σκόπιμα κενό).
                pendingLines = new JArray();
                foreach (var tok in lineItems)
                {
                    JObject line = tok as JObject ?? new JObject();
                    JObject matched = line["matched"] as JObject;
                    int? mtrlId = (matched != null && matched["mtrlId"] != null)
                        ? matched["mtrlId"].Value<int>()
                        : (int?)line["manualMtrlId"];
                    if (mtrlId == null || mtrlId <= 0) { pendingLines.Add(line); continue; }

                    double qty = ParseInvariantDouble(line["quantity"]);
                    double price = ParseInvariantDouble(line["unit_price"]);
                    MtrLineRow profile = FindItemHistoryProfile(xSupport, company, trdrId, mtrlId.Value, docNumber);
                    var newLine = new MtrLineRow { MtrL = mtrlId.Value, Qty1 = qty, Price = price };
                    if (profile != null)
                        foreach (var kv in profile.Extra) newLine.Extra[kv.Key] = kv.Value;
                    linesToWrite.Add(newLine);
                }
                strategyUsed = "manualPerLine";
            }
            else
            {
                // mode == "auto" (default) - πλήρως αυτόματο, βλ.
                // AnalyzeTraderPattern (pool-consistency confidence).
                pattern = AnalyzeTraderPattern(xSupport, company, trdrId, series, sosource, docNumber);

                if (pattern.Confidence >= PATTERN_CONFIDENCE_THRESHOLD && pattern.BestLines?.Count == 1)
                {
                    // "A" (pattern-based συγκεντρωτική γραμμή) - όλη η λίστα
                    // ειδών του PDF (matched ή όχι) γίνεται ΜΙΑ γραμμή.
                    var tmpl = pattern.BestLines[0];
                    double totalQty = 0, totalValue = 0;
                    foreach (var tok in lineItems)
                    {
                        double qty = ParseInvariantDouble(tok["quantity"]);
                        double price = ParseInvariantDouble(tok["unit_price"]);
                        totalQty += qty;
                        totalValue += qty * price;
                    }
                    var newLine = new MtrLineRow
                    {
                        MtrL = tmpl.MtrL,
                        Qty1 = totalQty > 0 ? totalQty : tmpl.Qty1,
                        Price = totalQty > 0 ? (totalValue / totalQty) : tmpl.Price,
                    };
                    foreach (var kv in tmpl.Extra) newLine.Extra[kv.Key] = kv.Value;
                    linesToWrite.Add(newLine);
                    strategyUsed = "A";
                    pendingLines = new JArray();
                }
                else if (matchedLines.Count > 0)
                {
                    // "B" - ασφαλέστερο fallback σε per-item MTRSUPCODE
                    // matching όταν το pattern δεν είναι αρκετά σίγουρο.
                    foreach (var l in matchedLines)
                    {
                        MtrLineRow profile = FindItemHistoryProfile(xSupport, company, trdrId, l.mtrlId, docNumber);
                        var newLine = new MtrLineRow { MtrL = l.mtrlId, Qty1 = l.qty, Price = l.price };
                        if (profile != null)
                            foreach (var kv in profile.Extra) newLine.Extra[kv.Key] = kv.Value;
                        linesToWrite.Add(newLine);
                    }
                    strategyUsed = "B";
                }
                else
                {
                    // Ούτε αξιόπιστο pattern, ούτε καν 1 matched γραμμή -
                    // ΔΕΝ πετάμε exception, γυρνάμε needsManualInput ώστε το
                    // UI να προσφέρει τον semi-manual οδηγό (ρητή οδηγία
                    // χρήστη 16/08 - "πρώτη φορά καταχώρηση, δύο δρόμοι:
                    // καθαρά manual στο Soft1, ή semi-manual με τον Jarvis").
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        needsManualInput = true,
                        patternConfidence = Math.Round(pattern.Confidence, 2),
                        sampleSize = pattern.SampleSize,
                        singleLineSampleSize = pattern.SingleLineSampleSize,
                        errorMessage = "Δεν βρέθηκε αρκετά σίγουρο pattern καταχώρησης για αυτόν τον συναλλασσόμενο."
                    });
                }
            }

            if (linesToWrite.Count == 0)
                throw new Exception("Καμία γραμμή δεν ταυτοποιήθηκε - δεν υπάρχει τίποτα να καταχωρηθεί ακόμα.");

            XModule m = xSupport.CreateModule(objectName);
            XTable FINDOC = m.GetTable("FINDOC");
            XTable lineTable = m.GetTable(lineTableName);
            try
            {
                // NormalizeDocType - ΝΕΟ 16/08 (βλ. FromCodePoints/
                // KnownDocTypesUnicodeSafe): αν το docType ταιριάζει σε έναν
                // από τους 5 γνωστούς τύπους, αντικαθίσταται με την εκδοχή
                // κατασκευασμένη από explicit code points - επιπλέον
                // ασφάλεια πριν γράψουμε σε COMMENTS/FINCODE (AutoFull/
                // Manual modes).
                string safeDocType = NormalizeDocType(docType);
                string fullDocIdentifier = string.Join(" ", new[] { safeDocType, docNumber }.Where(s => !string.IsNullOrWhiteSpace(s)));
                // Γεμίζει ΜΟΝΟ στο FincodeMode.AutoPrefixOnly - βλ. εκεί - ο
                // αριθμός που πρέπει να συμπληρώσει ο χειριστής χειροκίνητα
                // (δεν τον γράφουμε εμείς, greek-text write path σπασμένο).
                string manualFincodeHint = null;

                m.InsertData();
                FINDOC.Current["TRDR"] = trdrId;
                FINDOC.Current["SERIES"] = series;
                if (docDate.HasValue) FINDOC.Current["TRNDATE"] = docDate.Value;
                // ΑΦΑΙΡΕΘΗΚΕ Truncate() - ρητή απόφαση χρήστη 16/08
                // ("απλοποιήστε το") - οι τιμές εδώ είναι πάντα πολύ κάτω
                // από τα όρια των στηλών (FINCODE=30/COMMENTS=255/
                // REMARKS=2000).
                if (!string.IsNullOrWhiteSpace(fullDocIdentifier))
                    FINDOC.Current["REMARKS"] = "Jarvis DR - πηγή παραστατικό: " + fullDocIdentifier;

                // ΑΝΑΘΕΩΡΗΘΗΚΕ 16/08 (ζωντανό test, χρήστης εξήγησε το
                // ΠΡΑΓΜΑΤΙΚΟ Soft1 UI behavior - βλ. GetFincodeMode πιο πάνω
                // για την πλήρη ανάλυση). ΣΗΜΕΙΩΣΗ 16/08: ελληνικά strings
                // μέσω Current[...]/SetAsString αλλοιώνονται ζωντανά
                // (επιβεβαιωμένο, ΑΝΕΞΑΡΤΗΤΑ από σωστό DB collation/OS
                // locale) - raw SQL UPDATE ΜΕΤΑ το PostData() ΑΠΟΡΡΙΦΘΗΚΕ
                // ρητά (χρήστης: "δημιουργεί πρόβλημα στη λογιστική",
                // παρακάμπτει business logic/triggers του Soft1) - η
                // αλλοίωση ΠΑΡΑΜΕΝΕΙ ανοιχτό θέμα, ΔΕΝ έχει λυθεί ακόμα.
                if (!string.IsNullOrWhiteSpace(docNumber))
                {
                    var (fincodeMode, seriesCode) = GetFincodeMode(xSupport, company, series, sosource);
                    switch (fincodeMode)
                    {
                        case FincodeMode.AutoFull:
                            // Soft1 φτιάχνει ΟΛΟΚΛΗΡΟ το FINCODE μόνο του -
                            // ο αναγνωρισμένος κωδικός πάει στο COMMENTS.
                            FINDOC.Current["COMMENTS"] = fullDocIdentifier;
                            break;
                        case FincodeMode.AutoPrefixOnly:
                            // ΑΝΑΘΕΩΡΗΘΗΚΕ 16/08 (ρητή απόφαση χρήστη μετά
                            // από παρατήρηση στο S1DocReader): ΔΕΝ γράφουμε
                            // ΚΑΘΟΛΟΥ το FINCODE εδώ πια - αυτό το series
                            // configuration είναι ΟΥΤΩΣ Ή ΑΛΛΩΣ σχεδιασμένο
                            // για χειροκίνητη συμπλήρωση αριθμού από τον
                            // χειριστή (το ίδιο το Soft1 UI περιμένει άνθρωπο
                            // να "κολλήσει" τον αριθμό πάνω στο SERIES.CODE
                            // prefix) - αποφεύγουμε ΤΕΛΕΙΩΣ το σπασμένο
                            // Greek-text write path. Ο χειριστής θα το δει
                            // στο ήδη ανοιχτό (auto-open) παραστατικό και θα
                            // πληκτρολογήσει ο ίδιος - φυσικό μονοπάτι, ΔΕΝ
                            // περνάει από XTable/XRow automation, άρα ΔΕΝ
                            // αλλοιώνεται.
                              manualFincodeHint = docNumber;
                            
                            break;
                        case FincodeMode.Manual:
                        default:
                            // Καμία αυτόματη συμπλήρωση - γράφουμε ολόκληρο
                            // τον κωδικό (τύπο+αριθμό) όπως τον αναγνωρίσαμε.
                            FINDOC.Current["FINCODE"] = fullDocIdentifier;
                            break;
                    }
                }

                // Write mechanism επιβεβαιωμένο ζωντανά (S1DocReader,
                // CreateDocument, χρήστης 16/08): Add() ανά γραμμή + set
                // πεδίων μέσω Current[...] + Current.Post() ΑΝΑ ΓΡΑΜΜΗ.
                foreach (var line in linesToWrite)
                {
                    lineTable.Add();
                    lineTable.Current["MTRL"] = line.MtrL;
                    lineTable.Current["QTY1"] = line.Qty1;
                    lineTable.Current["PRICE"] = line.Price;
                    // Γενικό - γράφει ό,τι πεδίο υπάρχει στο CarryOverFieldsByPhysicalTable
                    // ΚΑΙ βρέθηκε τιμή στο ιστορικό (null -> ΔΕΝ γράφεται, μένει default).
                    // NormalizeNumeric (ΝΕΟ - ζωντανό crash): το BUSUNITS είναι
                    // smallint στο SQL -> διαβάζεται ως Int16 από το DataTable,
                    // αλλά το Soft1 set_Item περιμένει αυστηρά Int32 (.NET
                    // unboxing ΔΕΝ κάνει implicit widening) - InvalidCastException.
                    foreach (var kv in line.Extra)
                        if (kv.Value != null) lineTable.Current[kv.Key] = NormalizeNumeric(kv.Value);
                    lineTable.Current.Post();
                }

                int findocId = m.PostData();
                if (findocId <= 0)
                    throw new Exception("Αποτυχία καταχώρησης παραστατικού (PostData επέστρεψε 0).");

                DebugLog.Log($"[dr] ExecuteRegisterDrDocument OK -> findocId={findocId} objectName={objectName} " +
                    $"strategy={strategyUsed} patternConfidence={pattern?.Confidence:F2} mode={mode} " +
                    $"linesWritten={linesToWrite.Count} pendingLines={pendingLines.Count}");

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    findocId,
                    sosource,
                    objectName,
                    strategyUsed,
                    patternConfidence = pattern != null ? Math.Round(pattern.Confidence, 2) : (double?)null,
                    linesWritten = linesToWrite.Count,
                    pendingLines, // unmatched - ΔΕΝ γράφτηκαν, το UI δείχνει προειδοποίηση
                    manualFincodeHint // ΝΕΟ 16/08 - βλ. FincodeMode.AutoPrefixOnly - το UI
                                       // δείχνει "συμπλήρωσε τον αριθμό X στο ήδη ανοιχτό παραστατικό"
                });
            }
            finally
            {
                lineTable.Dispose();
                FINDOC.Dispose();
                m.Dispose();
            }
        }

        // ══════════════════════════════════════════════════════════════════
        // create_order - ΝΕΟ 17/08, ρητό αίτημα χρήστη: "Εισαγωγή
        // παραγγελίας σε οποιοδήποτε κύκλωμα με χρήση οδηγίας" (φυσική
        // γλώσσα -> ο ΙΔΙΟΣ ο Jarvis συνθέτει, μέσω δικού του tool-use
        // (query_data) - ΟΧΙ deterministic wizard σαν το TASK/TASKS). Ο
        // Jarvis ψάχνει ΜΟΝΟΣ ΤΟΥ κύκλωμα/σειρά/συναλλασσόμενο/είδη ΠΡΙΝ
        // καλέσει αυτό το tool - το tool ΜΟΝΟ γράφει, ΑΦΟΥ πρώτα περάσει
        // το confidence gate. Βλ. BuildSystemPrompt (JarvisAgentClient.cs)
        // "ΚΑΤΑΧΩΡΗΣΗ ΠΑΡΑΓΓΕΛΙΑΣ/ΠΑΡΑΣΤΑΤΙΚΟΥ ΜΕ ΟΔΗΓΙΑ" για την πλήρη
        // καθοδήγηση προς τον Jarvis.
        //
        // Λιανική (retail) ΕΞΑΙΡΕΙΤΑΙ ρητά (out of scope προς το παρόν) -
        // ΑΥΤΟΜΑΤΑ, αφού δεν είναι στο DocumentObjectsBySosource/
        // LineTableBySosource whitelist (ΔΕΝ χρειάστηκε να κυνηγήσουμε
        // ειδικά ποιο SOSOURCE είναι η λιανική - απλά ΔΕΝ το προσθέσαμε).
        //
        // Confidence gating (ρητό αίτημα 17/08 - ενεργοποιεί το "για το
        // μέλλον" σχέδιο του PATTERN_CONFIDENCE_THRESHOLD/AnalyzeTraderPattern
        // πιο πάνω, αλλά ΕΔΩ το confidence είναι self-reported από τον
        // ΙΔΙΟ τον Jarvis - ΟΧΙ στατιστικό όπως στο DR, αφού εδώ ο Jarvis
        // είναι αυτός που έκανε την ερμηνεία φυσικής γλώσσας, άρα ο ίδιος
        // ξέρει πόσο σίγουρος είναι). ParamCode 500016, default 85% - η
        // παράμετρος αναμένεται σε ΠΟΣΟΣΤΟ (π.χ. 85), ΟΧΙ κλάσμα (0.85),
        // πιο φυσικό για χειροκίνητη συμπλήρωση μέσα στο Soft1.
        // ══════════════════════════════════════════════════════════════════

        private const double DefaultOrderEntryConfidenceThreshold = 0.85;

        private static double GetOrderEntryConfidenceThreshold(XSupport xSupport)
        {
            try
            {
                XTable t = xSupport.GetSQLDataSet(
                    "SELECT ParamValue FROM cccParams WHERE ParamCode=500016");
                if (t == null || t.Count == 0) return DefaultOrderEntryConfidenceThreshold;

                double value = Convert.ToDouble(t.Current["ParamValue"], CultureInfo.InvariantCulture);
                return (value > 0 && value <= 100) ? value / 100.0 : DefaultOrderEntryConfidenceThreshold;
            }
            catch (Exception ex)
            {
                DebugLog.Log("[order_entry] GetOrderEntryConfidenceThreshold EXCEPTION, fallback: " + ex);
                return DefaultOrderEntryConfidenceThreshold;
            }
        }

        // ΝΕΟ 17/08, ρητό αίτημα χρήστη - "παραμετρική προβολή" ανά
        // κύκλωμα. ΑΡΧΙΚΑ φτιάχτηκε μόνο για το create_order (βλ.
        // CreateModule call μέσα στο ExecuteCreateOrder), αλλά ΕΠΕΚΤΑΘΗΚΕ
        // (ρητό αίτημα - "Εμβάσματα Πελατών/Προμηθευτών", sosource
        // 1412/1413) και στο ΑΝΟΙΓΜΑ υπαρχόντων παραστατικών (βλ.
        // ExecuteOpenDocument) - το 1412/1413 δεν είναι καν εγγράψιμα
        // κυκλώματα από το create_order (δεν έχουν "γραμμές ειδών"), άρα
        // ΜΟΝΟ εκεί έχει νόημα το δικό τους entry. ΓΕΝΙΚΕΥΤΗΚΕ το όνομα
        // (πριν GetOrderEntryFormName) αφού πλέον καλείται και από τα
        // δύο σημεία, όχι μόνο order entry.
        //
        // ParamCode 500018, ParamValueString (nvarchar, ΟΧΙ ParamValue -
        // επιβεβαιωμένο από το σχήμα: cccParams.ParamValueString). Μορφή:
        // "sosource=FormName" entries χωρισμένα με ';' (π.χ.
        // "1351=Salesform Jetoil;1251=Νέα προβολή JETOIL;5151=Ανάλωση;" +
        // "1412=Εμβάσματα Προμηθευτών Jetoil;1413=Εμβάσματα Πελατών JETOIL") -
        // ίδιο "key=value list" idiom με άλλα σημεία του αρχείου (π.χ.
        // CarryOverFieldsByPhysicalTable). Λείπει η παράμετρος/το entry
        // -> null, ο caller πέφτει στην ήδη υπάρχουσα, default
        // συμπεριφορά (plain objectName στο create, ΧΩΡΙΣ FORM= στο open).
        private static string GetConfiguredFormName(XSupport xSupport, int sosource)
        {
            try
            {
                XTable t = xSupport.GetSQLDataSet(
                    "SELECT ParamValueString FROM cccParams WHERE ParamCode=500018");
                if (t == null || t.Count == 0) return null;

                string raw = t.Current["ParamValueString"] == DBNull.Value ? null : t.Current["ParamValueString"].ToString();
                if (string.IsNullOrWhiteSpace(raw)) return null;

                foreach (var entry in raw.Split(';'))
                {
                    var parts = entry.Split('=');
                    if (parts.Length == 2 &&
                        int.TryParse(parts[0].Trim(), out int entrySosource) &&
                        entrySosource == sosource &&
                        !string.IsNullOrWhiteSpace(parts[1]))
                    {
                        return parts[1].Trim();
                    }
                }
                return null;
            }
            catch (Exception ex)
            {
                DebugLog.Log("[form_name] GetConfiguredFormName EXCEPTION, fallback χωρίς FORM: " + ex);
                return null;
            }
        }

        public static readonly object CreateOrderToolDefinition = new
        {
            name = "create_order",
            description =
                "Καταχωρεί ΝΕΟ παραστατικό (παραγγελία/τιμολόγιο/ΔΑ κ.λπ.) " +
                "σε ΕΝΑ από τα υποστηριζόμενα κυκλώματα, ΑΦΟΥ πρώτα έχεις " +
                "βρει (μέσω query_data) ΟΛΑ τα απαραίτητα στοιχεία: " +
                "sosource, series, τον συναλλασσόμενο (trdrId), και τις " +
                "γραμμές ειδών. ΜΗΝ το καλέσεις αν λείπει ή είναι αβέβαιο " +
                "κάποιο από αυτά - ρώτησε πρώτα τον χειριστή στο chat. " +
                "ΕΞΑΙΡΟΥΝΤΑΙ ρητά αιτήματα λιανικής πώλησης (δεν " +
                "υποστηρίζεται ακόμα) - πες στον χειριστή ότι δεν " +
                "καλύπτεται. Υποστηριζόμενα sosource: 1351=Πωλήσεις, " +
                "1251=Αγορές/Παραλαβή, 1353=Υπηρεσίες πωλήσεων, " +
                "1253=Υπηρεσίες αγορών, 5151=Ενδοδιακίνηση/Παραγωγή. Δώσε " +
                "ΠΑΝΤΑ confidence (0 έως 1) - πόσο σίγουρος είσαι ότι ΟΛΑ " +
                "τα στοιχεία (κύκλωμα/σειρά/συναλλασσόμενος/γραμμές) είναι " +
                "σωστά και χωρίς ασάφεια. Αν το confidence είναι κάτω από " +
                "το όριο, το tool ΘΑ ΑΠΟΡΡΙΦΘΕΙ - στην περίπτωση αυτή " +
                "ρώτησε τον χειριστή διευκρίνιση στο chat αντί να " +
                "ξαναδοκιμάσεις με το ίδιο confidence.",
            input_schema = new
            {
                type = "object",
                properties = new
                {
                    sosource = new
                    {
                        type = "integer",
                        @enum = new[] { 1351, 1251, 1353, 1253, 5151 },
                        description = "Κωδικός κυκλώματος (SOSOURCE)."
                    },
                    series = new
                    {
                        type = "integer",
                        description = "Η σειρά (SERIES) του παραστατικού μέσα σε αυτό το κύκλωμα."
                    },
                    trdrId = new
                    {
                        type = "integer",
                        description = "Το ID (TRDR) του συναλλασσόμενου, όπως βρέθηκε από query_data."
                    },
                    payment = new
                    {
                        type = "integer",
                        description = "Τρόπος πληρωμής (PAYMENT). Προαιρετικό - αν λείπει, γεμίζει από την κάρτα του συναλλασσόμενου."
                    },
                    shipment = new
                    {
                        type = "integer",
                        description = "Τρόπος αποστολής (SHIPMENT). Προαιρετικό - αν λείπει, γεμίζει από την κάρτα του συναλλασσόμενου."
                    },
                    lines = new
                    {
                        type = "array",
                        description = "Γραμμές ειδών.",
                        items = new
                        {
                            type = "object",
                            properties = new
                            {
                                mtrlId = new { type = "integer", description = "ID είδους (MTRL)." },
                                quantity = new { type = "number", description = "Ποσότητα." },
                                price = new
                                {
                                    type = "number",
                                    description = "Τιμή μονάδας. Προαιρετικό - αν λείπει, το Soft1 βάζει την τιμολογιακή πολιτική του είδους/πελάτη."
                                }
                            },
                            required = new[] { "mtrlId", "quantity" }
                        }
                    },
                    sourceInstruction = new
                    {
                        type = "string",
                        description = "Η ΑΚΡΙΒΗΣ οδηγία/φράση του χειριστή που οδήγησε σε αυτή την καταχώρηση - καταγράφεται για μελλοντική εκπαίδευση/αναφορά."
                    },
                    confidence = new
                    {
                        type = "number",
                        description = "Πόσο σίγουρος είσαι (0 έως 1) ότι ΟΛΑ τα παραπάνω είναι σωστά, χωρίς ασάφεια."
                    },
                    confidenceNotes = new
                    {
                        type = "string",
                        description = "Προαιρετικό - σύντομη εξήγηση τι σε έκανε λιγότερο σίγουρο, αν το confidence δεν είναι κοντά στο 1."
                    }
                },
                required = new[] { "sosource", "series", "trdrId", "lines", "sourceInstruction", "confidence" }
            }
        };

        public static string ExecuteCreateOrder(XSupport xSupport, JObject input)
        {
            int sosource = (int?)input["sosource"] ?? 0;
            int series = (int?)input["series"] ?? 0;
            int trdrId = (int?)input["trdrId"] ?? 0;
            int? payment = (int?)input["payment"];
            int? shipment = (int?)input["shipment"];
            JArray lines = input["lines"] as JArray ?? new JArray();
            string sourceInstruction = input["sourceInstruction"]?.ToString();
            double confidence = (double?)input["confidence"] ?? 0;

            if (series <= 0 || trdrId <= 0)
                throw new Exception("Λείπει series ή trdrId για καταχώρηση παραγγελίας.");
            if (lines.Count == 0)
                throw new Exception("Δεν δόθηκε καμία γραμμή είδους.");
            if (!LineTableBySosource.TryGetValue(sosource, out string lineTableName))
                throw new Exception(
                    $"Δεν υποστηρίζεται ακόμα καταχώρηση παραγγελίας για sosource={sosource} (π.χ. η λιανική δεν καλύπτεται ακόμα).");
            if (!DocumentObjectsBySosource.TryGetValue(sosource, out var docInfo))
                throw new Exception($"Άγνωστο object για sosource={sosource}.");

            double threshold = GetOrderEntryConfidenceThreshold(xSupport);
            if (confidence < threshold)
                throw new Exception(
                    $"Το confidence ({confidence:P0}) είναι κάτω από το απαιτούμενο όριο ({threshold:P0}) - " +
                    "ρώτησε διευκρίνιση τον χειριστή πριν καταχωρήσεις.");

            int company = xSupport.ConnectionInfo.CompanyId;
            string objectName = docInfo.ObjectName;

            // Payment/Shipment - αν δεν δόθηκαν ρητά, fallback στα default
            // της κάρτας του συναλλασσόμενου (ρητό αίτημα χρήστη 16/08).
            if (payment == null || shipment == null)
            {
                XTable trdrCard = xSupport.GetSQLDataSet(
                    "SELECT PAYMENT, SHIPMENT FROM TRDR WHERE COMPANY=:1 AND TRDR=:2",
                    company, trdrId);
                if (trdrCard != null && trdrCard.Count > 0)
                {
                    if (payment == null && trdrCard.Current["PAYMENT"] != DBNull.Value)
                        payment = Convert.ToInt32(trdrCard.Current["PAYMENT"]);
                    if (shipment == null && trdrCard.Current["SHIPMENT"] != DBNull.Value)
                        shipment = Convert.ToInt32(trdrCard.Current["SHIPMENT"]);
                }
            }

            // ΝΕΟ 17/08, ρητό αίτημα χρήστη - "παραμετρική προβολή" με την
            // οποία καταχωρείται η κίνηση. ΕΠΙΒΕΒΑΙΩΘΗΚΕ από τον χρήστη
            // (πραγματικός κώδικας από αδελφή εφαρμογή, ΟΧΙ εικασία):
            // CreateModule δέχεται "OBJECTNAME;FormName" - semicolon ΜΕΣΑ
            // στο ΙΔΙΟ string argument (ΔΕΝ υπάρχει ξεχωριστή παράμετρος -
            // αυτό είχε επιβεβαιωθεί σωστά με reflection). ParamCode
            // 500018 (ParamValueString) - αν λείπει η παράμετρος ή δεν
            // υπάρχει entry για αυτό το sosource, CreateModule ΧΩΡΙΣ
            // FORM (ήδη υπάρχουσα, default συμπεριφορά - ρητή απαίτηση
            // χρήστη "αν δεν υπάρχει παράμετρος τότε να είναι καταχώρηση
            // από την default").
            string orderFormName = GetConfiguredFormName(xSupport, sosource);
            string moduleName = string.IsNullOrWhiteSpace(orderFormName)
                ? objectName
                : objectName + ";" + orderFormName;

            XModule m = xSupport.CreateModule(moduleName);
            XTable FINDOC = m.GetTable("FINDOC");
            XTable lineTable = m.GetTable(lineTableName);
            try
            {
                m.InsertData();
                FINDOC.Current["TRDR"] = trdrId;
                FINDOC.Current["SERIES"] = series;
                if (payment.HasValue) FINDOC.Current["PAYMENT"] = payment.Value;
                if (shipment.HasValue) FINDOC.Current["SHIPMENT"] = shipment.Value;
                if (!string.IsNullOrWhiteSpace(sourceInstruction))
                    FINDOC.Current["REMARKS"] = Truncate("Jarvis - " + sourceInstruction, 2000);

                foreach (var tok in lines)
                {
                    int mtrlId = (int?)tok["mtrlId"] ?? 0;
                    double qty = ParseInvariantDouble(tok["quantity"]);
                    double? price = tok["price"] != null ? (double?)ParseInvariantDouble(tok["price"]) : null;
                    if (mtrlId <= 0 || qty <= 0)
                        throw new Exception("Μη έγκυρη γραμμή είδους (mtrlId/quantity).");

                    lineTable.Add();
                    lineTable.Current["MTRL"] = mtrlId;
                    lineTable.Current["QTY1"] = qty;
                    if (price.HasValue) lineTable.Current["PRICE"] = price.Value;
                    lineTable.Current.Post();
                }

                int findocId = m.PostData();
                if (findocId <= 0)
                    throw new Exception("Αποτυχία καταχώρησης παραγγελίας (PostData επέστρεψε 0).");

                DebugLog.Log($"[order_entry] ExecuteCreateOrder OK -> findocId={findocId} sosource={sosource} " +
                    $"objectName={objectName} confidence={confidence:F2} linesWritten={lines.Count}");

                // ΝΕΟ 17/08 - "εκπαίδευση" (βλ. LogOrderEntryPrompt πιο
                // πάνω) - best-effort, ΔΕΝ ρίχνει exception αν αποτύχει
                // (το παραστατικό έχει ΉΔΗ καταχωρηθεί επιτυχώς). Το id
                // επιστρέφεται στο tool result ώστε ο Jarvis να γράψει το
                // rating link (βλ. BuildSystemPrompt) - -1/null αν
                // απέτυχε το logging, το UI δεν δείχνει τότε αστέρια.
                int promptLogSoactionId = LogOrderEntryPrompt(xSupport, sosource, docInfo.Description, sourceInstruction, findocId);

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    findocId,
                    sosource,
                    objectName,
                    confidence = Math.Round(confidence, 2),
                    linesWritten = lines.Count,
                    promptLogSoactionId = promptLogSoactionId > 0 ? (int?)promptLogSoactionId : null
                });
            }
            finally
            {
                lineTable.Dispose();
                FINDOC.Dispose();
                m.Dispose();
            }
        }

        // ══════════════════════════════════════════════════════════════════
        // Semi-manual οδηγός (ΝΕΟ 16/08, ρητή οδηγία χρήστη) - lookups για
        // όταν το AnalyzeTraderPattern δεν βγάζει αρκετή σιγουριά. Τρία
        // βήματα: (1) κύκλωμα+σειρά χειροκίνητα, (2) "Ανά Γραμμή" (χρειάζεται
        // αναζήτηση σε ΟΛΟΝ τον κατάλογο) ή "Σύμπτυξη" (χρειάζεται πρώτα τα
        // ΓΝΩΣΤΑ είδη ΤΟΥ trader, fallback σε αναζήτηση αν δεν υπάρχει
        // κανένα - ρητή προτίμηση χρήστη).
        // ══════════════════════════════════════════════════════════════════

        // Σειρές διαθέσιμες για ΕΝΑ κύκλωμα (sosource) - χειροκίνητη επιλογή
        // όταν δεν υπάρχει bestGuess από το ιστορικό (βλ. ExecuteFindTraderSeriesHistory,
        // ίδιο idiom με εκεί).
        public static string ExecuteGetSeriesForSosource(XSupport xSupport, int sosource)
        {
            int company = xSupport.ConnectionInfo.CompanyId;
            var series = new JArray();
            XTable t = xSupport.GetSQLDataSet(
                "SELECT SERIES, NAME FROM SERIES WHERE COMPANY=:1 AND SOSOURCE=:2 ORDER BY NAME",
                company, sosource);
            if (t != null && t.Count > 0)
            {
                DataTable dt = t.CreateDataTable(true);
                foreach (DataRow row in dt.Rows)
                {
                    series.Add(new JObject
                    {
                        ["series"] = Convert.ToInt32(row["SERIES"]),
                        ["name"] = row["NAME"] == DBNull.Value ? null : row["NAME"].ToString()
                    });
                }
            }
            return JsonConvert.SerializeObject(new { series });
        }

        // Αναζήτηση στον ΓΕΝΙΚΟ κατάλογο ειδών - fallback του "Σύμπτυξη" όταν
        // ο trader δεν έχει ΚΑΝΕΝΑ γνωστό είδος, ΚΑΙ το βασικό εργαλείο του
        // "Ανά Γραμμή" (ο χειριστής ψάχνει ό,τι είδος θέλει). Ίδιο JOIN
        // MTRUNIT idiom με το ExecuteMatchExtractedItems.
        public static string ExecuteSearchItems(XSupport xSupport, string query)
        {
            int company = xSupport.ConnectionInfo.CompanyId;
            var items = new JArray();
            if (string.IsNullOrWhiteSpace(query)) return JsonConvert.SerializeObject(new { items });

            string like = "%" + query.Trim() + "%";
            XTable t = xSupport.GetSQLDataSet(
                "SELECT TOP 20 M.MTRL, M.CODE, M.NAME, U.SHORTCUT AS UNITNAME " +
                "FROM MTRL M LEFT JOIN MTRUNIT U ON U.COMPANY=M.COMPANY AND U.MTRUNIT=M.MTRUNIT1 " +
                "WHERE M.COMPANY=:1 AND M.ISACTIVE=1 AND (M.CODE LIKE :2 OR M.NAME LIKE :2) " +
                "ORDER BY M.NAME", company, like);
            if (t != null && t.Count > 0)
            {
                DataTable dt = t.CreateDataTable(true);
                foreach (DataRow row in dt.Rows)
                {
                    items.Add(new JObject
                    {
                        ["mtrlId"] = Convert.ToInt32(row["MTRL"]),
                        ["code"] = row["CODE"] == DBNull.Value ? null : row["CODE"].ToString(),
                        ["name"] = row["NAME"] == DBNull.Value ? null : row["NAME"].ToString(),
                        ["unit"] = row["UNITNAME"] == DBNull.Value ? null : row["UNITNAME"].ToString()
                    });
                }
            }
            return JsonConvert.SerializeObject(new { items });
        }

        // Είδη που έχει ΞΑΝΑΧΡΗΣΙΜΟΠΟΙΗΣΕΙ αυτός ο trader σε αυτό το κύκλωμα -
        // ΠΡΩΤΗ επιλογή του "Σύμπτυξη" (ρητή προτίμηση χρήστη: "πρώτα δείχνει
        // τα ήδη γνωστά είδη"), ταξινομημένα κατά συχνότητα. Query πάνω στο
        // physical MTRLINES (ΟΧΙ virtual name, ίδιο σκεπτικό με ReadMtrLines).
        public static string ExecuteGetTraderKnownItems(XSupport xSupport, int trdrId, int sosource)
        {
            int company = xSupport.ConnectionInfo.CompanyId;
            var items = new JArray();
            XTable t = xSupport.GetSQLDataSet(
                "SELECT L.MTRL, M.CODE, M.NAME, U.SHORTCUT AS UNITNAME, COUNT(*) AS CNT " +
                "FROM MTRLINES L " +
                "INNER JOIN FINDOC F ON F.COMPANY=L.COMPANY AND F.FINDOC=L.FINDOC " +
                "INNER JOIN MTRL M ON M.COMPANY=L.COMPANY AND M.MTRL=L.MTRL " +
                "LEFT JOIN MTRUNIT U ON U.COMPANY=M.COMPANY AND U.MTRUNIT=M.MTRUNIT1 " +
                "WHERE L.COMPANY=:1 AND F.TRDR=:2 AND F.SOSOURCE=:3 AND F.ISCANCEL=0 AND M.ISACTIVE=1 " +
                "GROUP BY L.MTRL, M.CODE, M.NAME, U.SHORTCUT " +
                "ORDER BY COUNT(*) DESC", company, trdrId, sosource);
            if (t != null && t.Count > 0)
            {
                DataTable dt = t.CreateDataTable(true);
                foreach (DataRow row in dt.Rows)
                {
                    items.Add(new JObject
                    {
                        ["mtrlId"] = Convert.ToInt32(row["MTRL"]),
                        ["code"] = row["CODE"] == DBNull.Value ? null : row["CODE"].ToString(),
                        ["name"] = row["NAME"] == DBNull.Value ? null : row["NAME"].ToString(),
                        ["unit"] = row["UNITNAME"] == DBNull.Value ? null : row["UNITNAME"].ToString(),
                        ["count"] = Convert.ToInt32(row["CNT"])
                    });
                }
            }
            return JsonConvert.SerializeObject(new { items });
        }

        // Άνοιγμα κάρτας συναλλασσόμενου (ΟΧΙ παραστατικού - ξεχωριστό
        // μηχανισμό από το ExecuteOpenDocument, ο συναλλασσόμενος ΔΕΝ περνάει
        // από SOSOURCE/DocumentObjectsBySosource) - ίδιο AUTOLOCATE idiom.
        public static string ExecuteOpenTrader(XSupport xSupport, string objectName, int trdrId)
        {
            if (string.IsNullOrWhiteSpace(objectName))
                throw new Exception("Άγνωστο object name για άνοιγμα συναλλασσόμενου.");

            string command = $"{objectName}[AUTOLOCATE={trdrId}]";
            xSupport.ExecS1Command(command, null);
            DebugLog.Log($"[open_trader] objectName={objectName} trdrId={trdrId} command={command}");

            return JsonConvert.SerializeObject(new { success = true, objectName, command });
        }

        // ══════════════════════════════════════════════════════════════════
        // Προσωποποιημένη προβολή ανά χρήστη - ΝΕΟ 15/08. Πίνακας
        // CRMPPRMS (COMPANY, SODTYPE, USERS, CRMPPRMS, SODATA) κρατάει, ανά
        // χρήστη, ποια custom view προτιμάει για κάθε SOSOURCE -
        // επιβεβαιωμένο ζωντανά από τον χρήστη 15/08 (raw SELECT * dump +
        // στήλες). SODTYPE=96 και CRMPPRMS=2 (το "type" της εγγραφής για τη
        // λίστα ονομασμένων views, ξεχωριστό από την ΙΔΙΑ τη σημασία
        // "SODTYPE" αλλού) είναι ΣΤΑΘΕΡΑ σε ΑΥΤΗ τη βάση - ρητά
        // επιβεβαιωμένο "προς το παρόν", ο χρήστης θα το ξαναδεί σε άλλη
        // βάση αργότερα.
        //
        // Μορφή SODATA (ίδιο '~'/'|' μοτίβο με SERIESCNV):
        //   "idx|name|SOSOURCE|?|series|viewId|roles|"
        // πράγματι επιβεβαιωμένο παράδειγμα:
        //   "1|Ανάλυση Δέιγματος Δεξαμενής|2021|3|9996|474|1,2,3,4,5,6,7|~..."
        // -> για SOSOURCE=2021, viewId=474. Το viewId είναι CSTINFO.CSTINFO
        // id (Form structure, CSTTYPE=1) - CSTINFO.CSTNAME δίνει το
        // πραγματικό όνομα που καταλαβαίνει το FORM= parameter.
        // ══════════════════════════════════════════════════════════════════

        private const int CrmpprmsSodType = 96;
        private const int CrmpprmsNamedViewsType = 2;

        // null σε ΟΠΟΙΑΔΗΠΟΤΕ αποτυχία/έλλειψη - ΠΟΤΕ δεν μπλοκάρει το
        // open_document, μόνο του αφαιρεί το FORM= (γυρνάει στο default
        // view, ίδια συμπεριφορά με πριν αυτό το feature).
        private static string GetPersonalizedFormName(XSupport xSupport, int userId, int sosource)
        {
            try
            {
                int company = xSupport.ConnectionInfo.CompanyId;
                XTable t = xSupport.GetSQLDataSet(
                    "SELECT SODATA FROM CRMPPRMS WHERE COMPANY=:1 AND SODTYPE=:2 AND USERS=:3 AND CRMPPRMS=:4",
                    company, CrmpprmsSodType, userId, CrmpprmsNamedViewsType);

                if (t == null || t.Count == 0) return null;
                string sodata = t.Current["SODATA"] == DBNull.Value ? null : t.Current["SODATA"].ToString();
                if (string.IsNullOrWhiteSpace(sodata)) return null;

                int? viewId = null;
                foreach (var entry in sodata.Split('~'))
                {
                    if (string.IsNullOrWhiteSpace(entry)) continue;
                    var parts = entry.Split('|');
                    // index 2 = SOSOURCE, index 5 = view id (βλ. format πιο πάνω)
                    if (parts.Length > 5 &&
                        int.TryParse(parts[2], out int entrySosource) && entrySosource == sosource &&
                        int.TryParse(parts[5], out int vid))
                    {
                        viewId = vid;
                        break;
                    }
                }
                if (viewId == null) return null;

                XTable cst = xSupport.GetSQLDataSet(
                    "SELECT CSTNAME FROM CSTINFO WHERE CSTINFO=:1", viewId.Value);
                if (cst == null || cst.Count == 0) return null;
                return cst.Current["CSTNAME"] == DBNull.Value ? null : cst.Current["CSTNAME"].ToString();
            }
            catch (Exception ex)
            {
                DebugLog.Log("[open_document] GetPersonalizedFormName EXCEPTION, fallback χωρίς FORM: " + ex);
                return null;
            }
        }

        // ══════════════════════════════════════════════════════════════════
        // get_conversion_targets - ΝΕΟ 15/08 (βλ. session notes "Μετατροπή
        // παραστατικών"). ΚΑΘΑΡΑ read-only discovery - βρίσκει τις ΠΙΘΑΝΕΣ
        // σειρές-στόχους μετασχηματισμού ενός παραστατικού ΔΥΝΑΜΙΚΑ, από το
        // πεδίο SERIES.SERIESCNV - ΚΑΜΙΑ εγγραφή/μετατροπή εδώ, μόνο
        // discovery ΠΡΙΝ από όποιο μελλοντικό write tool.
        //
        // Μορφή SERIESCNV (επιβεβαιωμένο ζωντανά 15/08 από τον χρήστη):
        // string, εγγραφές χωρισμένες με '~', κάθε εγγραφή pipe-delimited
        // "Α|SOSOURCE|TargetSeries||" - το πρώτο πεδίο (Α) ΔΕΝ μας
        // ενδιαφέρει (σειρά εμφάνισης στο Soft1 UI, ρητή απάντηση χρήστη -
        // "δεν παίζει ρόλο"), το ΤΡΙΤΟ πεδίο (index 2) είναι το target
        // SERIES. Παράδειγμα:
        //   "1|1351|7074|||~2|1351|7067|||~1|1351|7001|||~"
        //   -> target series: 7074, 7067, 7001
        // ══════════════════════════════════════════════════════════════════

        public static readonly object GetConversionTargetsToolDefinition = new
        {
            name = "get_conversion_targets",
            description =
                "Βρίσκει τις ΠΙΘΑΝΕΣ σειρές-στόχους μετασχηματισμού για ένα " +
                "παραστατικό (π.χ. σε ποιους τύπους Τιμολογίου μπορεί να " +
                "γίνει μια Παραγγελία) - ΔΙΑΒΑΖΕΙ ΜΟΝΟ, ΔΕΝ κάνει καμία " +
                "μετατροπή (δεν υπάρχει ακόμα tool για την ίδια τη " +
                "μετατροπή). Επιστρέφει 'targets' (θεωρητικές πιθανές " +
                "σειρές-στόχους) ΚΑΙ 'alreadyConvertedTo' (αν αυτό το " +
                "παραστατικό έχει ΗΔΗ μετασχηματιστεί κάπου στο παρελθόν - " +
                "εμπειρική επιβεβαίωση, πιο αξιόπιστη όταν υπάρχει). " +
                "Χρησιμοποίησέ το ΠΑΝΤΑ πριν αναφέρεις " +
                "μετασχηματισμό/μετατροπή παραστατικού σε χειριστή, αντί να " +
                "μαντέψεις σειρά-στόχο.",
            input_schema = new
            {
                type = "object",
                properties = new
                {
                    findoc = new
                    {
                        type = "integer",
                        description = "Το FINDOC id (πρωτεύον κλειδί) του παραστατικού-πηγής."
                    }
                },
                required = new[] { "findoc" }
            }
        };

        public static string ExecuteGetConversionTargets(XSupport xSupport, int findoc)
        {
            XTable src = xSupport.GetSQLDataSet(
                "SELECT F.COMPANY, F.SOSOURCE, F.SERIES, S.SERIESCNV " +
                "FROM FINDOC F JOIN SERIES S ON S.COMPANY=F.COMPANY AND S.SERIES=F.SERIES " +
                $"WHERE F.FINDOC={findoc}");

            if (src == null || src.Count == 0)
                throw new Exception($"Δεν βρέθηκε παραστατικό με FINDOC={findoc}.");

            int company = Convert.ToInt32(src.Current["COMPANY"]);
            int sosource = Convert.ToInt32(src.Current["SOSOURCE"]);
            int sourceSeries = Convert.ToInt32(src.Current["SERIES"]);
            string seriescnv = src.Current["SERIESCNV"] == DBNull.Value
                ? null : src.Current["SERIESCNV"]?.ToString();

            // Parse - βλ. σχόλιο format πιο πάνω. int.TryParse αντί για
            // ρίξιμο exception σε τυχόν απροσδόκητη/κενή εγγραφή - καλύτερα
            // να αγνοηθεί μία κακοσχηματισμένη εγγραφή παρά να σκάσει όλο
            // το discovery.
            var targetSeriesIds = new List<int>();
            if (!string.IsNullOrWhiteSpace(seriescnv))
            {
                foreach (var entry in seriescnv.Split('~'))
                {
                    if (string.IsNullOrWhiteSpace(entry)) continue;
                    var parts = entry.Split('|');
                    if (parts.Length > 2 && int.TryParse(parts[2], out int targetSeries)
                        && !targetSeriesIds.Contains(targetSeries))
                    {
                        targetSeriesIds.Add(targetSeries);
                    }
                }
            }

            var targets = new List<object>();
            if (targetSeriesIds.Count > 0)
            {
                // targetSeriesIds είναι ΗΔΗ επιβεβαιωμένα int (int.TryParse
                // πιο πάνω) - ασφαλές να μπουν απευθείας στο IN(), ίδιο
                // σκεπτικό με GetQaLogSeries/GetDirectExportMaxRows.
                string idList = string.Join(",", targetSeriesIds);
                XTable targetInfo = xSupport.GetSQLDataSet(
                    $"SELECT SERIES, NAME, SOSOURCE FROM SERIES WHERE COMPANY={company} AND SERIES IN ({idList})");

                DataTable dt = targetInfo.CreateDataTable(true);
                foreach (DataRow row in dt.Rows)
                {
                    targets.Add(new
                    {
                        series = Convert.ToInt32(row["SERIES"]),
                        name = row["NAME"] == DBNull.Value ? null : row["NAME"].ToString(),
                        sosource = Convert.ToInt32(row["SOSOURCE"])
                    });
                }
            }

            // Εμπειρική επιβεβαίωση (ρητή τεχνική του χρήστη 15/08): ΠΕΡΑ
            // από τις θεωρητικές επιλογές (SERIESCNV πιο πάνω), ψάξε αν αυτό
            // το παραστατικό ΕΧΕΙ ΗΔΗ μετασχηματιστεί κάπου - FINDOC.FINDOCS
            // στο ΠΑΙΔΙ κρατάει το id ΤΗΣ ΠΗΓΗΣ (findocs = findocSOURCE, ΟΧΙ
            // "findocs" σαν πληθυντικό/λίστα του ίδιου - επιβεβαιωμένο ζωντανά
            // από τον χρήστη). Δεν ξέρουμε αν είναι πάντα μονός αριθμός ή
            // dash-delimited λίστα (ίδια σύμβαση με CONVDOCS) - το LIKE
            // καλύπτει ΚΑΙ τα δύο, με boundary στο '-' ώστε να ΜΗΝ πιάνει
            // κατά λάθος substring άλλου id (π.χ. "120" μέσα στο "1120749").
            // Ο χρήστης είπε "στο 90% των περιπτώσεων θα σου επιστρέψει 1
            // παραστατικό" - καθαρά ΠΛΗΡΟΦΟΡΙΑΚΟ πεδίο, ΔΕΝ αντικαθιστά τα
            // targets πιο πάνω.
            var alreadyConvertedTo = new List<object>();
            XTable existing = xSupport.GetSQLDataSet(
                "SELECT F.FINDOC, F.SERIES, F.SOSOURCE, F.FINCODE, S.NAME AS SERIESNAME " +
                "FROM FINDOC F LEFT JOIN SERIES S ON S.COMPANY=F.COMPANY AND S.SERIES=F.SERIES " +
                $"WHERE F.COMPANY={company} AND (" +
                $"F.FINDOCS='{findoc}' OR F.FINDOCS LIKE '{findoc}-%' OR " +
                $"F.FINDOCS LIKE '%-{findoc}' OR F.FINDOCS LIKE '%-{findoc}-%')");
            if (existing != null && existing.Count > 0)
            {
                DataTable existingDt = existing.CreateDataTable(true);
                foreach (DataRow row in existingDt.Rows)
                {
                    alreadyConvertedTo.Add(new
                    {
                        findoc = Convert.ToInt32(row["FINDOC"]),
                        series = Convert.ToInt32(row["SERIES"]),
                        seriesName = row["SERIESNAME"] == DBNull.Value ? null : row["SERIESNAME"].ToString(),
                        sosource = Convert.ToInt32(row["SOSOURCE"]),
                        fincode = row["FINCODE"] == DBNull.Value ? null : row["FINCODE"].ToString()
                    });
                }
            }

            var payload = new
            {
                sourceFindoc = findoc,
                sourceCompany = company,
                sourceSosource = sosource,
                sourceSeries,
                targets,
                alreadyConvertedTo
            };
            return JsonConvert.SerializeObject(payload);
        }

        // ══════════════════════════════════════════════════════════════════
        // create_crm_task - ΝΕΟ (βλ. README Roadmap #1 "Email agent"/Phase
        // 2c, "Μοίρασε αυτό σε συνάδελφο Χ"). Δημιουργεί εργασία CRM
        // (SOACTION) ανατεθειμένη σε συγκεκριμένο χρήστη - ΙΔΙΟ idiom με το
        // ήδη υπάρχον CreateQaLogSoAction (XModule/"SOTASK"/SOACTION), αλλά
        // ΔΙΑΦΟΡΕΤΙΚΗ σειρά/κατάσταση: το task είναι "Σε εξέλιξη" (ΟΧΙ
        // ολοκληρωμένο σαν το Q&A log - βλ. ACTSTATUS/ACTSTATES πιο κάτω).
        //
        // Ο Jarvis ΠΡΕΠΕΙ να έχει ήδη λύσει το ACTOR (userId) - π.χ. μέσω
        // query_data στο USERS (USERS=id, NAME - επιβεβαιωμένο σχήμα, βλ.
        // JarvisShell.xaml.cs::GetDisplayName) ΠΡΙΝ καλέσει αυτό το tool -
        // ΔΕΝ κάνει το ίδιο το tool το lookup ονόματος (βλ. system prompt).
        // ══════════════════════════════════════════════════════════════════

        public static readonly object CreateCrmTaskToolDefinition = new
        {
            name = "create_crm_task",
            description =
                "Δημιουργεί μια εργασία CRM (task) στο Soft1, ανατεθειμένη " +
                "σε συγκεκριμένο χρήστη/συνάδελφο. ΠΡΙΝ το καλέσεις, ΠΡΕΠΕΙ " +
                "να έχεις ήδη βρει το actorUserId (query_data στον πίνακα " +
                "USERS - στήλες USERS=id, NAME). Αν ο χειριστής δεν έχει " +
                "πει σε ΠΟΙΟΝ να ανατεθεί, ή αν η αναζήτηση επέστρεψε " +
                "παραπάνω από ένα πιθανό άτομο, ΡΩΤΑ πρώτα (❓/> " +
                "quick-reply format) - ΜΗΝ μαντέψεις ΠΟΤΕ το άτομο. Ρώτα " +
                "ΕΠΙΣΗΣ ρητά αν θέλει υπενθύμιση, και αν ναι ΠΟΤΕ - πέρασε " +
                "το αποτέλεσμα στο reminderDate (ΧΩΡΙΣ αυτό, ΔΕΝ μπαίνει " +
                "υπενθύμιση). Αν η εργασία αφορά ΣΥΓΚΕΚΡΙΜΕΝΟ πελάτη/" +
                "προμηθευτή, πέρασε ΚΑΙ trdr ΚΑΙ tsodType μαζί (βρες το " +
                "trdr με query_data στο TRDR - ίδιο ΔΙΕΥΚΡΙΝΙΣΤΙΚΕΣ " +
                "ΕΡΩΤΗΣΕΙΣ πρωτόκολλο αν είναι ασαφές ΠΟΙΟΝ εννοεί). " +
                "ΠΑΝΤΑ ρώτα ΚΑΙ πότε πρέπει να ξεκινήσει/εκτελεστεί η " +
                "εργασία (fromDate, ΥΠΟΧΡΕΩΤΙΚΟ) - αν δεν πει κάτι " +
                "συγκεκριμένο, χρησιμοποίησε το τώρα.",
            input_schema = new
            {
                type = "object",
                properties = new
                {
                    title = new
                    {
                        type = "string",
                        description = "Σύντομος τίτλος/περίληψη της εργασίας."
                    },
                    description = new
                    {
                        type = "string",
                        description = "Λεπτομερής περιγραφή της εργασίας."
                    },
                    actorUserId = new
                    {
                        type = "integer",
                        description =
                            "Το USERS id του χρήστη στον οποίο ανατίθεται η εργασία " +
                            "(βρέθηκε ήδη με query_data). Αν ο χειριστής θέλει την " +
                            "ΙΔΙΑ εργασία σε ΠΑΝΩ ΑΠΟ ΕΝΑΝ χρήστη ταυτόχρονα, " +
                            "χρησιμοποίησε ΑΝΤΙ γι' αυτό το actorUserIds (array) - " +
                            "θα δημιουργηθεί ΞΕΧΩΡΙΣΤΗ εγγραφή ανά χρήστη."
                    },
                    actorUserIds = new
                    {
                        type = "array",
                        items = new { type = "integer" },
                        description =
                            "Εναλλακτικό του actorUserId - λίστα από USERS ids όταν " +
                            "ανατίθεται η ΙΔΙΑ εργασία σε πολλούς χρήστες ταυτόχρονα. " +
                            "Χρησιμοποίησε ΕΝΑ από τα δύο (actorUserId ή actorUserIds), όχι και τα δύο."
                    },
                    fromDate = new
                    {
                        type = "string",
                        description =
                            "ΥΠΟΧΡΕΩΤΙΚΟ - πότε ξεκινάει/εκτελείται η εργασία " +
                            "(ISO date, π.χ. '2026-08-20T09:00:00'). ΞΕΧΩΡΙΣΤΟ " +
                            "από την ημερομηνία καταχώρησης (αυτή μπαίνει " +
                            "αυτόματα σήμερα)."
                    },
                    reminderDate = new
                    {
                        type = "string",
                        description =
                            "Προαιρετικό - πότε θέλει ο χειριστής υπενθύμιση " +
                            "(ISO date, π.χ. '2026-08-20' ή '2026-08-20T09:00:00'). " +
                            "ΜΟΝΟ αν ο χειριστής το ζήτησε ρητά - ΜΗΝ το βάλεις " +
                            "από μόνος σου χωρίς να ρωτήσεις. ΔΕΝ μπορεί να " +
                            "είναι ΜΕΤΑ το fromDate."
                    },
                    trdr = new
                    {
                        type = "integer",
                        description =
                            "Προαιρετικό - το TRDR id του συναλλασσόμενου " +
                            "(πελάτη/προμηθευτή) που αφορά η εργασία, ΜΟΝΟ αν " +
                            "υπάρχει τέτοιος. ΑΝ το δώσεις, ΠΡΕΠΕΙ να δώσεις " +
                            "ΚΑΙ tsodType."
                    },
                    tsodType = new
                    {
                        type = "integer",
                        @enum = new[] { 12, 13 },
                        description =
                            "Προαιρετικό, αλλά ΥΠΟΧΡΕΩΤΙΚΟ μαζί με το trdr - " +
                            "12 αν είναι προμηθευτής, 13 αν είναι πελάτης " +
                            "(ίδια σύμβαση με SODTYPE στο TRDR)."
                    },
                    durationMinutes = new
                    {
                        type = "integer",
                        description =
                            "Προαιρετικό - πόση ώρα (σε λεπτά) αναμένεται να " +
                            "διαρκέσει η εργασία, π.χ. 30, 60, 90. ΠΑΝΤΑ απλός " +
                            "ακέραιος αριθμός λεπτών - ΠΟΤΕ μην υπολογίζεις ή " +
                            "γράφεις raw ώρες:λεπτά string, αυτό το κάνει το " +
                            "backend."
                    }
                },
                // ΟΧΙ actorUserId στο required - είναι ΕΝΑ από δύο εναλλακτικά
                // (actorUserId ή actorUserIds, βλ. περιγραφές πιο πάνω). Το
                // runtime validation (ParseActorUserIds) ρίχνει καθαρό σφάλμα
                // αν λείπουν ΚΑΙ τα δύο.
                required = new[] { "title", "description", "fromDate" }
            }
        };

        // ParamCode 500012 ("Jarvis - Σειρά CRM Tasks") - ΑΠΑΙΤΟΥΜΕΝΟ, ίδιο
        // σκεπτικό με το GetQaLogSeries (500008) - χωρίς σειρά δεν μπορεί να
        // γίνει η καταχώρηση, throw αν λείπει (ΟΧΙ ασφαλές default όπως τα
        // δύο παρακάτω, εκεί μια λάθος τιμή είναι απλά αισθητική).
        private static int GetCrmTaskSeries(XSupport xSupport)
        {
            XTable t = xSupport.GetSQLDataSet(
                "SELECT ParamValue FROM cccParams WHERE ParamCode=500012");
            if (t == null || t.Count == 0)
                throw new Exception(
                    "Δεν βρέθηκε η παράμετρος 500012 (Σειρά CRM Tasks) στο cccParams.");
            return Convert.ToInt32(t.Current["ParamValue"]);
        }

        // ParamCode 500013 ("Jarvis - ActStates", default 1001) και 500014
        // ("Jarvis - ActStatus", default 1) - προαιρετικά, ίδιο πνεύμα με
        // GetReportDecimalPlaces/GetDirectExportMaxRows: αν λείπουν, ασφαλές
        // fallback (οι default τιμές που έδωσε ρητά ο χρήστης 15/08 - "Σε
        // εξέλιξη"), ΔΕΝ σπάει το chat, μόνο DebugLog.
        // internal (ΟΧΙ private πια, ΝΕΟ 17/08) - ξαναχρησιμοποιείται ΚΑΙ
        // από το JarvisEmailAccess.cs (500022/500023, βλ. εκεί) - γενικός
        // optional-int-param reader, όχι κάτι αποκλειστικά CRM-task.
        internal static int GetCrmTaskOptionalParam(XSupport xSupport, int paramCode, int defaultValue)
        {
            try
            {
                XTable t = xSupport.GetSQLDataSet(
                    $"SELECT ParamValue FROM cccParams WHERE ParamCode={paramCode}");
                if (t == null || t.Count == 0) return defaultValue;

                var val = t.Current["ParamValue"];
                return (val == null || val == DBNull.Value) ? defaultValue : Convert.ToInt32(val);
            }
            catch (Exception ex)
            {
                DebugLog.Log($"[crm_task] GetCrmTaskOptionalParam({paramCode}) EXCEPTION, fallback: " + ex);
                return defaultValue;
            }
        }

        // Ίδιο idiom με GetCrmTaskOptionalParam πιο πάνω, ΑΛΛΑ string
        // (ParamValueString) - ΝΕΟ 18/08, ρητό αίτημα χρήστη ("μια
        // παράμετρος που θα την φορτώνουμε με κείμενο εκπαίδευσης ...
        // κάτι σαν skill"). Reusable - ΟΧΙ ειδικά για ένα ParamCode,
        // οποιαδήποτε μελλοντική προαιρετική ParamValueString παράμετρος
        // μπορεί να το ξαναχρησιμοποιήσει.
        internal static string GetOptionalParamString(XSupport xSupport, int paramCode)
        {
            try
            {
                XTable t = xSupport.GetSQLDataSet(
                    $"SELECT TOP 1 ParamValueString FROM cccParams WHERE ParamCode={paramCode} " +
                    "AND (paramsIsActive=1 OR paramsIsActive IS NULL) ORDER BY cccParams DESC");
                if (t == null || t.Count == 0 || t.Current["ParamValueString"] == DBNull.Value)
                    return null;
                string val = t.Current["ParamValueString"].ToString();
                return string.IsNullOrWhiteSpace(val) ? null : val;
            }
            catch (Exception ex)
            {
                DebugLog.Log($"[params] GetOptionalParamString({paramCode}) EXCEPTION, fallback null: " + ex);
                return null;
            }
        }

        // Δημιουργεί ΕΝΑ CRM record (SOACTION-based, ανεξάρτητα από object -
        // ίδιος πίνακας "SOACTION" πάντα μέσω GetTable, ρητά επιβεβαιωμένο
        // από τον χρήστη 15/08: "τα πεδία είναι παντού ίδια") - κοινός
        // πυρήνας που καλείται ΜΙΑ φορά ανά actor όταν ζητούνται πολλαπλοί
        // παραλήπτες (νέο 15/08, "να φτιάχνω ταυτόχρονα σε περισσότερους
        // από έναν την ίδια εργασία" - το SOACTION δεν υποστηρίζει
        // multi-actor σε ΕΝΑ record, το ACTOR είναι μονό πεδίο).
        private static int CreateCrmRecordCore(
            XSupport xSupport, string objectName, int series, string title, string description,
            int actorUserId, DateTime fromDate, DateTime? reminderDate, int? trdr, int? tsodType,
            int actStates, int actStatus, int? parentSoactionId = null, int? inst = null, int? prjc = null,
            int? durationMinutes = null)
        {
            XModule m = xSupport.CreateModule(objectName);
            XTable SOACTION = m.GetTable("SOACTION");
            try
            {
                m.InsertData();
                SOACTION.Current["SERIES"] = series;
                // ΟΧΙ SOACTIONCODE χειροκίνητα - γεμίζει μόνο του (ίδιο
                // επιβεβαιωμένο pattern με το CreateQaLogSoAction).
                // "AUTOTASK - " πρόθεμα ΡΗΤΟ, ζητήθηκε 15/08 - tag ώστε να
                // ξεχωρίζουν οι Jarvis-δημιουργημένες εργασίες από τις
                // χειροκίνητες μέσα στο Soft1.
                SOACTION.Current["COMMENTS"] = Truncate("AUTOTASK - " + title, 2000);
                SOACTION.Current["REMARKS"] = Truncate(description, 2000);
                SOACTION.Current["ACTOR"] = actorUserId;
                SOACTION.Current["ORDEREDBY"] = xSupport.ConnectionInfo.UserId;
                SOACTION.Current["ACTSTATUS"] = actStatus;
                SOACTION.Current["ACTSTATES"] = actStates;
                // TRNDATE = ΠΑΝΤΑ σήμερα (ρητό 15/08, ΔΕΝ έρχεται από τον
                // χειριστή/Jarvis) - ΞΕΧΩΡΙΣΤΟ από το FROMDATE, που είναι η
                // πραγματική "ημερομηνία εκκίνησης" της εργασίας.
                SOACTION.Current["TRNDATE"] = DateTime.Today;
                SOACTION.Current["FROMDATE"] = fromDate;

                // Διάρκεια σε λεπτά - ΝΕΟ 17/08, ρητό αίτημα χρήστη: custom
                // field SOACTION.cccMidDur (Designer field "Διάρκεια Σε
                // Λεπτά", integer), ΚΟΙΝΟ ανάμεσα σε manual καταχώρηση
                // (native φόρμα εργασίας Series 6) και Jarvis-δημιουργημένες
                // εργασίες - ΕΠΙΤΗΔΕΣ ΟΧΙ το native SOACTION.DURATION (raw
                // datetime/TimeSpan encoding, βλ. session notes 17/08) - ένα
                // απλό ακέραιο πεδίο και από τις δύο πλευρές, καμία επαφή με
                // raw format.
                if (durationMinutes.HasValue)
                    SOACTION.Current["cccMidDur"] = durationMinutes.Value;

                // ΝΕΟ 16/08, ρητό αίτημα χρήστη ("Επόμενη ενέργεια" στο
                // dashboard) - SOACTIONS δείχνει στο soaction που μόλις
                // ολοκληρώθηκε, ώστε να υπάρχει ιστορικό αλυσίδας ενεργειών
                // (confirmed πεδίο, βλ. schema SOACTION.SOACTIONS int).
                if (parentSoactionId.HasValue)
                    SOACTION.Current["SOACTIONS"] = parentSoactionId.Value;

                // Συναλλασσόμενος - ΠΡΟΑΙΡΕΤΙΚΟ ζεύγος (βλ. validation στους
                // callers, ζητήθηκε ρητά 15/08).
                if (trdr.HasValue)
                {
                    SOACTION.Current["TRDR"] = trdr.Value;
                    SOACTION.Current["TSODTYPE"] = tsodType.Value;
                }

                // Εγκατάσταση/Έργο - ΝΕΟ 16/08, ρητό αίτημα χρήστη, ΚΑΙ ΤΑ ΔΥΟ
                // προαιρετικά ΚΑΙ ανεξάρτητα μεταξύ τους (όχι ζεύγος σαν το
                // trdr/tsodType πιο πάνω) - confirmed πεδία ΑΠΕΥΘΕΙΑΣ πάνω στο
                // SOACTION (schema: SOACTION.INST/SOACTION.PRJC, int, nullable).
                if (inst.HasValue) SOACTION.Current["INST"] = inst.Value;
                if (prjc.HasValue) SOACTION.Current["PRJC"] = prjc.Value;

                // Υπενθύμιση - ΞΕΧΩΡΙΣΤΟΣ πίνακας-σύντροφος VACTRMND (ΟΧΙ
                // πεδίο πάνω στο SOACTION), πηγή: ζωντανός SBSL κώδικας του
                // χρήστη 15/08 (TblRem=SOActionObj.FindTable('VACTRMND');
                // TblRem.Edit; TblRem.HASREMIND=1; TblRem.RMDATE=...). Εδώ
                // ΧΩΡΙΣ ξεχωριστό .Edit()/.Add() - ίδιο σκεπτικό με το
                // SOACTION.Current[...] πιο πάνω: sibling table του ΙΔΙΟΥ
                // module, ήδη σε insert-mode μετά το m.InsertData(),
                // committed μαζί στο τελικό m.PostData(). ✅ Επιβεβαιωμένο
                // ζωντανά 15/08 (SOTASK) ότι αυτό το pattern δουλεύει.
                if (reminderDate.HasValue)
                {
                    XTable VACTRMND = m.GetTable("VACTRMND");
                    VACTRMND.Current["HASREMIND"] = 1;
                    VACTRMND.Current["RMDATE"] = reminderDate.Value;
                }

                return m.PostData();
            }
            finally
            {
                SOACTION.Dispose();
                m.Dispose();
            }
        }

        // Διαβάζει actorUserId (μονός, ΠΑΛΙΟ σχήμα) Ή actorUserIds (λίστα,
        // ΝΕΟ 15/08) από το input - το δεύτερο έχει προτεραιότητα αν
        // υπάρχει. Κοινό parsing, χρησιμοποιείται ΚΑΙ από το
        // ExecuteCreateCrmTask ΚΑΙ από το ExecuteCreateCrmRecord.
        private static List<int> ParseActorUserIds(JObject input)
        {
            var actorUserIds = new List<int>();
            if (input?["actorUserIds"] is JArray arr)
            {
                foreach (var v in arr)
                {
                    int? uid = (int?)v;
                    if (uid.HasValue) actorUserIds.Add(uid.Value);
                }
            }
            else
            {
                int? actorUserId = (int?)input?["actorUserId"];
                if (actorUserId.HasValue) actorUserIds.Add(actorUserId.Value);
            }
            return actorUserIds;
        }

        public static string ExecuteCreateCrmTask(XSupport xSupport, JObject input)
        {
            string title = input?["title"]?.ToString();
            string description = input?["description"]?.ToString();
            string fromDateRaw = input?["fromDate"]?.ToString();
            string reminderDateRaw = input?["reminderDate"]?.ToString();
            int? trdr = (int?)input?["trdr"];
            int? tsodType = (int?)input?["tsodType"];
            // ΝΕΟ 16/08 - "Επόμενη ενέργεια" (βλ. CreateCrmRecordCore).
            int? parentSoactionId = (int?)input?["parentSoactionId"];
            // ΝΕΟ 16/08, ρητό αίτημα χρήστη - Εγκατάσταση/Έργο, ΚΑΙ ΤΑ ΔΥΟ
            // προαιρετικά (βλ. taskInstField/taskPrjcField στο index.html).
            int? inst = (int?)input?["inst"];
            int? prjc = (int?)input?["prjc"];
            // ΝΕΟ 17/08 - βλ. CreateCrmRecordCore (custom field cccMidDur).
            int? durationMinutes = (int?)input?["durationMinutes"];
            List<int> actorUserIds = ParseActorUserIds(input);

            if (string.IsNullOrWhiteSpace(title))
                throw new Exception("Λείπει ο τίτλος της εργασίας.");
            if (actorUserIds.Count == 0)
                throw new Exception("Λείπει το actorUserId/actorUserIds (σε ποιον ανατίθεται η εργασία).");
            if (string.IsNullOrWhiteSpace(fromDateRaw))
                throw new Exception("Λείπει το fromDate (ημερομηνία εκκίνησης της εργασίας).");
            if (durationMinutes.HasValue && durationMinutes.Value <= 0)
                throw new Exception("Η διάρκεια (durationMinutes) πρέπει να είναι θετικός αριθμός λεπτών.");

            // trdr/tsodType - ΖΕΥΓΟΣ, ρητή απαίτηση του χρήστη 15/08 (TSODTYPE
            // "υποχρεωτικό ΑΝ πρέπει να βάλω συναλλασσόμενο") - το ένα ΧΩΡΙΣ
            // το άλλο δεν βγάζει νόημα (ούτε trdr χωρίς να ξέρουμε αν είναι
            // πελάτης/προμηθευτής, ούτε tsodType χωρίς να ξέρουμε ΠΟΙΟΝ).
            if (trdr.HasValue != tsodType.HasValue)
                throw new Exception("Τα trdr και tsodType πρέπει να δίνονται ΜΑΖΙ (ή κανένα από τα δύο).");
            if (tsodType.HasValue && tsodType.Value != 12 && tsodType.Value != 13)
                throw new Exception($"Μη έγκυρο tsodType={tsodType.Value} (μόνο 12=προμηθευτής ή 13=πελάτης).");

            // ΠΡΙΝ ανοίξουμε το module - αν ο Jarvis έστειλε κάτι
            // μη-αναγνωρίσιμο σαν ημερομηνία, καλύτερα να αποτύχει ΚΑΘΑΡΑ
            // εδώ (χωρίς μισο-δημιουργημένο task) παρά να σκάσει στη μέση
            // του write.
            if (!DateTime.TryParse(fromDateRaw, out DateTime fromDate))
                throw new Exception($"Μη έγκυρη ημερομηνία εκκίνησης: {fromDateRaw}");

            DateTime? reminderDate = null;
            if (!string.IsNullOrWhiteSpace(reminderDateRaw))
            {
                if (!DateTime.TryParse(reminderDateRaw, out DateTime parsed))
                    throw new Exception($"Μη έγκυρη ημερομηνία υπενθύμισης: {reminderDateRaw}");
                // Ρητός κανόνας του χρήστη 15/08: η υπενθύμιση ΔΕΝ μπορεί να
                // είναι μετά την ημερομηνία εκκίνησης (fromDate) - το ίδιο
                // validation γίνεται ΚΑΙ client-side (index.html, TASK
                // wizard) αλλά ΕΔΩ είναι το πραγματικό, αναγκαστικό σημείο
                // ελέγχου - το wizard είναι μόνο ένα από τα δύο μονοπάτια
                // κλήσης (το άλλο είναι ο ίδιος ο Jarvis μέσω chat/AI).
                if (parsed > fromDate)
                    throw new Exception("Η ημερομηνία υπενθύμισης δεν μπορεί να είναι μετά την ημερομηνία εκκίνησης.");
                reminderDate = parsed;
            }

            int series = GetCrmTaskSeries(xSupport);
            int actStates = GetCrmTaskOptionalParam(xSupport, 500013, 1001);
            int actStatus = GetCrmTaskOptionalParam(xSupport, 500014, 1);

            var results = new JArray();
            foreach (int actorUserId in actorUserIds)
            {
                int soactionId = CreateCrmRecordCore(
                    xSupport, "SOTASK", series, title, description, actorUserId,
                    fromDate, reminderDate, trdr, tsodType, actStates, actStatus,
                    parentSoactionId, inst, prjc, durationMinutes);
                results.Add(new JObject { ["actorUserId"] = actorUserId, ["soactionId"] = soactionId });
            }

            var payload = new
            {
                success = true,
                results,
                // backward-compat: soactionId του ΠΡΩΤΟΥ - το index.html
                // (απλό TASK, ΕΝΑΣ actor στη μεγάλη πλειοψηφία περιπτώσεων)
                // το διαβάζει ήδη έτσι, δεν χρειάζεται να αλλάξει.
                soactionId = ((JObject)results[0])["soactionId"],
                reminderSet = reminderDate.HasValue
            };
            return JsonConvert.SerializeObject(payload);
        }

        // ══════════════════════════════════════════════════════════════════
        // TASKS wizard - ΝΕΟ 15/08 (βλ. session notes). Ο χειριστής διαλέγει
        // ο ΙΔΙΟΣ τύπο CRM εγγραφής (5 τύποι) ΚΑΙ σειρά, αντί για το ΕΝΑ
        // hardcoded SOTASK/ParamCode 500012 του απλού TASK wizard.
        //
        // SOSOURCE ΠΑΝΤΑ 2021 και για τους 5 τύπους - το SOREDIR (στήλη στο
        // SERIES) είναι αυτό που διαφοροποιεί το object, επιβεβαιωμένο
        // ζωντανά από τον χρήστη 15/08 (βλ. και BlackBook "Advanced Browser
        // Redirection" - #ObjectID/#ObjectName redirection βάσει
        // SOSOURCE/SOREDIR, ίδιος μηχανισμός).
        // ══════════════════════════════════════════════════════════════════

        private static readonly Dictionary<int, string> CrmObjectsBySoredir = new Dictionary<int, string>
        {
            [0] = "SOACTION",   // Γενικές Ενέργειες
            [1] = "SOCALL",     // Κλήση
            [2] = "SOMEETING",  // Συνάντηση
            [3] = "SOTASK",     // Task
            [4] = "SOEMAIL",    // Email
        };

        public static string ExecuteGetCrmSeriesForType(XSupport xSupport, int soredir)
        {
            var results = new JArray();
            if (!CrmObjectsBySoredir.ContainsKey(soredir))
                return JsonConvert.SerializeObject(new { results });

            XTable t = xSupport.GetSQLDataSet(
                "SELECT SERIES, NAME FROM SERIES WHERE COMPANY=:1 AND SOSOURCE=2021 AND SOREDIR=:2 ORDER BY NAME",
                xSupport.ConnectionInfo.CompanyId, soredir);

            if (t != null && t.Count > 0)
            {
                DataTable dt = t.CreateDataTable(true);
                foreach (DataRow row in dt.Rows)
                {
                    results.Add(new JObject
                    {
                        ["series"] = Convert.ToInt32(row["SERIES"]),
                        ["name"] = row["NAME"] == DBNull.Value ? null : row["NAME"].ToString()
                    });
                }
            }
            return JsonConvert.SerializeObject(new { results });
        }

        public static string ExecuteCreateCrmRecord(XSupport xSupport, JObject input)
        {
            string title = input?["title"]?.ToString();
            string description = input?["description"]?.ToString();
            string fromDateRaw = input?["fromDate"]?.ToString();
            string reminderDateRaw = input?["reminderDate"]?.ToString();
            int? trdr = (int?)input?["trdr"];
            int? tsodType = (int?)input?["tsodType"];
            int? soredir = (int?)input?["soredir"];
            int? series = (int?)input?["series"];
            // ΝΕΟ 16/08 - "Επόμενη ενέργεια" + Εγκατάσταση/Έργο (βλ.
            // CreateCrmRecordCore, ίδια πεδία με το ExecuteCreateCrmTask).
            int? parentSoactionId = (int?)input?["parentSoactionId"];
            int? inst = (int?)input?["inst"];
            int? prjc = (int?)input?["prjc"];
            List<int> actorUserIds = ParseActorUserIds(input);

            if (string.IsNullOrWhiteSpace(title))
                throw new Exception("Λείπει ο τίτλος της εργασίας.");
            if (soredir == null || !CrmObjectsBySoredir.TryGetValue(soredir.Value, out string objectName))
                throw new Exception($"Μη έγκυρος τύπος CRM (soredir={soredir}).");
            if (series == null)
                throw new Exception("Λείπει η σειρά.");
            if (actorUserIds.Count == 0)
                throw new Exception("Λείπει το actorUserIds (σε ποιους ανατίθεται η εγγραφή).");
            if (string.IsNullOrWhiteSpace(fromDateRaw))
                throw new Exception("Λείπει το fromDate (ημερομηνία εκκίνησης).");

            if (trdr.HasValue != tsodType.HasValue)
                throw new Exception("Τα trdr και tsodType πρέπει να δίνονται ΜΑΖΙ (ή κανένα από τα δύο).");
            if (tsodType.HasValue && tsodType.Value != 12 && tsodType.Value != 13)
                throw new Exception($"Μη έγκυρο tsodType={tsodType.Value} (μόνο 12=προμηθευτής ή 13=πελάτης).");

            if (!DateTime.TryParse(fromDateRaw, out DateTime fromDate))
                throw new Exception($"Μη έγκυρη ημερομηνία εκκίνησης: {fromDateRaw}");

            DateTime? reminderDate = null;
            if (!string.IsNullOrWhiteSpace(reminderDateRaw))
            {
                if (!DateTime.TryParse(reminderDateRaw, out DateTime parsed))
                    throw new Exception($"Μη έγκυρη ημερομηνία υπενθύμισης: {reminderDateRaw}");
                if (parsed > fromDate)
                    throw new Exception("Η ημερομηνία υπενθύμισης δεν μπορεί να είναι μετά την ημερομηνία εκκίνησης.");
                reminderDate = parsed;
            }

            int actStates = GetCrmTaskOptionalParam(xSupport, 500013, 1001);
            int actStatus = GetCrmTaskOptionalParam(xSupport, 500014, 1);

            var results = new JArray();
            foreach (int actorUserId in actorUserIds)
            {
                int soactionId = CreateCrmRecordCore(
                    xSupport, objectName, series.Value, title, description, actorUserId,
                    fromDate, reminderDate, trdr, tsodType, actStates, actStatus,
                    parentSoactionId, inst, prjc);
                results.Add(new JObject { ["actorUserId"] = actorUserId, ["soactionId"] = soactionId });
            }

            var payload = new
            {
                success = true,
                objectName,
                // Clickable link (doc:2021:id) ΜΟΝΟ όταν objectName==SOTASK -
                // ήδη υπάρχει confirmed entry στο DocumentObjectsBySosource
                // γι' αυτό. Τα άλλα 4 (SOACTION/SOCALL/SOMEETING/SOEMAIL)
                // ΔΕΝ έχουν ακόμα δικό τους entry εκεί - θα άνοιγαν ΛΑΘΟΣ
                // object αν προσπαθούσαμε (SOSOURCE=2021 είναι ΚΟΙΝΟ και
                // στους 5 τύπους, το dictionary είναι 1-προς-1) - follow-up
                // αν χρειαστεί αργότερα, προς το παρόν απλά χωρίς link.
                canOpenLink = objectName == "SOTASK",
                results,
                reminderSet = reminderDate.HasValue
            };
            return JsonConvert.SerializeObject(payload);
        }

        // ══════════════════════════════════════════════════════════════════
        // Dashboard "Tasks - Εργασίες" σελίδα (ΝΕΟ 16/08, ρητό αίτημα
        // χρήστη) - ΞΕΧΩΡΙΣΤΟ από το TASK/TASKS wizard πιο πάνω (εκείνο
        // ΔΗΜΙΟΥΡΓΕΙ, αυτό εδώ ΔΙΑΒΑΖΕΙ/ΔΙΑΧΕΙΡΙΖΕΤΑΙ). Μόνο εργασίες που
        // έχει αναθέσει Ο ΙΔΙΟΣ ο χειριστής (ORDEREDBY=τρέχων χρήστης, ρητή
        // οδηγία χρήστη - ΟΧΙ όλες οι εργασίες του συστήματος).
        //
        // ACTSTATUS string list - επιβεβαιωμένο ζωντανά 16/08 μέσω
        // GetXStrings('ACTSTATUS') στο πραγματικό Soft1 της Jetoil (ΔΕΝ το
        // μαντέψαμε):
        //  0=Αδιάφορο 1=Προς έναρξη 2=Σε εξέλιξη 3=Ολοκληρώθηκε
        //  4=Ακυρώθηκε 5=Σε αναμονή 6=Σε αναβολή 7=Προς επιστροφή
        // Το 3="Ολοκληρώθηκε" ήδη επιβεβαιωμένο ΞΕΧΩΡΙΣΤΑ (CreateQaLogSoAction/
        // README - "ίδια τιμή με τα υπάρχοντα historic logs").
        private static readonly Dictionary<int, string> CrmActStatusLabels = new Dictionary<int, string>
        {
            [0] = "Αδιάφορο",
            [1] = "Προς έναρξη",
            [2] = "Σε εξέλιξη",
            [3] = "Ολοκληρώθηκε",
            [4] = "Ακυρώθηκε",
            [5] = "Σε αναμονή",
            [6] = "Σε αναβολή",
            [7] = "Προς επιστροφή",
        };

        // Ελληνικά labels για το SOREDIR (ίδιο σύνολο με CrmObjectsBySoredir
        // πιο πάνω, ήδη επιβεβαιωμένο - βλ. σχόλιο εκεί).
        private static readonly Dictionary<int, string> CrmTypeLabels = new Dictionary<int, string>
        {
            [0] = "Γενικές Ενέργειες",
            [1] = "Κλήση",
            [2] = "Συνάντηση",
            [3] = "Task",
            [4] = "Email",
        };

        public static string ExecuteGetMyAssignedTasks(XSupport xSupport)
        {
            int company = xSupport.ConnectionInfo.CompanyId;
            int userId = xSupport.ConnectionInfo.UserId;
            var tasks = new JArray();

            // ΝΕΟ 16/08 (ζωντανό performance θέμα, χρήστης εντόπισε αργή/
            // "κρεμασμένη" φόρτωση) - TOP ασφαλές όριο, το SOACTION μπορεί
            // να έχει έτη ιστορικού χωρίς index στο ORDEREDBY - ήδη
            // ORDER BY FROMDATE DESC, άρα δείχνει τα πιο πρόσφατα.
            // ΝΕΟ 17/08, ρητό αίτημα χρήστη - ΠΑΡΑΜΕΤΡΙΚΟ πλέον (ParamCode
            // 500024, "Jarvis - Dashboard Tasks Max Rows"), default 100 αν
            // λείπει η παράμετρος (ΔΙΟΡΘΩΘΗΚΕ από το παλιό hardcoded 200 -
            // ρητή απόφαση χρήστη "αν δεν υπάρχει τότε top 100", ενιαίο
            // default με τα άλλα δύο νέα ParamCodes 500022/500023 πιο κάτω).
            // ΟΧΙ SQL injection risk - int, ΟΧΙ string interpolation
            // χρήστη μέσα στο TOP.
            int maxRows = GetCrmTaskOptionalParam(xSupport, 500024, 100);
            // ΝΕΟ 16/08 - PRJC/INST LEFT JOIN (ρητό αίτημα χρήστη, στήλες
            // Έργο/Εγκατάσταση στη λίστα + κύκλοι ανά έργο/εγκατάσταση) +
            // TRNDATE/FINALDATE (ρητό αίτημα - ενότητα "Σήμερα": πόσες
            // μπήκαν/ολοκληρώθηκαν/είναι ανοιχτές).
            XTable t = xSupport.GetSQLDataSet(
                $"SELECT TOP {maxRows} A.SOACTION, A.SOREDIR, A.FROMDATE, A.TRNDATE, A.FINALDATE, A.COMMENTS, " +
                "A.ACTSTATUS, A.ACTOR, U.NAME AS ACTORNAME, A.PRJC, P.CODE AS PRJCCODE, A.INST, I.NAME AS INSTNAME " +
                "FROM SOACTION A " +
                "LEFT JOIN USERS U ON U.USERS=A.ACTOR " +
                "LEFT JOIN PRJC P ON P.COMPANY=A.COMPANY AND P.PRJC=A.PRJC " +
                "LEFT JOIN INST I ON I.COMPANY=A.COMPANY AND I.INST=A.INST " +
                "WHERE A.COMPANY=:1 AND A.SOSOURCE=2021 AND A.ORDEREDBY=:2 " +
                "ORDER BY A.FROMDATE DESC",
                company, userId);

            if (t != null && t.Count > 0)
            {
                DataTable dt = t.CreateDataTable(true);
                foreach (DataRow row in dt.Rows)
                {
                    int soredir = Convert.ToInt32(row["SOREDIR"]);
                    int actStatus = row["ACTSTATUS"] == DBNull.Value ? 0 : Convert.ToInt32(row["ACTSTATUS"]);
                    tasks.Add(new JObject
                    {
                        ["soactionId"] = Convert.ToInt32(row["SOACTION"]),
                        ["soredir"] = soredir,
                        ["typeLabel"] = CrmTypeLabels.TryGetValue(soredir, out var tl) ? tl : ("SOREDIR " + soredir),
                        ["fromDate"] = row["FROMDATE"] == DBNull.Value ? null : Convert.ToDateTime(row["FROMDATE"]).ToString("yyyy-MM-ddTHH:mm"),
                        ["trnDate"] = row["TRNDATE"] == DBNull.Value ? null : Convert.ToDateTime(row["TRNDATE"]).ToString("yyyy-MM-dd"),
                        ["finalDate"] = row["FINALDATE"] == DBNull.Value ? null : Convert.ToDateTime(row["FINALDATE"]).ToString("yyyy-MM-dd"),
                        ["subject"] = row["COMMENTS"] == DBNull.Value ? null : row["COMMENTS"].ToString(),
                        ["actStatus"] = actStatus,
                        ["statusLabel"] = CrmActStatusLabels.TryGetValue(actStatus, out var sl) ? sl : ("Status " + actStatus),
                        ["actorId"] = row["ACTOR"] == DBNull.Value ? (int?)null : Convert.ToInt32(row["ACTOR"]),
                        ["actorName"] = row["ACTORNAME"] == DBNull.Value ? "(άγνωστος)" : row["ACTORNAME"].ToString(),
                        ["prjcId"] = row["PRJC"] == DBNull.Value ? (int?)null : Convert.ToInt32(row["PRJC"]),
                        ["prjcCode"] = row["PRJCCODE"] == DBNull.Value ? null : row["PRJCCODE"].ToString(),
                        ["instId"] = row["INST"] == DBNull.Value ? (int?)null : Convert.ToInt32(row["INST"]),
                        ["instName"] = row["INSTNAME"] == DBNull.Value ? null : row["INSTNAME"].ToString(),
                    });
                }
            }

            // ΝΕΟ 16/08, ρητό αίτημα χρήστη - διάστημα auto-refresh (λεπτά)
            // για τη σελίδα Tasks. ParamCode 500015 ("Jarvis - Dashboard
            // Tasks Refresh Interval"), προαιρετικό - αν λείπει, 5 λεπτά.
            // Ίδιο idiom με GetCrmTaskOptionalParam(500013/500014).
            int refreshMinutes = GetCrmTaskOptionalParam(xSupport, 500015, 5);

            return JsonConvert.SerializeObject(new { tasks, refreshMinutes });
        }

        // ══════════════════════════════════════════════════════════════════
        // Calendar tab (κουρτίνα Email, ΝΕΟ 17/08, βλ. README Roadmap #1) -
        // ΞΕΧΩΡΙΣΤΟ από το ExecuteGetMyAssignedTasks πιο πάνω: scope ΕΔΩ
        // είναι ACTOR=τρέχων χρήστης (εργασίες ΑΝΑΤΕΘΕΙΜΕΝΕΣ ΣΕ αυτόν - "τι
        // έχω στο ημερολόγιό μου"), ΟΧΙ ORDEREDBY (εργασίες που ΑΝΕΘΕΣΕ ο
        // ίδιος - διαφορετικός σκοπός, Dashboard "Tasks" tab). Default
        // απόφαση 17/08, session notes.
        //
        // end = FROMDATE + DURATION λεπτά (ή 30 λεπτά placeholder αν το
        // DURATION είναι NULL) - ίδιος υπολογισμός/epoch με το
        // DATEDIFF(MINUTE, 0, DURATION) που ήδη επιβεβαιώσαμε (βλ.
        // "SOACTION.DURATION" στα Επιβεβαιωμένα SDK facts, README).
        // Deterministic - καλείται από JarvisShell.HandleEmailGetCalendarAsync
        // (ΟΧΙ tool/AI call, ίδιο idiom με ExecuteGetMyAssignedTasks).
        // searchText - ΝΕΟ 17/08, ρητό αίτημα χρήστη ("συνθέτει φίλτρο, " +
        // "δηλαδή ημερομηνία και κάτι ακόμα") - προαιρετικό, λέξη-κλειδί
        // στο COMMENTS (θέμα).
        // ΣΗΜΕΙΩΣΗ 17/08: υπήρξε εδώ ΚΑΙ "hideRepeatedSubjects" (GROUP BY
        // COMMENTS HAVING COUNT(*)=1) - ΑΦΑΙΡΕΘΗΚΕ, ρητό αίτημα χρήστη
        // ("άχρηστο checkbox") - δεν δούλευε όταν το επαναλαμβανόμενο θέμα
        // έχει ΜΕΤΑΒΛΗΤΟ περιεχόμενο (π.χ. ώρα στον τίτλο, κάθε γραμμή
        // "τεχνικά μοναδική"). Αντικαταστάθηκε από γενικότερη λύση:
        // JarvisEmailAccess.ShowCalendarEntriesToolDefinition/
        // ExecuteShowCalendarEntries - ο Claude υπολογίζει ΟΠΟΙΑΔΗΠΟΤΕ
        // λογική χρειάζεται μέσω query_data και στέλνει το ΗΔΗ-σωστό
        // αποτέλεσμα απευθείας στο κύριο παράθυρο, ΧΩΡΙΣ να χρειάζεται
        // hardcoded SQL flag εδώ για κάθε πιθανό pattern.
        public static JArray GetSoactionCalendarEntries(
            XSupport xSupport, DateTime start, DateTime end, string searchText = null)
        {
            int company = xSupport.ConnectionInfo.CompanyId;
            int userId = xSupport.ConnectionInfo.UserId;
            var entries = new JArray();

            string sql =
                "SELECT A.SOACTION, A.SOREDIR, A.FROMDATE, A.COMMENTS, A.ACTSTATUS, " +
                "CASE WHEN A.DURATION IS NULL THEN 30 ELSE DATEDIFF(MINUTE, 0, A.DURATION) END AS DURATIONMINUTES " +
                "FROM SOACTION A " +
                "WHERE A.COMPANY=:1 AND A.ACTOR=:2 AND A.FROMDATE >= :3 AND A.FROMDATE < :4";
            var sqlParams = new List<object> { company, userId, start, end };

            if (!string.IsNullOrWhiteSpace(searchText))
            {
                sql += $" AND A.COMMENTS LIKE :{sqlParams.Count + 1}";
                sqlParams.Add("%" + searchText + "%");
            }
            sql += " ORDER BY A.FROMDATE";
            XTable t = xSupport.GetSQLDataSet(sql, sqlParams.ToArray());

            if (t != null && t.Count > 0)
            {
                DataTable dt = t.CreateDataTable(true);
                foreach (DataRow row in dt.Rows)
                {
                    int soredir = Convert.ToInt32(row["SOREDIR"]);
                    int actStatus = row["ACTSTATUS"] == DBNull.Value ? 0 : Convert.ToInt32(row["ACTSTATUS"]);
                    DateTime fromDate = Convert.ToDateTime(row["FROMDATE"]);
                    int durationMinutes = Convert.ToInt32(row["DURATIONMINUTES"]);
                    entries.Add(new JObject
                    {
                        ["source"] = "soft1",
                        ["soactionId"] = Convert.ToInt32(row["SOACTION"]),
                        ["soredir"] = soredir,
                        ["typeLabel"] = CrmTypeLabels.TryGetValue(soredir, out var tl) ? tl : ("SOREDIR " + soredir),
                        ["subject"] = row["COMMENTS"] == DBNull.Value ? null : row["COMMENTS"].ToString(),
                        ["start"] = fromDate.ToString("yyyy-MM-ddTHH:mm"),
                        ["end"] = fromDate.AddMinutes(durationMinutes).ToString("yyyy-MM-ddTHH:mm"),
                        ["actStatus"] = actStatus,
                        ["statusLabel"] = CrmActStatusLabels.TryGetValue(actStatus, out var sl) ? sl : ("Status " + actStatus),
                    });
                }
            }
            return entries;
        }

        // "Ολοκλήρωση" κουμπί - ΙΔΙΟ locate+edit+PostData idiom με το ήδη
        // επιβεβαιωμένο RateQaLogSoAction πιο πάνω, ΑΛΛΑ με το ΣΩΣΤΟ object
        // ανά SOREDIR (RateQaLogSoAction hardcoded πάντα "SOTASK" γιατί μόνο
        // εκεί χρησιμοποιείται - εδώ χρειαζόμαστε ΚΑΙ τους 5 τύπους).
        // ΝΕΟ 16/08, ρητό αίτημα χρήστη: (1) γεμίζει και το FINALDATE
        // (ημερομηνία/ώρα ολοκλήρωσης), (2) δέχεται προαιρετική σημείωση
        // από μικρό διάλογο στο UI - μπαίνει ΠΡΙΝ από το ήδη υπάρχον
        // REMARKS, όχι το αντικαθιστά: νέο REMARKS = "<σημείωση>" + 4
        // κενά + παλιό REMARKS (ρητή μορφή, ζητήθηκε ρητά). Διορθώθηκε
        // 16/08 από αρχική αναφορά "COMMENTS" σε "REMARKS" (ο χρήστης
        // το ξεκαθάρισε ο ίδιος - "με συγχωρείς στο remarks θα το κάνεις").
        public static void ExecuteCompleteCrmTask(XSupport xSupport, int soredir, int soactionId, string note = null)
        {
            if (!CrmObjectsBySoredir.TryGetValue(soredir, out string objectName))
                throw new Exception($"Μη έγκυρος τύπος CRM (soredir={soredir}).");

            XModule m = xSupport.CreateModule(objectName);
            XTable soaction = m.GetTable("SOACTION");
            try
            {
                m.LocateData(soactionId);
                soaction.Current.Edit(soactionId);

                // ΝΕΟ 16/08 - defensive check, ζωντανό crash του χρήστη:
                // Soft1 αρνείται (Softone.Interop.S1Exception "ΠΡΟΣΟΧΗ!
                // Ημερομηνία λήξης μικρότερη της ημερομηνίας έναρξης.") να
                // γράψει FINALDATE < FROMDATE - συμβαίνει όταν η εργασία
                // έχει FROMDATE στο μέλλον (προγραμματισμένη) και ο
                // χειριστής προσπαθεί να την ολοκληρώσει νωρίτερα. Αντί να
                // αφήσουμε το ωμό/τεχνικό S1Exception να ανέβει ως έχει
                // μέχρι το UI (ήδη μπλοκαρισμένο ΚΑΙ client-side, βλ.
                // openTaskCompleteModal στο index.html - αυτό εδώ είναι το
                // δεύτερο, αυθεντικό επίπεδο άμυνας), το πιάνουμε ΝΩΡΙΣ και
                // πετάμε φιλικό, ελληνικό μήνυμα - ΠΡΙΝ αγγίξουμε ΚΑΝΕΝΑ
                // πεδίο, ώστε να μη μείνει η εγγραφή σε μισο-edit state.
                object fromDateRaw = soaction.Current["FROMDATE"];
                if (fromDateRaw != null && fromDateRaw != DBNull.Value)
                {
                    DateTime fromDate = Convert.ToDateTime(fromDateRaw);
                    if (fromDate > DateTime.Now)
                    {
                        throw new Exception(
                            $"Η εργασία είναι προγραμματισμένη για το μέλλον ({fromDate:dd/MM/yyyy HH:mm}) - δεν μπορεί να ολοκληρωθεί πριν ξεκινήσει.");
                    }
                }

                soaction.Current["ACTSTATUS"] = 3; // "Ολοκληρώθηκε"
                soaction.Current["FINALDATE"] = DateTime.Now;

                if (!string.IsNullOrWhiteSpace(note))
                {
                    object existingRaw = soaction.Current["REMARKS"];
                    string existing = (existingRaw == null || existingRaw == DBNull.Value) ? "" : existingRaw.ToString();
                    string combined = string.IsNullOrEmpty(existing) ? note : note + "    " + existing;
                    soaction.Current["REMARKS"] = Truncate(combined, 2000); // REMARKS varchar(2000)
                }

                m.PostData();
            }
            finally
            {
                soaction.Dispose();
                m.Dispose();
            }
        }

        // "Επεξεργασία" κουμπί - ΙΔΙΟ AUTOLOCATE idiom με το ExecuteOpenTrader,
        // αλλά για CRM εγγραφή. Λύνει το κενό που σημειώνεται πιο πάνω στο
        // ExecuteCreateCrmRecord (canOpenLink μόνο για SOTASK) - εδώ
        // ανοίγουμε ΚΑΙ τους 5 τύπους σωστά μέσω CrmObjectsBySoredir.
        public static string ExecuteOpenCrmAction(XSupport xSupport, int soredir, int soactionId)
        {
            if (!CrmObjectsBySoredir.TryGetValue(soredir, out string objectName))
                throw new Exception($"Μη έγκυρος τύπος CRM (soredir={soredir}).");

            string command = $"{objectName}[AUTOLOCATE={soactionId}]";
            xSupport.ExecS1Command(command, null);
            DebugLog.Log($"[dashboard_tasks] ExecuteOpenCrmAction objectName={objectName} soactionId={soactionId} command={command}");

            return JsonConvert.SerializeObject(new { success = true, objectName, command });
        }

        // ══════════════════════════════════════════════════════════════════
        // task_search_trader / task_search_user - ΝΕΟ, βοηθητικά για το
        // TASK wizard (deterministic φόρμα, ΟΧΙ chat/AI - βλ. session notes
        // 15/08, index.html "TASK" trigger + JarvisShell.xaml.cs). Καλούνται
        // ΑΠΕΥΘΕΙΑΣ από τη φόρμα, όχι μέσω tool-use loop.
        //
        // Παραμετρικό SQL (:N), ΟΧΙ string interpolation σαν το query_data -
        // εκεί το SQL το γράφει ο Claude (trusted πρόθεση, whitelist
        // ελεγμένο μόνο SELECT), εδώ το κείμενο έρχεται ΑΠΕΥΘΕΙΑΣ από
        // πληκτρολόγηση χειριστή σε πεδίο αναζήτησης - χρειάζεται σωστό
        // escaping, ίδιο επιβεβαιωμένο pattern με JarvisShell.GetDisplayName
        // / S1DocReader's Soft1Bridge.FindTraderByAfm (":1"/":2" positional).
        // ══════════════════════════════════════════════════════════════════

        public static string ExecuteTaskSearchTrader(XSupport xSupport, string text, int sodType)
        {
            var results = new JArray();
            // 12=προμηθευτής/13=πελάτης (ίδια σύμβαση με SODTYPE στο TRDR) -
            // οτιδήποτε άλλο δεν βγάζει νόημα, γύρνα άδεια λίστα αντί να
            // ρίξεις exception (η φόρμα απλά δεν θα δείξει αποτελέσματα).
            if (sodType != 12 && sodType != 13)
                return JsonConvert.SerializeObject(new { results });

            string likePattern = "%" + (text ?? "") + "%";
            XTable t = xSupport.GetSQLDataSet(
                "SELECT TOP 20 TRDR, CODE, NAME, AFM, PHONE01 FROM TRDR " +
                "WHERE SODTYPE = :1 AND ISACTIVE = 1 AND " +
                "(AFM LIKE :2 OR NAME LIKE :3 OR PHONE01 LIKE :4) ORDER BY NAME",
                sodType, likePattern, likePattern, likePattern);

            if (t != null && t.Count > 0)
            {
                DataTable dt = t.CreateDataTable(true);
                foreach (DataRow row in dt.Rows)
                {
                    results.Add(new JObject
                    {
                        ["trdr"] = Convert.ToInt32(row["TRDR"]),
                        ["code"] = row["CODE"] == DBNull.Value ? null : row["CODE"].ToString(),
                        ["name"] = row["NAME"] == DBNull.Value ? null : row["NAME"].ToString(),
                        ["afm"] = row["AFM"] == DBNull.Value ? null : row["AFM"].ToString(),
                        ["phone"] = row["PHONE01"] == DBNull.Value ? null : row["PHONE01"].ToString()
                    });
                }
            }
            return JsonConvert.SerializeObject(new { results });
        }

        public static string ExecuteTaskSearchUser(XSupport xSupport, string text)
        {
            var results = new JArray();
            string likePattern = "%" + (text ?? "") + "%";
            XTable t = xSupport.GetSQLDataSet(
                "SELECT TOP 20 USERS, NAME FROM USERS WHERE NAME LIKE :1 ORDER BY NAME",
                likePattern);

            if (t != null && t.Count > 0)
            {
                DataTable dt = t.CreateDataTable(true);
                foreach (DataRow row in dt.Rows)
                {
                    results.Add(new JObject
                    {
                        ["userId"] = Convert.ToInt32(row["USERS"]),
                        ["name"] = row["NAME"] == DBNull.Value ? null : row["NAME"].ToString()
                    });
                }
            }
            return JsonConvert.SerializeObject(new { results });
        }

        // task_search_inst / task_search_prjc - ΝΕΟ 16/08, ρητό αίτημα
        // χρήστη ("Εγκατάσταση"/"Έργο", ΚΑΙ ΤΑ ΔΥΟ προαιρετικά στο TASK/
        // TASKS wizard) - ΙΔΙΟ idiom με ExecuteTaskSearchTrader/User πιο
        // πάνω. Μόνο ΕΝΕΡΓΑ (ISACTIVE=1), confirmed πεδία από schema.
        public static string ExecuteTaskSearchInst(XSupport xSupport, string text)
        {
            var results = new JArray();
            string likePattern = "%" + (text ?? "") + "%";
            XTable t = xSupport.GetSQLDataSet(
                "SELECT TOP 20 INST, CODE, NAME FROM INST " +
                "WHERE COMPANY=:1 AND ISACTIVE=1 AND (CODE LIKE :2 OR NAME LIKE :3) ORDER BY NAME",
                xSupport.ConnectionInfo.CompanyId, likePattern, likePattern);

            if (t != null && t.Count > 0)
            {
                DataTable dt = t.CreateDataTable(true);
                foreach (DataRow row in dt.Rows)
                {
                    results.Add(new JObject
                    {
                        ["inst"] = Convert.ToInt32(row["INST"]),
                        ["code"] = row["CODE"] == DBNull.Value ? null : row["CODE"].ToString(),
                        ["name"] = row["NAME"] == DBNull.Value ? null : row["NAME"].ToString()
                    });
                }
            }
            return JsonConvert.SerializeObject(new { results });
        }

        public static string ExecuteTaskSearchPrjc(XSupport xSupport, string text)
        {
            var results = new JArray();
            string likePattern = "%" + (text ?? "") + "%";
            XTable t = xSupport.GetSQLDataSet(
                "SELECT TOP 20 PRJC, CODE, NAME FROM PRJC " +
                "WHERE COMPANY=:1 AND ISACTIVE=1 AND (CODE LIKE :2 OR NAME LIKE :3) ORDER BY NAME",
                xSupport.ConnectionInfo.CompanyId, likePattern, likePattern);

            if (t != null && t.Count > 0)
            {
                DataTable dt = t.CreateDataTable(true);
                foreach (DataRow row in dt.Rows)
                {
                    results.Add(new JObject
                    {
                        ["prjc"] = Convert.ToInt32(row["PRJC"]),
                        ["code"] = row["CODE"] == DBNull.Value ? null : row["CODE"].ToString(),
                        ["name"] = row["NAME"] == DBNull.Value ? null : row["NAME"].ToString()
                    });
                }
            }
            return JsonConvert.SerializeObject(new { results });
        }
    }
}
