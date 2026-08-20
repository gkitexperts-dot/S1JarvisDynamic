using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Softone;

namespace S1Jarvis.Core
{
    // ══════════════════════════════════════════════════════════════════════
    // JarvisItems - ΝΕΟ 18/08, ρητό αίτημα χρήστη ("άνοιγμα ειδών ... θέλει
    // ανάλυση"). Δημιουργία νέου MTRL (είδος) μέσω chat - ΔΥΟ tiers ρητά
    // περιγραμμένα από τον χρήστη:
    //   (α) Απλό: ΜΟΝΟ τα απαραίτητα πεδία (βλ. RequiredFromOperator πιο
    //       κάτω) - όλα τα υπόλοιπα αντιγράφονται από ΠΑΡΟΜΟΙΟ υπάρχον
    //       είδος (πρότυπο) στο μητρώο.
    //   (β) Πολύπλοκο: όσο το δυνατόν περισσότερα πεδία - ΕΚΤΟΣ σκοπείου
    //       ΑΥΤΗΣ της πρώτης υλοποίησης (θα χτιστεί σε επόμενο βήμα, βλ.
    //       README - χρειάζεται web-search integration + δυναμικό mapping
    //       "πινακοποιημένων" πεδίων σε υποπίνακες, ρητά μεγαλύτερο σκοπός).
    //
    // ΡΗΤΑ ΑΠΟΦΑΣΙΣΜΕΝΑ ΑΠΟ ΤΟΝ ΧΡΗΣΤΗ 18/08:
    //  - "Μερίδα είδους" = απλά μία νέα εγγραφή MTRL, ΚΑΝΕΝΑ ξεχωριστό
    //    concept/πίνακας.
    //  - ΑΓΝΟΟΥΜΕ τα ccc* custom πεδία (Jetoil-specific) - μένουμε σε
    //    στάνταρ Soft1 MTRL πεδία, ΓΕΝΙΚΟ σχεδιασμό (ρητή υπενθύμιση
    //    χρήστη "μην στέκεσαι στην Jetoil μόνο").
    //  - Designer Object: "ITEM", SODTYPE=51 - ΤΟ ΙΔΙΟ idiom με το TRDR
    //    (βλ. JarvisTools.TraderObjectsBySodType/ExecuteCreateTraderFromAade) -
    //    το SODTYPE ΔΕΝ μπαίνει χειροκίνητα σε Current[...], είναι
    //    inherent στο ίδιο το CreateModule("ITEM").
    //  - CODE: προτείνεται μόνος του (ΙΔΙΑ λογική με
    //    JarvisTools.SuggestNextTraderCode), επεξεργάσιμο - ΟΧΙ σιωπηλή
    //    αυτόματη ανάθεση.
    //  - Πάντα ρωτάει ο χειριστής: CODE(προτεινόμενο)/NAME/MTRUNIT1(ΜΜ)/
    //    VAT(ΦΠΑ)/MTRACN(Λογαριασμός). MTRUNIT3(ΜΜ αγορών)/MTRUNIT4(ΜΜ
    //    πώλησης) = default ΙΔΙΟ με MTRUNIT1, αλλάζουν ΜΟΝΟ αν ζητηθεί
    //    ρητά. Προαιρετικά PRICER(τιμή λιανικής)/PRICEW(τιμή χονδρικής).
    //  - ΠΑΝΤΑ ρωτάει (εκτός αν ήδη απαντήθηκε στο αρχικό prompt):
    //    MTRLOTUSE (Παρτίδα, checkbox) / MTRSNUSE (SN, 0/1).
    //  - Όλα τα ΥΠΟΛΟΙΠΑ πεδία (Ομάδα/Κατηγορία/Οίκος/Τύπος/κλπ) - ΑΝ
    //    υπάρχει πρότυπο είδος, αντιγράφονται ΑΠΟ ΕΚΕΙ (whitelist, βλ.
    //    ParamCode 500026/ItemCopyFieldsWhitelist πιο κάτω - ΙΔΙΟ idiom
    //    με το CarryOverFieldsByPhysicalTable του DR feature: comma-
    //    delimited λίστα στηλών, επέκταση = 1 αλλαγή στην παράμετρο, ΟΧΙ
    //    στον κώδικα).
    //  - Πρότυπο είδος: αν το δώσει ο χειριστής, ρωτάει ΜΟΝΟ τη νέα
    //    περιγραφή. Αν δεν το δώσει, ρωτάει ΑΝ υπάρχει πρότυπο. Αν ΟΥΤΕ
    //    τότε δοθεί, ΜΙΑ ενιαία ερώτηση με όλα τα απαραίτητα πεδία μαζί
    //    (βλ. BuildSystemPrompt στο JarvisAgentClient.cs).
    //
    // ΔΥΟ tools:
    //   get_item_template  - ΜΟΝΟ ανάγνωση (whitelist πεδία πρότυπου +
    //                         προτεινόμενος επόμενος κωδικός), ΔΕΝ
    //                         χρειάζεται επιβεβαίωση.
    //   create_item        - write, ΥΠΟΧΡΕΩΤΙΚΗ επιβεβαίωση πριν την
    //                         κλήση (μόνιμη εγγραφή MTRL - ίδιος κανόνας
    //                         με create_trader_from_aade/send_email).
    // ══════════════════════════════════════════════════════════════════════
    internal static class JarvisItems
    {
        // ParamCode 500026 - "Jarvis - Item Copy Fields" (βλ. PARAMS.md).
        // Comma-delimited MTRL στήλες που επιτρέπεται να αντιγραφούν από
        // πρότυπο είδος - ΚΑΙ στο get_item_template (τι διαβάζουμε) ΚΑΙ
        // στο create_item (server-side whitelist validation, ο Jarvis ΔΕΝ
        // μπορεί να γράψει σε αυθαίρετη στήλη). Default αν λείπει η
        // παράμετρος - λογική επιλογή στηλών, ΧΩΡΙΣ ccc* (ρητή απόφαση
        // χρήστη).
        private const string DefaultItemCopyFields =
            "MTRGROUP,MTRCATEGORY,MTRMANFCTR,MTRTYPE,MTRTYPE1,BUSUNITS," +
            "COSTCNTR,COUNTRY,MCOUNTRY,MTRDUTY,MTRSEASON,SOCURRENCY," +
            "MTRPCATEGORY,MTRMARK,MTRMODEL";

