using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Softone;
using S1Courier.Core;
using S1Courier.Models;

namespace S1Jarvis.Core
{
    // ══════════════════════════════════════════════════════════════════════
    // JarvisCourier - ΝΕΟ 17/08, ρητό αίτημα χρήστη: φέρνει τον S1Courier
    // (έκδοση courier vouchers - ACS/ΕΛΤΑ/Γενική/Courier Center) μέσα στην
    // κουρτίνα "JARVISCOURIER" του Jarvis. Reuse ΑΥΤΟΥΣΙΩΝ των provider
    // classes/models από το S1Courier.dll (βλ. csproj Reference) - ΜΟΝΟ το
    // entitlement (JarvisLicenseGuard + AccessConfig.CourierToolName) και η
    // UI/list μηχανική είναι ξεχωριστά από το standalone S1Courier προϊόν.
    //
    // v1 SCOPE (ρητή απόφαση χρήστη 17/08): ΜΟΝΟ μεμονωμένη έκδοση, ΟΧΙ
    // μαζική (SOTASK/ACTLINES batch flow) - αυτό μένει για αργότερα.
    //
    // ΡΟΗ (ρητά περιγραμμένη από χρήστη):
    //  1. Χειριστής ζητάει παραστατικά με prompt στον agent (chat) - ο
    //     Claude τα βρίσκει με query_data (ελεύθερο SQL, ΟΠΟΙΑΔΗΠΟΤΕ
    //     κριτήρια) και τα δείχνει σε ΛΙΣΤΑ στο κύριο παράθυρο μέσω
    //     show_courier_documents (ΙΔΙΟ idiom με show_calendar_entries -
    //     ο Claude υπολογίζει, το tool απλά ΜΕΤΑΦΕΡΕΙ το αποτέλεσμα, ΔΕΝ
    //     ψάχνει τίποτα μόνο του).
    //  2. Ανά γραμμή, 2 κουμπιά: "Εμφάνιση εγγραφής" (deterministic -
    //     ΞΑΝΑΧΡΗΣΙΜΟΠΟΙΕΙ το ήδη υπάρχον JarvisTools.ExecuteOpenDocument)
    //     και "Δημιουργία Voucher" (deterministic - ανοίγει modal ΙΔΙΟ με
    //     το CourierControl του S1Courier, port 1-προς-1 των πεδίων/
    //     capability-driven enable-disable/validation).
    // ══════════════════════════════════════════════════════════════════════
    internal static class JarvisCourier
    {
        // ── show_courier_documents (LLM tool) ───────────────────────────
        public static readonly object ShowCourierDocumentsToolDefinition = new
        {
            name = "show_courier_documents",
            description =
                "Εμφανίζει ΣΥΓΚΕΚΡΙΜΕΝΑ παραστατικά (που ΗΔΗ βρήκες μέσω " +
                "query_data) ΑΠΕΥΘΕΙΑΣ σε λίστα στο κύριο παράθυρο της " +
                "κουρτίνας Courier - χρησιμοποίησέ το ΠΑΝΤΑ όταν ο " +
                "χειριστής ζητήσει να δει παραστατικά προς αποστολή (π.χ. " +
                "\"δείξε μου τα σημερινά παραστατικά του πελάτη Χ\", " +
                "\"φέρε τα ανεξόφλητα προς αποστολή\"). ΡΟΗ: (1) query_data " +
                "ΠΡΩΤΑ για να βρεις τα FINDOC που ταιριάζουν (JOIN TRDR για " +
                "όνομα/κωδικό πελάτη - π.χ. SELECT F.FINDOC, F.TRNDATE, " +
                "F.FINCODE, F.SOSOURCE, T.CODE AS TRDRCODE, T.NAME AS " +
                "TRDRNAME FROM FINDOC F JOIN TRDR T ON T.TRDR=F.TRDR WHERE " +
                "..., δες ΓΝΩΣΤΟ SCHEMA για τα υπόλοιπα πεδία FINDOC), " +
                "(2) κάλεσε ΑΥΤΟ το tool με τα αποτελέσματα. ΜΗΝ απαντήσεις " +
                "ΜΕ ΛΙΣΤΑ μέσα στο chat - ο χειριστής βλέπει το αποτέλεσμα " +
                "ΑΠΕΥΘΕΙΑΣ στο κύριο παράθυρο, απλά επιβεβαίωσε ΣΥΝΤΟΜΑ.",
            input_schema = new
            {
                type = "object",
                properties = new
                {
                    entries = new
                    {
                        type = "array",
                        description = "Τα παραστατικά που θα εμφανιστούν - ΑΚΡΙΒΩΣ αυτά, καμία επιπλέον επεξεργασία/φιλτράρισμα από το backend.",
                        items = new
                        {
                            type = "object",
                            properties = new
                            {
                                findocId = new { type = "integer", description = "FINDOC.FINDOC (id)." },
                                date = new { type = "string", description = "Ημερομηνία παραστατικού (π.χ. '2026-08-17')." },
                                fincode = new { type = "string", description = "Κωδικός παραστατικού (FINDOC.FINCODE)." },
                                sosource = new { type = "integer", description = "FINDOC.SOSOURCE - χρειάζεται για το κουμπί 'Εμφάνιση εγγραφής'." },
                                trdrCode = new { type = "string", description = "Κωδικός πελάτη (TRDR.CODE)." },
                                trdrName = new { type = "string", description = "Επωνυμία πελάτη (TRDR.NAME)." }
                            },
                            required = new[] { "findocId", "date", "fincode", "sosource", "trdrCode", "trdrName" }
                        }
                    }
                },
                required = new[] { "entries" }
            }
        };