        // Ίδιο idiom με JarvisTools.GetCrmTaskOptionalParam - εδώ string
        // (ParamValueString) αντί για int (ParamValue), ίδιο query σχήμα
        // με το JarvisEmailAccess.GetRequiredParamString αλλά ΧΩΡΙΣ throw
        // (optional, με default).
        private static string GetItemCopyFieldsWhitelistRaw(XSupport xSupport)
        {
            try
            {
                XTable t = xSupport.GetSQLDataSet(
                    "SELECT TOP 1 ParamValueString FROM cccParams WHERE ParamCode=500026 " +
                    "AND (paramsIsActive=1 OR paramsIsActive IS NULL) ORDER BY cccParams DESC");
                if (t == null || t.Count == 0 || t.Current["ParamValueString"] == DBNull.Value)
                    return DefaultItemCopyFields;
                string val = t.Current["ParamValueString"].ToString();
                return string.IsNullOrWhiteSpace(val) ? DefaultItemCopyFields : val;
            }
            catch (Exception ex)
            {
                DebugLog.Log("[items] GetItemCopyFieldsWhitelistRaw EXCEPTION, fallback: " + ex);
                return DefaultItemCopyFields;
            }
        }

        private static HashSet<string> GetItemCopyFieldsWhitelist(XSupport xSupport) =>
            new HashSet<string>(
                GetItemCopyFieldsWhitelistRaw(xSupport).Split(',').Select(s => s.Trim().ToUpperInvariant())
                    .Where(s => s.Length > 0));

        // Ίδια λογική με JarvisTools.SuggestNextTraderCode (MAX-based,
        // ΔΕΝ κοιτάει SODTYPE - όλα τα είδη είναι SODTYPE=51/"ITEM", ΟΧΙ
        // μοιρασμένα σε πολλά SODTYPE όπως ο TRDR). ΑΠΛΟ v1 (TRY_PARSE
        // αριθμητικό MAX+1) - αν το CODE δεν είναι αριθμητικό pattern σε
        // αυτή την εγκατάσταση, ο χειριστής το αλλάζει ελεύθερα (ρητά
        // "επεξεργάσιμο πεδίο", ΔΕΝ χρειάζεται τέλεια πρόβλεψη).
        private static string SuggestNextMtrlCode(XSupport xSupport)
        {
            try
            {
                int company = xSupport.ConnectionInfo.CompanyId;
                XTable t = xSupport.GetSQLDataSet(
                    "SELECT TOP 50 CODE FROM MTRL WHERE COMPANY=:1 AND ISNUMERIC(CODE)=1 " +
                    "ORDER BY TRY_CONVERT(bigint, CODE) DESC", company);
                if (t == null || t.Count == 0) return "1";

                string topCode = t.Current["CODE"]?.ToString();
                if (string.IsNullOrWhiteSpace(topCode) || !long.TryParse(topCode, out long topVal))
                    return "1";

                int width = topCode.Length; // ίδιο zero-padding heuristic με SuggestNextTraderCode
                string next = (topVal + 1).ToString();
                return next.Length < width ? next.PadLeft(width, '0') : next;
            }
            catch (Exception ex)
            {
                DebugLog.Log("[items] SuggestNextMtrlCode EXCEPTION, fallback '1': " + ex);
                return "1";
            }
        }

        // ── get_item_template (LLM tool) - ΜΟΝΟ ανάγνωση. Βρίσκει το
        // whitelist-πεδία ΕΝΟΣ ήδη-εντοπισμένου (μέσω query_data) πρότυπου
        // είδους + προτείνει τον επόμενο κωδικό. Ο Claude καλεί ΠΡΩΤΑ
        // query_data για να βρει/επιβεβαιώσει το MTRL id του πρότυπου
        // (π.χ. WHERE NAME LIKE '%...%') - αυτό εδώ ΔΕΝ ψάχνει τίποτα
        // μόνο του, μόνο διαβάζει τα ΣΥΓΚΕΚΡΙΜΕΝΑ whitelist πεδία. ──────
        public static readonly object GetItemTemplateToolDefinition = new
        {
            name = "get_item_template",
            description =
                "Διαβάζει τα βασικά (whitelist) πεδία ΕΝΟΣ πρότυπου είδους " +
                "(που ΗΔΗ εντόπισες με query_data - π.χ. WHERE NAME LIKE " +
                "'%...%') για να τα χρησιμοποιήσεις σαν βάση για ΝΕΟ είδος " +
                "(create_item). ΠΑΝΤΑ επιστρέφει ΚΑΙ έναν προτεινόμενο " +
                "επόμενο κωδικό (suggestedCode) - δείξ' τον στον χειριστή " +
                "ως ΕΠΕΞΕΡΓΑΣΙΜΗ πρόταση, ΜΗΝ τον χρησιμοποιήσεις σιωπηλά. " +
                "Αν ο χειριστής ΔΕΝ έχει πρότυπο, μπορείς να το καλέσεις " +
                "ΧΩΡΙΣ templateMtrl - θα πάρεις πίσω ΜΟΝΟ το suggestedCode.",
            input_schema = new
            {
                type = "object",
                properties = new
                {
                    templateMtrl = new { type = "integer", description = "MTRL.MTRL (id) του πρότυπου είδους - προαιρετικό." }
                },
                required = new string[0]
            }
        };