        public static string ExecuteShowCourierDocuments(JObject input, Action<JArray> onShowCourierDocuments)
        {
            var entries = input?["entries"] as JArray ?? new JArray();
            onShowCourierDocuments?.Invoke(entries);
            return JsonConvert.SerializeObject(new { success = true, count = entries.Count });
        }

        // ── cancel_courier_voucher (LLM tool) - ΝΕΟ 18/08, ρητό αίτημα
        // χρήστη: "θα πρέπει να μπορεί να το κάνει [ακύρωση] και από chat".
        // ΔΕΝ ψάχνει τίποτα μόνο του - ο Claude ΗΔΗ έχει βρει τις 4 τιμές
        // μέσω query_data (FINDOC.VARCHAR01/VARCHAR02/CCCCOURJOBID/FINDOC)
        // ΠΡΙΝ καλέσει αυτό, ίδιο idiom με show_courier_documents. Η
        // ΥΠΟΧΡΕΩΤΙΚΗ επιβεβαίωση (❓/> quick-reply, "ΠΟΤΕ στο ίδιο turn που
        // εμφανίζεις το παραστατικό") επιβάλλεται από το system prompt
        // (βλ. BuildSystemPrompt), ΟΧΙ εδώ - το tool απλά εκτελεί.
        public static readonly object CancelCourierVoucherToolDefinition = new
        {
            name = "cancel_courier_voucher",
            description =
                "Ακυρώνει ΥΠΑΡΧΟΥΣΑ αποστολή courier - χρησιμοποίησέ το ΜΟΝΟ " +
                "αφού (1) βρήκες το παραστατικό με query_data, (2) το έδειξες " +
                "με show_courier_documents, ΚΑΙ (3) ο χειριστής επιβεβαίωσε " +
                "ΡΗΤΑ σε ΕΠΟΜΕΝΟ μήνυμα (π.χ. \"ναι\") ΜΕΤΑ από ερώτηση " +
                "❓/> quick-reply δική σου. ΠΟΤΕ μην καλέσεις αυτό το tool " +
                "στο ΙΔΙΟ turn που βρήκες/έδειξες το παραστατικό - πάντα " +
                "πρώτα ρώτα, περίμενε απάντηση.",
            input_schema = new
            {
                type = "object",
                properties = new
                {
                    findocId = new { type = "integer", description = "FINDOC.FINDOC - για να καθαριστούν τα VARCHAR01/VARCHAR02/CCCCOURJOBID μετά την επιτυχή ακύρωση." },
                    providerName = new { type = "string", description = "ΑΚΡΙΒΩΣ η τιμή FINDOC.VARCHAR02 (όνομα courier - π.χ. \"ACS Courier\" - ΟΧΙ code)." },
                    shipmentNumber = new { type = "string", description = "FINDOC.VARCHAR01 (αριθμός αποστολής)." },
                    jobId = new { type = "string", description = "FINDOC.CCCCOURJOBID αν υπάρχει, αλλιώς παράλειψέ το." }
                },
                required = new[] { "findocId", "providerName", "shipmentNumber" }
            }
        };

        public static async Task<string> ExecuteCancelCourierVoucherChatAsync(XSupport xSupport, JObject input)
        {
            int? findocId = (int?)input?["findocId"];
            string providerName = input?["providerName"]?.ToString();
            string shipmentNumber = input?["shipmentNumber"]?.ToString();
            string jobId = input?["jobId"]?.ToString();

            JObject result = await CancelVoucherAsync(xSupport, providerName, shipmentNumber, jobId, findocId);
            return result.ToString(Newtonsoft.Json.Formatting.None);
        }

        // ── get_courier_voucher_data (LLM tool) - ΝΕΟ 18/08, ρητό αίτημα
        // χρήστη: έκδοση voucher μέσω chat (χωρίς modal). Wraps ΑΥΤΟΥΣΙΑ τα
        // ήδη υπάρχοντα BuildRequestFromFindoc/LoadActiveProviders - ΙΔΙΑ
        // δεδομένα με αυτά που βλέπει ο χειριστής στο modal (καμία δεύτερη
        // λογική/SQL στο system prompt - ο Claude ΔΕΝ μαντεύει στοιχεία
        // παραλήπτη/capability flags, τα παίρνει ΑΠΟ ΕΔΩ).
        public static readonly object GetCourierVoucherDataToolDefinition = new
        {
            name = "get_courier_voucher_data",
            description =
                "Φέρνει τα ήδη γνωστά στοιχεία αποστολέα/παραλήπτη/βάρους " +
                "ΚΑΙ τη λίστα ενεργών courier providers (με τα capability " +
                "flags τους - ποιος υποστηρίζει επιταγή/Σάββατο/ώρα " +
                "παράδοσης) για ΕΝΑ παραστατικό - χρησιμοποίησέ το ΠΡΩΤΟ " +
                "όταν ο χειριστής ζητήσει έκδοση voucher μέσω chat (π.χ. " +
                "\"έκδωσε voucher για το παραστατικό 245\"). Το αποτέλεσμα " +
                "δείχνει τι ΕΙΝΑΙ ήδη γνωστό - χρησιμοποίησέ το για να " +
                "αποφασίσεις τι ΛΕΙΠΕΙ/είναι ασαφές και χρειάζεται ερώτηση " +
                "στον χειριστή (ΠΟΤΕ μην υποθέσεις courier/ΑΚ/επιταγή μόνος σου).",
            input_schema = new
            {
                type = "object",
                properties = new
                {
                    findocId = new { type = "integer", description = "FINDOC.FINDOC (id)." }
                },
                required = new[] { "findocId" }
            }
        };