        public static string ExecuteGetItemTemplate(XSupport xSupport, JObject input)
        {
            string suggestedCode = SuggestNextMtrlCode(xSupport);
            int? templateMtrl = input?["templateMtrl"]?.ToObject<int?>();
            if (!templateMtrl.HasValue)
                return JsonConvert.SerializeObject(new { success = true, suggestedCode, copiedFields = new JObject() });

            HashSet<string> whitelist = GetItemCopyFieldsWhitelist(xSupport);
            if (whitelist.Count == 0)
                return JsonConvert.SerializeObject(new { success = true, suggestedCode, copiedFields = new JObject() });

            string columns = string.Join(",", whitelist);
            XTable t = xSupport.GetSQLDataSet(
                $"SELECT {columns} FROM MTRL WHERE MTRL=:1", templateMtrl.Value);
            if (t == null || t.Count == 0)
                return JsonConvert.SerializeObject(new
                { success = false, error = $"Δεν βρέθηκε είδος με MTRL={templateMtrl.Value} για πρότυπο." });

            // Κάθε στήλη στο whitelist είναι ΗΔΗ μέρος του SELECT πιο πάνω -
            // αν η ερώτηση πέτυχε, υπάρχει σίγουρα στο αποτέλεσμα (ΔΕΝ
            // χρειάζεται pre-check .Columns.Contains). try/catch ανά στήλη
            // ίδιο idiom με JarvisCourier.ColSafe - άμυνα σε τυχόν
            // απροσδόκητο τύπο/null χωρίς να σκάσει όλη η ανάγνωση.
            var copiedFields = new JObject();
            foreach (string col in whitelist)
            {
                try
                {
                    object val = t.Current[col];
                    if (val != null && val != DBNull.Value) copiedFields[col] = JToken.FromObject(val);
                }
                catch (Exception ex)
                {
                    DebugLog.Log($"[items] ExecuteGetItemTemplate: παράλειψη στήλης {col}: " + ex.Message);
                }
            }
            return JsonConvert.SerializeObject(new { success = true, suggestedCode, copiedFields });
        }

        // ── create_item (LLM tool) - write. ΥΠΟΧΡΕΩΤΙΚΗ επιβεβαίωση πριν
        // την κλήση (βλ. BuildSystemPrompt στο JarvisAgentClient.cs) -
        // μόνιμη εγγραφή MTRL, ίδιος κανόνας με create_trader_from_aade/
        // send_email. ΙΔΙΟ write idiom με ExecuteCreateTraderFromAade
        // (CreateModule -> GetTable -> InsertData() -> set Current[...] ->
        // PostData()) - "ITEM" object, SODTYPE=51 inherent (ΔΕΝ μπαίνει
        // χειροκίνητα). ──────────────────────────────────────────────────
        public static readonly object CreateItemToolDefinition = new
        {
            name = "create_item",
            description =
                "Δημιουργεί ΝΕΟ είδος (MTRL) - ΑΝΕΠΙΣΤΡΕΠΤΗ ενέργεια " +
                "(μόνιμη εγγραφή). Χρησιμοποίησέ το ΜΟΝΟ αφού (1) μάζεψες " +
                "όλα τα απαραίτητα πεδία (code/name/mtrunit1/vat/mtracn/ " +
                "mtrlotuse/mtrsnuse - ΠΑΝΤΑ, βλ. system prompt για ΠΩΣ), " +
                "(2) πήρες τα copiedFields από το get_item_template αν " +
                "υπάρχει πρότυπο, (3) έδειξες ΠΛΗΡΕΣ draft (ΟΛΑ τα πεδία, " +
                "ανθρώπινα διατυπωμένα) στον χειριστή ΚΑΙ (4) πήρες ρητή " +
                "επιβεβαίωση σε ΕΠΟΜΕΝΟ μήνυμα. ΠΟΤΕ στο ίδιο turn που " +
                "έδειξες το draft.",
            input_schema = new
            {
                type = "object",
                properties = new
                {
                    code = new { type = "string", description = "Κωδικός είδους (MTRL.CODE) - από το suggestedCode ή ό,τι διάλεξε/άλλαξε ο χειριστής." },
                    name = new { type = "string", description = "Περιγραφή είδους (MTRL.NAME)." },
                    mtrunit1 = new { type = "integer", description = "Βασική Μονάδα Μέτρησης (MTRL.MTRUNIT1, FK στο MTRUNIT)." },
                    vat = new { type = "integer", description = "ΦΠΑ (MTRL.VAT, FK)." },
                    mtracn = new { type = "integer", description = "Λογαριασμός (MTRL.MTRACN)." },
                    mtrunit3 = new { type = "integer", description = "ΜΜ αγορών (MTRL.MTRUNIT3) - προαιρετικό, default = mtrunit1." },
                    mtrunit4 = new { type = "integer", description = "ΜΜ πώλησης (MTRL.MTRUNIT4) - προαιρετικό, default = mtrunit1." },
                    mtrlotuse = new { type = "boolean", description = "Παρτίδα (MTRL.MTRLOTUSE) - ΠΑΝΤΑ ρωτημένο ρητά πριν εδώ." },
                    mtrsnuse = new { type = "boolean", description = "Serial Number (MTRL.MTRSNUSE) - ΠΑΝΤΑ ρωτημένο ρητά πριν εδώ." },
                    pricer = new { type = "number", description = "Τιμή λιανικής (MTRL.PRICER) - προαιρετικό." },
                    pricew = new { type = "number", description = "Τιμή χονδρικής (MTRL.PRICEW) - προαιρετικό." },
                    copiedFields = new
                    {
                        type = "object",
                        description = "Πεδία-τιμές από το get_item_template (copiedFields) - ΑΚΡΙΒΩΣ ό,τι επέστρεψε, ΧΩΡΙΣ αλλαγές στα ονόματα στηλών."
                    }
                },
                required = new[] { "code", "name", "mtrunit1", "vat", "mtracn", "mtrlotuse", "mtrsnuse" }
            }
        };

        public static string ExecuteCreateItem(XSupport xSupport, JObject input)
        {
            string code = input?["code"]?.ToString();
            string name = input?["name"]?.ToString();
            if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(name))
                throw new Exception("Λείπει κωδικός ή περιγραφή του νέου είδους.");
            if (!input.TryGetValue("mtrunit1", out JToken mtrunit1Tok) || mtrunit1Tok.Type == JTokenType.Null)
                throw new Exception("Λείπει η βασική Μονάδα Μέτρησης.");
            if (!input.TryGetValue("vat", out JToken vatTok) || vatTok.Type == JTokenType.Null)
                throw new Exception("Λείπει το ΦΠΑ.");
            if (!input.TryGetValue("mtracn", out JToken mtracnTok) || mtracnTok.Type == JTokenType.Null)
                throw new Exception("Λείπει ο Λογαριασμός.");
            if (!input.TryGetValue("mtrlotuse", out JToken lotTok) || lotTok.Type == JTokenType.Null)
                throw new Exception("Λείπει η απάντηση για Παρτίδα (ναι/όχι).");
            if (!input.TryGetValue("mtrsnuse", out JToken snTok) || snTok.Type == JTokenType.Null)
                throw new Exception("Λείπει η απάντηση για Serial Number (ναι/όχι).");

            int mtrunit1 = mtrunit1Tok.ToObject<int>();
            int vat = vatTok.ToObject<int>();
            int mtracn = mtracnTok.ToObject<int>();
            bool mtrlotuse = lotTok.ToObject<bool>();
            bool mtrsnuse = snTok.ToObject<bool>();
            int mtrunit3 = input?["mtrunit3"]?.ToObject<int?>() ?? mtrunit1;
            int mtrunit4 = input?["mtrunit4"]?.ToObject<int?>() ?? mtrunit1;
            double? pricer = input?["pricer"]?.ToObject<double?>();
            double? pricew = input?["pricew"]?.ToObject<double?>();

            int company = xSupport.ConnectionInfo.CompanyId;
            XTable dup = xSupport.GetSQLDataSet(
                "SELECT COUNT(*) AS CNT FROM MTRL WHERE COMPANY=:1 AND CODE=:2", company, code);
            if (dup != null && dup.Count > 0 && Convert.ToInt32(dup.Current["CNT"]) > 0)
                throw new Exception($"Ο κωδικός \"{code}\" υπάρχει ήδη σε άλλο είδος - διάλεξε άλλον.");