        public static string ExecuteGetCourierVoucherData(XSupport xSupport, JObject input)
        {
            int findocId = (int)input["findocId"];
            JObject request = BuildRequestFromFindoc(xSupport, findocId);
            JArray providers = LoadActiveProviders(xSupport);
            return new JObject { ["request"] = request, ["providers"] = providers }
                .ToString(Newtonsoft.Json.Formatting.None);
        }

        // ── create_courier_voucher (LLM tool) - ΝΕΟ 18/08, ρητό αίτημα
        // χρήστη: "εφόσον τα επιβεβαιώσει με τον χειριστή, τότε να εκδίδει
        // το voucher". ΙΔΙΟ input schema με το payload του modal (βλ.
        // index.html courierVoucherCreateBtn) - reuse ΑΥΤΟΥΣΙΟ το
        // CreateVoucherAsync (διαβάζει από JObject με τα ίδια keys,
        // αδιάφορο αν ήρθε από chat tool_use ή από postCommand). ΕΠΙΠΛΕΟΝ
        // (μόνο εδώ, ΟΧΙ στο modal path): το PDF ΑΠΟΘΗΚΕΥΕΤΑΙ στο δίσκο
        // (ΟΧΙ base64/iframe - δεν υπάρχει modal σε αυτό το flow) με ΙΔΙΟ
        // idiom "Clickable export path" (βλ. JarvisTools.
        // BuildDirectExportPath) ώστε ο Claude να απαντήσει με clickable
        // link `[shipmentNumber.pdf](path)` στο chat.
        public static readonly object CreateCourierVoucherToolDefinition = new
        {
            name = "create_courier_voucher",
            description =
                "Εκδίδει ΝΕΑ αποστολή courier - χρησιμοποίησέ το ΜΟΝΟ αφού " +
                "(1) κάλεσες get_courier_voucher_data, (2) ρώτησες ό,τι " +
                "έλειπε/ήταν ασαφές (❓/> quick-reply - ποιος courier, " +
                "αντικαταβολή/επιταγή, βάρος/τεμάχια αν δεν είσαι σίγουρος " +
                "για τα defaults), ΚΑΙ (3) ο χειριστής επιβεβαίωσε ΡΗΤΑ σε " +
                "ΕΠΟΜΕΝΟ μήνυμα μια ΤΕΛΙΚΗ σύνοψη (courier + παραλήπτης + " +
                "ΑΚ/επιταγή). ΠΟΤΕ μην το καλέσεις στο ΙΔΙΟ turn με το " +
                "get_courier_voucher_data. Μετά την επιτυχία, ΞΑΝΑΚΑΛΕΣΕ " +
                "ΥΠΟΧΡΕΩΤΙΚΑ το show_courier_documents (ίδιο παραστατικό) " +
                "ώστε να ενημερωθεί η λίστα στο κύριο παράθυρο, ΚΑΙ απάντησε " +
                "ΜΟΝΟ με τον κωδικό voucher σαν clickable link (χρησιμοποίησε " +
                "ΑΚΡΙΒΩΣ το pdfLink πεδίο του αποτελέσματος, μην το ξαναφτιάχνεις).",
            input_schema = new
            {
                type = "object",
                properties = new
                {
                    documentNumber = new { type = "string", description = "FINDOC.FINDOC ως string (ίδιο findocId με το get_courier_voucher_data)." },
                    documentRef = new { type = "string", description = "FINDOC.FINCODE (από το request.documentRef του get_courier_voucher_data)." },
                    providerCode = new { type = "string", description = "providerCode από τη λίστα του get_courier_voucher_data - ΡΗΤΑ επιβεβαιωμένο από τον χειριστή." },
                    senderName = new { type = "string" }, senderAddress = new { type = "string" },
                    senderCity = new { type = "string" }, senderZipCode = new { type = "string" }, senderPhone = new { type = "string" },
                    receiverName = new { type = "string", description = "Υποχρεωτικό." },
                    receiverContactName = new { type = "string" },
                    receiverAddress = new { type = "string", description = "Υποχρεωτικό." },
                    receiverCity = new { type = "string" },
                    receiverZipCode = new { type = "string", description = "Υποχρεωτικό." },
                    receiverPhone = new { type = "string" },
                    pieces = new { type = "integer", description = "Υποχρεωτικό, >0." },
                    weight = new { type = "number", description = "Υποχρεωτικό, >0." },
                    comments = new { type = "string" },
                    isCOD = new { type = "boolean" },
                    codAmount = new { type = "number" },
                    codPaymentType = new { type = "integer", description = "0=Μετρητά, 1=Επιταγή." },
                    codChequeDate = new { type = "string", description = "YYYY-MM-DD, ΥΠΟΧΡΕΩΤΙΚΟ αν isCOD+codPaymentType=1 ΚΑΙ ο provider υποστηρίζει επιταγή (supportsCodChequeDate)." },
                    deliveryTimeRequested = new { type = "boolean" },
                    deliveryTimeFrom = new { type = "string", description = "HH:mm" },
                    deliveryTimeTo = new { type = "string", description = "HH:mm" },
                    deliveryDate = new { type = "string", description = "YYYY-MM-DD" },
                    saturdayDelivery = new { type = "boolean" }
                },
                required = new[] { "documentNumber", "providerCode", "receiverName", "receiverAddress", "receiverZipCode", "pieces", "weight" }
            }
        };

        public static async Task<string> ExecuteCreateCourierVoucherChatAsync(XSupport xSupport, JObject input)
        {
            JObject result = await CreateVoucherAsync(xSupport, input);

            if (result["success"]?.Value<bool>() == true)
            {
                try
                {
                    string providerCode = result["providerCode"]?.ToString();
                    string shipmentNumber = result["shipmentNumber"]?.ToString();
                    JObject pdfResult = await GetVoucherPdfAsync(xSupport, providerCode, shipmentNumber);
                    byte[] pdfBytes = Convert.FromBase64String(pdfResult["pdfBase64"]?.ToString() ?? "");
                    string path = SaveVoucherPdfToDisk(pdfBytes, shipmentNumber);
                    result["pdfPath"] = path;
                    // Mini-markdown link convention ΤΟΥ ΙΔΙΟΥ chat (βλ.
                    // index.html mini-markdown parser + JarvisShell
                    // WebMessageReceived "Process.Start σε κλικ" - ΙΔΙΟ με
                    // Phase 3 exports) - ο Claude ΤΟ ΑΝΤΙΓΡΑΦΕΙ αυτούσιο,
                    // δεν το ξαναφτιάχνει (βλ. tool description).
                    result["pdfLink"] = $"[{shipmentNumber}.pdf]({path})";
                }
                catch (Exception ex)
                {
                    // Η αποστολή δημιουργήθηκε επιτυχώς - αποτυχία λήψης/
                    // αποθήκευσης PDF ΔΕΝ πρέπει να εμφανιστεί σαν αποτυχία
                    // δημιουργίας. Ο Claude θα πει "δημιουργήθηκε, αλλά δεν
                    // βρήκα το PDF" (χωρίς link) αντί να μπερδέψει.
                    DebugLog.Log("[courier] ExecuteCreateCourierVoucherChatAsync PDF save EXCEPTION: " + ex);
                    result["pdfPath"] = null;
                    result["pdfLink"] = null;
                }
            }

            return result.ToString(Newtonsoft.Json.Formatting.None);
        }

        // Ίδιο path convention με JarvisTools.BuildDirectExportPath (Έγγραφα\
        // Jarvis Exports\{filename}_{timestamp}.{ext}) - ΞΕΧΩΡΙΣΤΟ, μικρό
        // αντίγραφο εδώ αντί για cross-file exposure, ίδιο σκεπτικό/σχόλιο
        // με εκεί (κρατάει το JarvisCourier αυτόνομο για το δικό του write path).
        private static string SaveVoucherPdfToDisk(byte[] pdfBytes, string shipmentNumber)
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "Jarvis Exports");
            Directory.CreateDirectory(dir);