            // copiedFields - server-side whitelist validation (ΙΔΙΟ
            // whitelist με το get_item_template) - ο Jarvis ΔΕΝ μπορεί να
            // γράψει σε στήλη εκτός λίστας, ΡΗΤΟ fail αν προσπαθήσει
            // (ΔΕΝ αγνοούμε σιωπηλά - "fail clearly", ίδια φιλοσοφία με
            // το υπόλοιπο project).
            HashSet<string> whitelist = GetItemCopyFieldsWhitelist(xSupport);
            var copiedFields = input?["copiedFields"] as JObject ?? new JObject();
            var invalid = copiedFields.Properties()
                .Select(p => p.Name.ToUpperInvariant())
                .Where(n => !whitelist.Contains(n))
                .ToList();
            if (invalid.Count > 0)
                throw new Exception($"Μη επιτρεπτά πεδία στο copiedFields: {string.Join(", ", invalid)}.");

            XModule m = xSupport.CreateModule("ITEM");
            XTable MTRL = m.GetTable("MTRL");
            try
            {
                m.InsertData();
                MTRL.Current["CODE"] = code;
                MTRL.Current["NAME"] = name;
                MTRL.Current["MTRUNIT1"] = mtrunit1;
                MTRL.Current["VAT"] = vat;
                MTRL.Current["MTRACN"] = mtracn;
                MTRL.Current["MTRUNIT3"] = mtrunit3;
                MTRL.Current["MTRUNIT4"] = mtrunit4;
                MTRL.Current["MTRLOTUSE"] = mtrlotuse ? 1 : 0;
                MTRL.Current["MTRSNUSE"] = mtrsnuse ? 1 : 0;
                // ΔΙΟΡΘΩΘΗΚΕ 18/08 (ζωντανό bug χρήστη - "Specified cast is
                // not valid", μόνο σε bulk import μέσω Browser scrape) -
                // MTRL.PRICER/PRICEW είναι SQL `float` - το Softone XTable
                // indexer κάνει εσωτερικά ΑΥΣΤΗΡΟ unboxing cast (πιθανόν
                // `(float)value`) - ένα boxed `double` (ό,τι δίνει το
                // `.ToObject<double?>()` στο input parsing πιο πάνω) ΔΕΝ
                // unboxάρει σε `float` (C# δεν επιτρέπει unboxing σε
                // διαφορετικό τύπο από αυτόν που "μπήκε" στο box, ΑΚΟΜΑ κι
                // αν η τιμή θα χωρούσε) - `Convert.ToSingle` κάνει
                // ΠΡΑΓΜΑΤΙΚΗ μετατροπή τιμής, όχι unboxing, οπότε δουλεύει.
                if (pricer.HasValue) MTRL.Current["PRICER"] = Convert.ToSingle(pricer.Value);
                if (pricew.HasValue) MTRL.Current["PRICEW"] = Convert.ToSingle(pricew.Value);

                // ΙΔΙΟ πρόβλημα, copiedFields (MTRGROUP/MTRTYPE/MTRTYPE1/
                // SOCURRENCY κλπ - ΟΛΑ ακέραιες FK/lookup στήλες, βλ.
                // DefaultItemCopyFields) - το `JToken.ToObject<object>()`
                // δίνει ΠΑΝΤΑ `long` για οποιονδήποτε ακέραιο (Json.NET
                // default), ΑΣΧΕΤΑ αν η αρχική SQL στήλη ήταν smallint/int -
                // το XTable indexer θέλει ΑΚΡΙΒΩΣ `int` (επιβεβαιωμένο ζωντανά
                // - τα ΗΔΗ σωστά typed πεδία πιο πάνω, π.χ. MTRUNIT1/VAT,
                // δούλεψαν κανονικά). `Convert.ToInt32` αντί για blind
                // object pass-through.
                foreach (var prop in copiedFields.Properties())
                {
                    if (prop.Value.Type == JTokenType.Null) continue;
                    object val = prop.Value.Type == JTokenType.Integer
                        ? (object)Convert.ToInt32(prop.Value.ToObject<long>())
                        : prop.Value.ToObject<object>();
                    MTRL.Current[prop.Name.ToUpperInvariant()] = val;
                }

                int mtrlId = m.PostData();
                if (mtrlId <= 0)
                    throw new Exception("Αποτυχία δημιουργίας είδους (PostData επέστρεψε 0).");

                // ΔΙΟΡΘΩΘΗΚΕ 18/08 (ζωντανό bug report χρήστη - "ο κωδικός
                // που έδειξε στο αποτέλεσμα δεν ήταν αυτός που έβαλε μέσα
                // στο είδος ... τυχαίο 7ψήφιο νούμερο"): το `ITEM` object
                // πιθανόν έχει auto-numbering ενεργό στο Designer - το
                // Soft1 ΑΓΝΟΕΙ/ΑΝΤΙΚΑΘΙΣΤΑ το CODE που στείλαμε κατά το
                // PostData(). ΠΟΤΕ μην εμπιστεύεσαι το `code` που ΖΗΤΗΣΑΜΕ -
                // διάβασε ΠΙΣΩ την ΠΡΑΓΜΑΤΙΚΗ τιμή από τη βάση μετά το
                // insert, ΑΥΤΗ επιστρέφεται στον Jarvis/χειριστή (ίδια
                // φιλοσοφία με ΟΛΟ το project - "μην μαντεύεις, επαλήθευσε
                // πάνω στο Soft1"). DebugLog ΑΝ διαφέρει, χρήσιμο για
                // διάγνωση αν ξαναχρειαστεί.
                string actualCode = code;
                try
                {
                    XTable check = xSupport.GetSQLDataSet("SELECT CODE FROM MTRL WHERE MTRL=:1", mtrlId);
                    if (check != null && check.Count > 0 && check.Current["CODE"] != DBNull.Value)
                        actualCode = check.Current["CODE"].ToString();
                }
                catch (Exception ex)
                {
                    DebugLog.Log($"[items] ExecuteCreateItem: αποτυχία επαλήθευσης CODE μετά το insert (mtrlId={mtrlId}): " + ex.Message);
                }
                if (actualCode != code)
                    DebugLog.Log($"[items] ExecuteCreateItem: ΤΟ CODE ΑΛΛΑΞΕ από το Soft1 - ζητήθηκε '{code}', αποθηκεύτηκε '{actualCode}' (πιθανό auto-numbering στο ITEM object).");

                DebugLog.Log($"[items] ExecuteCreateItem OK -> mtrlId={mtrlId} requestedCode={code} actualCode={actualCode} name={name}");
                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    mtrlId,
                    code = actualCode,
                    requestedCode = code,
                    codeChanged = actualCode != code,
                    name
                });
            }
            finally { MTRL.Dispose(); m.Dispose(); }
        }

        // ΝΕΟ 19/08, ζωντανό bug report χρήστη ("δεν μου έδωσε το link να
        // ανοίξω το είδος" - το create_item ΕΠΙΤΥΧΕ αλλά δεν υπήρχε τρόπος
        // να ανοίξει η κάρτα του). ΙΔΙΟ AUTOLOCATE idiom με το
        // JarvisTools.ExecuteOpenTrader (open_document/open_trader), ΑΠΛΑ
        // ΑΠΛΟΥΣΤΕΡΟ - το ITEM Designer object (ΙΔΙΟ που χρησιμοποιεί το
        // ExecuteCreateItem πιο πάνω) είναι ΠΑΝΤΑ το ίδιο, καμία ανάγκη για
        // objectName param (σε αντίθεση με trader που έχει SUPPLIER/
        // CUSTOMER ανάλογα με SODTYPE).
        public static string ExecuteOpenItem(XSupport xSupport, int mtrlId)
        {
            if (mtrlId <= 0)
                throw new Exception("Άγνωστο mtrlId για άνοιγμα είδους.");

            string command = $"ITEM[AUTOLOCATE={mtrlId}]";
            xSupport.ExecS1Command(command, null);
            DebugLog.Log($"[open_item] mtrlId={mtrlId} command={command}");

            return JsonConvert.SerializeObject(new { success = true, mtrlId, command });
        }
    }
}