            string safeName = string.Join("_",
                (string.IsNullOrWhiteSpace(shipmentNumber) ? "voucher" : shipmentNumber)
                    .Split(Path.GetInvalidFileNameChars()));
            string stamped = $"{safeName}_{DateTime.Now:yyyyMMdd_HHmmss}.pdf";
            string path = Path.Combine(dir, stamped);
            File.WriteAllBytes(path, pdfBytes);
            return path;
        }

        // ── Deterministic: build ShipmentRequest από ΕΝΑ FINDOC ─────────
        // Port του SALDOC.BuildRequestFromDocument/TryFillReceiverFromParam/
        // BuildReceiverHardcoded (S1Courier) - ΙΔΙΑ ΑΚΡΙΒΩΣ λογική
        // (δυναμικό mapping παραλήπτη μέσω cccParams 500002, αλλιώς
        // hardcoded fallback από FINDOC/MTRDOC/TRDR· αποστολέας πάντα από
        // BRANCH). Επιστρέφει JObject με τα ίδια πεδία με το ShipmentRequest
        // (camelCase) - το JS το χρησιμοποιεί για να γεμίσει το modal.
        private const int ParamReceiverMapping = 500002;

        public static JObject BuildRequestFromFindoc(XSupport xSupport, int findocId)
        {
            XTable t = xSupport.GetSQLDataSet(
                "SELECT F.FINDOC, F.FINCODE, F.TRDR, F.SUMAMNT, F.PAYMENT, " +
                "F.VARCHAR01, F.VARCHAR02, F.CCCCOURJOBID, F.CCCSNDCONTACTNAME, " +
                "M.SHIPPINGADDR, M.SHPCITY, M.SHPZIP, M.CCCRECEIVER, M.CCCPHONE " +
                "FROM FINDOC F LEFT JOIN MTRDOC M ON M.FINDOC = F.FINDOC " +
                "WHERE F.FINDOC = :1",
                findocId);
            if (t == null || t.Count == 0)
                throw new Exception($"Δεν βρέθηκε παραστατικό με FINDOC={findocId}.");

            var request = new JObject
            {
                ["documentNumber"] = findocId.ToString(),
                ["documentRef"] = t.Current["FINCODE"]?.ToString(),
                ["existingShipmentNumber"] = ColSafe(t, "VARCHAR01"),
                ["existingProviderCode"] = ColSafe(t, "VARCHAR02"),
                ["existingJobId"] = ColSafe(t, "CCCCOURJOBID"),
                ["paymentCode"] = ColSafe(t, "PAYMENT"),
                ["documentAmount"] = t.Current["SUMAMNT"] == DBNull.Value ? 0 : Convert.ToDecimal(t.Current["SUMAMNT"])
            };

            // ── Παραλήπτης: δυναμικά από cccParams 500002, αλλιώς hardcoded ──
            if (!TryFillReceiverFromParam(xSupport, request, findocId))
                BuildReceiverHardcoded(xSupport, request, t);

            // ── Αποστολέας: πάντα από BRANCH ──
            XTable branchData = xSupport.GetSQLDataSet(
                "SELECT * FROM BRANCH WHERE COMPANY = :1 AND BRANCH = :2",
                xSupport.ConnectionInfo.CompanyId,
                xSupport.ConnectionInfo.BranchId);
            if (branchData != null && branchData.Count > 0)
            {
                request["senderName"] = ColSafe(branchData, "NAME");
                request["senderAddress"] = ColSafe(branchData, "ADDRESS");
                request["senderCity"] = ColSafe(branchData, "CITY");
                request["senderZipCode"] = ColSafe(branchData, "ZIP");
                request["senderPhone"] = ColSafe(branchData, "PHONE1");
            }

            return request;
        }

        private static bool TryFillReceiverFromParam(XSupport xSupport, JObject request, int findocId)
        {
            try
            {
                XTable pds = xSupport.GetSQLDataSet(
                    "SELECT TOP 1 ParamValueString FROM cccParams WHERE ParamCode = :1 AND ISACTIVE = 1",
                    ParamReceiverMapping);
                if (pds == null || pds.Count == 0) return false;

                string query = pds.Current["ParamValueString"]?.ToString();
                if (string.IsNullOrWhiteSpace(query)) return false;

                string q = query.Trim();
                if (!q.StartsWith("SELECT", StringComparison.OrdinalIgnoreCase)) return false;
                string qNoTrailing = q.TrimEnd(';', ' ', '\r', '\n', '\t');
                if (qNoTrailing.Contains(";")) return false;

                XTable ds = xSupport.GetSQLDataSet(qNoTrailing, findocId.ToString());
                if (ds == null || ds.Count == 0) return false;

                request["receiverName"] = ColSafe(ds, "ReceiverName");
                request["receiverContactName"] = ColSafe(ds, "ReceiverContactName");
                request["receiverAddress"] = ColSafe(ds, "ReceiverAddress");
                request["receiverCity"] = ColSafe(ds, "ReceiverCity");
                request["receiverZipCode"] = ColSafe(ds, "ReceiverZipCode");
                request["receiverPhone"] = ColSafe(ds, "ReceiverPhone");

                string piecesStr = ColSafe(ds, "Pieces");
                request["pieces"] = int.TryParse(piecesStr, out int p) ? p : 1;

                string weightStr = ColSafe(ds, "Weight");
                request["weight"] = double.TryParse(weightStr, out double w) ? w : 1;

                return true;
            }
            catch
            {
                return false;
            }
        }

        private static void BuildReceiverHardcoded(XSupport xSupport, JObject request, XTable findocRow)
        {
            XTable trdrData = xSupport.GetSQLDataSet(
                "SELECT * FROM TRDR WHERE TRDR = :1", findocRow.Current["TRDR"]?.ToString());

            string contactName = ColSafe(findocRow, "CCCSNDCONTACTNAME");
            if (string.IsNullOrWhiteSpace(contactName))
                contactName = ColSafe(findocRow, "CCCRECEIVER");
            request["receiverContactName"] = contactName;

            request["receiverName"] = trdrData != null && trdrData.Count > 0 ? ColSafe(trdrData, "NAME") : null;
            request["receiverAddress"] = ColSafe(findocRow, "SHIPPINGADDR");
            request["receiverCity"] = ColSafe(findocRow, "SHPCITY");
            request["receiverZipCode"] = ColSafe(findocRow, "SHPZIP");

            string phone = trdrData != null && trdrData.Count > 0 ? ColSafe(trdrData, "PHONE01") : null;
            if (string.IsNullOrWhiteSpace(phone))
                phone = ColSafe(findocRow, "CCCPHONE");
            request["receiverPhone"] = phone;

            request["pieces"] = 1;
            request["weight"] = 1;
        }

        private static string ColSafe(XTable t, string col)
        {
            try
            {
                var v = t.Current[col];
                return (v == null || v == DBNull.Value) ? null : v.ToString();
            }
            catch { return null; }
        }

        // ── Deterministic: ενεργοί providers + capability flags ─────────
        // Port του CourierControl.LoadProviders (πίνακας CCCCRPROV) - ΑΛΛΑ
        // ΕΠΙΠΛΕΟΝ instantiate ΚΑΘΕ provider μέσω CourierProviderFactory
        // ώστε να διαβάσουμε τα ΠΡΑΓΜΑΤΙΚΑ capability flags (SupportsCod
        // ChequeDate κ.λπ.) από τις ίδιες τις provider classes - ΚΑΜΙΑ
        // δεύτερη/hardcoded αντιγραφή τους εδώ, το JS τα διαβάζει από το
        // JSON και ενεργοποιεί/απενεργοποιεί controls ΑΚΡΙΒΩΣ όπως το WPF.
        public static JArray LoadActiveProviders(XSupport xSupport)
        {
            var result = new JArray();
            XTable ds = xSupport.GetSQLDataSet(
                "SELECT * FROM CCCCRPROV WHERE ISACTIVE = 1 AND COMPANY = :1 AND BRANCH = :2 ORDER BY PROVNAME",
                xSupport.ConnectionInfo.CompanyId,
                xSupport.ConnectionInfo.BranchId);
            if (ds == null) return result;

            DataTable dt = ds.CreateDataTable(true);
            foreach (DataRow row in dt.Rows)
            {
                var config = ConfigFromRow(row);
                bool isDefault = row["ISDEFAULT"] != DBNull.Value && Convert.ToInt32(row["ISDEFAULT"]) == 1;

                var entry = new JObject
                {
                    ["providerCode"] = config.ProviderCode,
                    ["providerName"] = config.ProviderName,
                    ["isDefault"] = isDefault,
                    ["printType"] = config.PrintType,
                    ["printTemplate"] = config.PrintTemplate,
                    ["codPaywayCode"] = config.CodPaywayCode,
                    // ΝΕΟ 18/08, ρητό αίτημα χρήστη - CCCTRACKINGURL (custom
                    // Designer field, VarChar(500)) στο CCCCRPROV, ΟΧΙ στο
                    // CourierProviderConfig (τύπος από το S1Courier.dll -
                    // δεν μπορούμε να προσθέσουμε πεδίο εκεί, οπότε διάβασμα
                    // ΑΠΕΥΘΕΙΑΣ από το DataRow, ίδιο idiom με CCCSUBCODE στο
                    // ConfigFromRow). URL template με placeholder "{NUMBER}"
                    // - το JS το αντικαθιστά με τον αριθμό αποστολής και
                    // ανοίγει το αποτέλεσμα στην καρτέλα Browser (ρητή
                    // απόφαση χρήστη: tracking = Browser mode, ΟΧΙ ξεχωριστό
                    // UI/API integration ανά courier).
                    ["trackingUrlTemplate"] = row.Table.Columns.Contains("CCCTRACKINGURL")
                        ? row["CCCTRACKINGURL"] as string
                        : null
                };

                try
                {
                    ICourierProvider provider = CourierProviderFactory.Create(config);
                    entry["supportsCodChequeDate"] = provider.SupportsCodChequeDate;
                    entry["supportsDeliveryTimeWindow"] = provider.SupportsDeliveryTimeWindow;
                    entry["supportsDeliveryTimeRange"] = provider.SupportsDeliveryTimeRange;
                    entry["supportsSaturdayDelivery"] = provider.SupportsSaturdayDelivery;
                    entry["supportsDeliveryDate"] = provider.SupportsDeliveryDate;
                }
                catch (Exception ex)
                {
                    // Provider config λάθος/μη υποστηριζόμενος - ο χειριστής
                    // απλά δεν θα μπορεί να τον επιλέξει σωστά, ΔΕΝ σκάει
                    // ολόκληρη η λίστα providers γι' αυτό.
                    DebugLog.Log($"[courier] LoadActiveProviders CourierProviderFactory.Create({config.ProviderCode}) EXCEPTION: {ex.Message}");
                    entry["supportsCodChequeDate"] = false;
                    entry["supportsDeliveryTimeWindow"] = false;
                    entry["supportsDeliveryTimeRange"] = false;
                    entry["supportsSaturdayDelivery"] = false;
                    entry["supportsDeliveryDate"] = false;
                }

                result.Add(entry);
            }
            return result;
        }

        private static CourierProviderConfig ConfigFromRow(DataRow row) => new CourierProviderConfig
        {
            ID = row["CCCCRPROV"] == DBNull.Value ? 0 : Convert.ToInt32(row["CCCCRPROV"]),
            ProviderCode = row["PROVCODE"] as string,
            ProviderName = row["PROVNAME"] as string,
            ApiUrl = row["APIURL"] as string,
            ApiKey = row["APIKEY"] as string,
            UserAlias = row["USERALIAS"] as string,
            CredentialValue = row["CREDVALUE"] as string,
            AccountCode = row["ACCOUNTCODE"] as string,
            CompanyId = row["COMPANYID"] as string,
            CompanyPassword = row["COMPANYPASS"] as string,
            UserId = row["USERID"] as string,
            UserPassword = row["USERPASS"] as string,
            BillingCode = row["BILLCODE"] as string,
            CodPaywayCode = row["CODPAYWAY"] as string,
            Company = row["COMPANY"] == DBNull.Value ? 0 : Convert.ToInt32(row["COMPANY"]),
            Branch = row["BRANCH"] == DBNull.Value ? 0 : Convert.ToInt32(row["BRANCH"]),
            IsDefault = row["ISDEFAULT"] != DBNull.Value && Convert.ToInt32(row["ISDEFAULT"]) == 1,
            PrintType = row["PRINTTYPE"] as string,
            PrintTemplate = row["PRINTTEMPLATE"] as string,
            SubCode = row.Table.Columns.Contains("CCCSUBCODE") ? row["CCCSUBCODE"] as string : null,
            CustCode = row["CUSTCODE"] as string,
            CustUser = row["CUSTUSER"] as string,
            CustPass = row["CUSTPASS"] as string,
            PelCode = row["PELCODE"] as string,
            TwoStepAuth = row["TWOSTEPAUTH"] != DBNull.Value && Convert.ToInt32(row["TWOSTEPAUTH"]) == 1,
            MaxBatch = row.Table.Columns.Contains("MAXBATCH") && row["MAXBATCH"] != DBNull.Value ? Convert.ToInt32(row["MAXBATCH"]) : 0
        };

        private static CourierProviderConfig GetProviderConfigByCode(XSupport xSupport, string providerCode)
        {
            XTable ds = xSupport.GetSQLDataSet(
                "SELECT * FROM CCCCRPROV WHERE PROVCODE = :1 AND ISACTIVE = 1 AND COMPANY = :2 AND BRANCH = :3",
                providerCode,
                xSupport.ConnectionInfo.CompanyId,
                xSupport.ConnectionInfo.BranchId);
            if (ds == null || ds.Count == 0)
                throw new Exception($"Δεν βρέθηκε ενεργός courier provider: {providerCode}");
            return ConfigFromRow(ds.CreateDataTable(true).Rows[0]);
        }

        // ΝΕΟ - χρειάζεται ΜΟΝΟ για το Cancel: το FINDOC.VARCHAR02 (γραφμένο
        // στο CreateVoucherAsync/το πραγματικό CourierControl.btnCreate_Click)
        // κρατάει το ProviderNAME (π.χ. "ACS Courier"), ΟΧΙ το ProviderCode -
        // ΙΔΙΟ idiom με το πραγματικό S1Courier (_activeProvider.ProviderName).
        // Το πραγματικό CourierControl δεν χρειάζεται ποτέ αυτό το lookup
        // γιατί το Cancel εκεί βασίζεται στο _activeProvider που μένει στη
        // μνήμη μέσα στην ίδια session UI - ο Jarvis ΔΕΝ έχει τέτοιο in-memory
        // state ανά παραστατικό, οπότε πρέπει να ξαναβρει τον provider από τη
        // βάση με βάση το όνομα που όντως είναι αποθηκευμένο.
        private static CourierProviderConfig GetProviderConfigByName(XSupport xSupport, string providerName)
        {
            XTable ds = xSupport.GetSQLDataSet(
                "SELECT * FROM CCCCRPROV WHERE PROVNAME = :1 AND ISACTIVE = 1 AND COMPANY = :2 AND BRANCH = :3",
                providerName,
                xSupport.ConnectionInfo.CompanyId,
                xSupport.ConnectionInfo.BranchId);
            if (ds == null || ds.Count == 0)
                throw new Exception($"Δεν βρέθηκε ενεργός courier provider με όνομα: {providerName}");
            return ConfigFromRow(ds.CreateDataTable(true).Rows[0]);
        }

        // ── Deterministic: δημιουργία αποστολής (port btnCreate_Click) ──
        // Server-side validation ΕΠΙΠΛΕΟΝ (ΟΧΙ αντί) του client-side JS -
        // ΠΟΤΕ να μην εμπιστευόμαστε ΜΟΝΟ το UI, ίδιο σκεπτικό με ΚΑΘΕ
        // άλλο write tool σε αυτό το project.
        public static async Task<JObject> CreateVoucherAsync(XSupport xSupport, JObject input)
        {
            string providerCode = input?["providerCode"]?.ToString();
            if (string.IsNullOrWhiteSpace(providerCode))
                throw new Exception("Λείπει το providerCode.");

            string receiverName = input?["receiverName"]?.ToString();
            string receiverAddress = input?["receiverAddress"]?.ToString();
            string receiverZip = input?["receiverZipCode"]?.ToString();
            if (string.IsNullOrWhiteSpace(receiverName) || string.IsNullOrWhiteSpace(receiverAddress) || string.IsNullOrWhiteSpace(receiverZip))
                throw new Exception("Συμπληρώστε τα υποχρεωτικά στοιχεία παραλήπτη (επωνυμία/διεύθυνση/ΤΚ).");

            int pieces = (int?)input?["pieces"] ?? 1;
            double weight = (double?)input?["weight"] ?? 0;
            if (weight <= 0) throw new Exception("Το βάρος αποστολής πρέπει να είναι μεγαλύτερο από 0.");
            if (pieces <= 0) throw new Exception("Ο αριθμός τεμαχίων πρέπει να είναι μεγαλύτερος από 0.");

            var config = GetProviderConfigByCode(xSupport, providerCode);
            ICourierProvider provider = CourierProviderFactory.Create(config);

            var request = new ShipmentRequest
            {
                SenderName = input?["senderName"]?.ToString(),
                SenderAddress = input?["senderAddress"]?.ToString(),
                SenderCity = input?["senderCity"]?.ToString(),
                SenderZipCode = input?["senderZipCode"]?.ToString(),
                SenderPhone = input?["senderPhone"]?.ToString(),

                ReceiverName = receiverName,
                ReceiverContactName = input?["receiverContactName"]?.ToString(),
                ReceiverAddress = receiverAddress,
                ReceiverCity = input?["receiverCity"]?.ToString(),
                ReceiverZipCode = receiverZip,
                ReceiverPhone = input?["receiverPhone"]?.ToString(),

                Pieces = pieces,
                Weight = weight,
                Comments = input?["comments"]?.ToString(),
                IsCOD = (bool?)input?["isCOD"] ?? false,
                CODAmount = (decimal?)input?["codAmount"] ?? 0,
                CODPaymentType = (int?)input?["codPaymentType"] ?? 0,
                CODChequeDate = (DateTime?)input?["codChequeDate"],

                DeliveryTimeRequested = (bool?)input?["deliveryTimeRequested"] ?? false,
                DeliveryTimeFrom = TryParseTime(input?["deliveryTimeFrom"]?.ToString()),
                DeliveryTimeTo = TryParseTime(input?["deliveryTimeTo"]?.ToString()),
                DeliveryDate = (DateTime?)input?["deliveryDate"],
                SaturdayDelivery = (bool?)input?["saturdayDelivery"] ?? false,

                DocumentNumber = input?["documentNumber"]?.ToString(),
                DocumentRef = input?["documentRef"]?.ToString()
            };

            ShipmentResult result = await provider.CreateShipmentAsync(request);

            if (result.Success)
            {
                // Γράφει πίσω στο FINDOC - ΑΚΡΙΒΩΣ το ίδιο idiom με
                // CourierControl.btnCreate_Click (literal NULL μέσα στο SQL
                // όταν λείπει το JobId - ΟΧΙ DBNull.Value bound param, το
                // OLE binding layer της SoftOne το χειρίζεται λάθος).
                // documentNumber είναι string (ΙΔΙΟ πεδίο με το
                // BuildRequestFromFindoc πιο πάνω) - parse αντί για
                // ξεχωριστό "documentNumberInt" πεδίο. Κενό/απόν όταν η
                // αποστολή είναι ΧΩΡΙΣ παραστατικό (ρητό αίτημα χρήστη,
                // v1 scope) - το write-back απλά παραλείπεται τότε.
                int? findocId = int.TryParse(input?["documentNumber"]?.ToString(), out var fid) ? fid : (int?)null;
                if (findocId.HasValue && findocId.Value > 0)
                {
                    if (long.TryParse(result.ProviderJobId, out var jobIdValue))
                    {
                        xSupport.ExecuteSQL(
                            "UPDATE FINDOC SET VARCHAR01=:1, VARCHAR02=:2, CCCCOURJOBID=:3 WHERE FINDOC=:4",
                            result.ShipmentNumber, config.ProviderName, jobIdValue, findocId.Value);
                    }
                    else
                    {
                        xSupport.ExecuteSQL(
                            "UPDATE FINDOC SET VARCHAR01=:1, VARCHAR02=:2, CCCCOURJOBID=NULL WHERE FINDOC=:3",
                            result.ShipmentNumber, config.ProviderName, findocId.Value);
                    }
                }
            }

            return new JObject
            {
                ["success"] = result.Success,
                ["shipmentNumber"] = result.ShipmentNumber,
                ["trackingNumber"] = result.TrackingNumber,
                ["errorMessage"] = result.ErrorMessage,
                ["providerCode"] = config.ProviderCode,
                // ΝΕΟ - ώστε το modal να μπορεί να δείξει αμέσως το κουμπί
                // "Ακύρωση Voucher" μετά από επιτυχή δημιουργία, χωρίς να
                // χρειάζεται re-load από τη βάση. providerName ΕΠΙΤΗΔΕΣ (όχι
                // code) - βλ. GetProviderConfigByName για το γιατί.
                ["providerName"] = config.ProviderName,
                ["jobId"] = result.ProviderJobId
            };
        }

        // ── Deterministic: ακύρωση αποστολής (port btnCancelShipment_Click) ─
        // findocId: για το write-back (VARCHAR01/VARCHAR02/CCCCOURJOBID=NULL) -
        // ίδιο idiom με το CreateVoucherAsync πιο πάνω, ΜΟΝΟ όταν η αποστολή
        // έχει παραστατικό (v1 δεν έχει ακόμα cancel για standalone voucher
        // χωρίς παραστατικό - δεν υπάρχει πού να γραφτεί το NULL).
        public static async Task<JObject> CancelVoucherAsync(XSupport xSupport, string providerName, string shipmentNumber, string jobId, int? findocId)
        {
            if (string.IsNullOrWhiteSpace(providerName))
                throw new Exception("Λείπει ο courier (providerName).");
            if (string.IsNullOrWhiteSpace(shipmentNumber))
                throw new Exception("Δεν υπάρχει ενεργή αποστολή για ακύρωση.");

            var config = GetProviderConfigByName(xSupport, providerName);
            ICourierProvider provider = CourierProviderFactory.Create(config);

            CancelResult result = await provider.CancelShipmentAsync(shipmentNumber, jobId);

            if (result.Success && findocId.HasValue && findocId.Value > 0)
            {
                xSupport.ExecuteSQL(
                    "UPDATE FINDOC SET VARCHAR01=NULL, VARCHAR02=NULL, CCCCOURJOBID=NULL WHERE FINDOC=:1",
                    findocId.Value);
            }

            return new JObject
            {
                ["success"] = result.Success,
                ["errorMessage"] = result.ErrorMessage
            };
        }

        private static TimeSpan? TryParseTime(string s) =>
            !string.IsNullOrWhiteSpace(s) &&
            TimeSpan.TryParseExact(s, @"hh\:mm", System.Globalization.CultureInfo.InvariantCulture, out var t)
                ? (TimeSpan?)t : null;

        // ── Deterministic: λήψη PDF voucher (port btnPrintVoucher_Click) ─
        public static async Task<JObject> GetVoucherPdfAsync(XSupport xSupport, string providerCode, string shipmentNumber)
        {
            if (string.IsNullOrWhiteSpace(providerCode)) throw new Exception("Λείπει το providerCode.");
            if (string.IsNullOrWhiteSpace(shipmentNumber)) throw new Exception("Λείπει το shipmentNumber.");

            var config = GetProviderConfigByCode(xSupport, providerCode);
            ICourierProvider provider = CourierProviderFactory.Create(config);
            byte[] pdfBytes = await provider.GetVoucherAsync(shipmentNumber);

            if (pdfBytes == null || pdfBytes.Length == 0)
                throw new Exception("Αδυναμία λήψης voucher.");

            return new JObject
            {
                ["success"] = true,
                ["pdfBase64"] = Convert.ToBase64String(pdfBytes)
            };
        }
    }
}
