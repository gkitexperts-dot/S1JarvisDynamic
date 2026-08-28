# S1Jarvis — Παράμετροι (`cccParams`)

Ενοποιημένη λίστα όλων των παραμέτρων `cccParams` που διαβάζει ο S1Jarvis,
μαζεμένες σε ένα σημείο (μέχρι τώρα ήταν σκορπισμένες μέσα στο README).
Ενημέρωσε ΚΑΙ αυτό το αρχείο κάθε φορά που προστίθεται νέο `ParamCode`.

Σύμβαση ονομάτων/στηλών του πίνακα: `ParamCode` (μοναδικό, `int`),
`Στήλη τιμής` (`ParamValue` numeric ή `ParamValueString` nvarchar - ΠΟΤΕ
και τα δύο για το ίδιο param), `Default αν λείπει`, `Απαιτούμενο;`
(αν ΝΑΙ, ο Jarvis πετάει σφάλμα χωρίς αυτό — δεν έχει ασφαλές fallback).

**Εξαίρεση 20/08** (500040-500059, Dashboard Panels): ΕΠΙΤΗΔΕΣ και οι
ΔΥΟ στήλες μαζί - `ParamValue` = τύπος γραφήματος, `ParamValueString` =
SQL query. Ρητή σχεδιαστική απόφαση χρήστη ("κάθε Param θα έχει δύο
τιμές"), βλ. σημείωση στην αντίστοιχη γραμμή.

## Πίνακας

| ParamCode | Όνομα | Στήλη | Default | Απαιτούμενο; | Πού χρησιμοποιείται |
|---|---|---|---|---|---|
| 500000 | (debug flag) | ParamValue | off | Όχι | `DebugLog.cs` - ενεργοποιεί file logging. Κοινός διακόπτης με S1Courier/S1DocReader. |
| 500002 | Courier - Δυναμικό Mapping Παραλήπτη | ParamValueString | (hardcoded fallback) | Όχι | `JarvisCourier.cs` (`BuildRequestFromFindoc`) - SQL query template που επιστρέφει ReceiverName/Address/... για ΕΝΑ FINDOC. Λείπει → πέφτει σε hardcoded λογική (FINDOC/MTRDOC/TRDR). |
| 500008 | Jarvis - Knowledge Base Series | ParamValue | — | **Ναι** | `JarvisTools.cs` (`GetQaLogSeries`) - `SERIES` για τα `SOACTION` του Q&A log / `create_crm_task`. ✅ Επιβεβαιωμένο ζωντανά (`ParamValue=30000`). |
| 500009 | Jarvis - Πλήθος Δεκαδικών σε Reports AI | ParamValue | 2 | Όχι | `JarvisTools.cs`/system prompt - πλήθος δεκαδικών σε ΚΑΘΕ αριθμητική τιμή σε reports/πίνακες/κάρτες. |
| 500011 | Jarvis - Μέγιστες Γραμμές σε Απευθείας Εξαγωγή Αρχείου | ParamValue | 5000 | Όχι | `JarvisTools.cs` (`GetDirectExportMaxRows`) - `export_query_to_file` tool. `0` = χωρίς όριο. ΞΕΧΩΡΙΣΤΟ από το 200-row cap του `query_data` (εκεί ΔΕΝ περνάνε δεδομένα από το context του Claude). |
| 500012 | Jarvis - Σειρά CRM Tasks | ParamValue | — | **Ναι** | `JarvisTools.cs` (`create_crm_task`) - `SERIES` για νέες `SOACTION` εργασίες. |
| 500013 | Jarvis - ActStates (CRM task default) | ParamValue | 1001 | Όχι | `JarvisTools.cs` (`GetCrmTaskOptionalParam`) - default κατάσταση νέας εργασίας. |
| 500014 | Jarvis - ActStatus (CRM task default) | ParamValue | 1 | Όχι | Ίδιο με 500013, `ACTSTATUS`. |
| 500015 | Jarvis - Dashboard Tasks Auto-refresh (λεπτά) | ParamValue | 5 | Όχι | `JarvisTools.cs` (`ExecuteGetMyAssignedTasks`) - πόσο συχνά ξανακατεβάζει tasks το Dashboard "Tasks" tab. |
| 500016 | Jarvis - Order Entry Confidence Threshold | ParamValue | 85 | Όχι | `JarvisTools.cs` (`GetOrderEntryConfidenceThreshold`) - ΠΟΣΟΣΤΟ (π.χ. `85`, ΟΧΙ `0.85`) - πόσο σίγουρος πρέπει να είναι ο Claude πριν προχωρήσει σε αυτόματη καταχώρηση παραγγελίας από email. Self-reported confidence (ΟΧΙ στατιστικό όπως στο DR). |
| 500017 | Jarvis - Σειρά Prompt Log Παραγγελιών | ParamValue | — | **Ναι** | `JarvisTools.cs` (`create_order`) - `SERIES` για το log καταχώρησης παραγγελιών AI. ✅ Επιβεβαιωμένο ζωντανά 17/08 (`ParamValue=30002`). |
| 500018 | Jarvis - Παραμετρική Προβολή ανά Κύκλωμα | ParamValueString | (καμία - default συμπεριφορά) | Όχι | `JarvisTools.cs` (`GetConfiguredFormName`) - λίστα `"sosource=FormName;..."` (π.χ. `"1351=Salesform Jetoil;1412=Εμβάσματα Προμηθευτών Jetoil;..."`). Χρησιμοποιείται ΚΑΙ στο `create_order` ΚΑΙ στο `open_document` (`ExecuteOpenDocument`) - ποια native φόρμα Soft1 ανοίγει ανά SOSOURCE. |
| 500019 | Email OAuth - Client ID | ParamValueString | — | **Ναι** (για Email tab) | `JarvisEmailAccess.cs` - Azure AD App Registration Application/Client ID. |
| 500020 | Email OAuth - Tenant ID | ParamValueString | — | **Ναι** (για Email tab) | `JarvisEmailAccess.cs` - Azure AD Directory/Tenant ID. |
| 500021 | Email OAuth - Client Secret | ParamValueString | — | **Ναι** (για Email tab) | `JarvisEmailAccess.cs` - ⚠️ ουσιαστικά password, ΟΧΙ το "Secret ID" του Azure - η ΤΙΜΗ του secret. Χειρίσου το σαν ευαίσθητο στοιχείο. |
| 500022 | Jarvis - Email Inbox Max Emails | ParamValue | 100 | Όχι | `JarvisEmailAccess.cs` (`GetInboxEmailsAsync`) - `$top` στο Graph API query. |
| 500023 | Jarvis - Calendar Outlook Max Events | ParamValue | 100 | Όχι | `JarvisEmailAccess.cs` (`GetCalendarEventsAsync`) - `$top` στο Graph API `calendarView`. |
| 500024 | Jarvis - Dashboard Tasks Max Rows | ParamValue | 100 | Όχι | `JarvisTools.cs` (`ExecuteGetMyAssignedTasks`) - `TOP N` στο SQL. |
| 500025 | Jarvis - Browser read_page_content Max Characters | ParamValue | 40000 | Όχι | `JarvisTools.cs` (`ExecuteReadPageContent`) - πόσους χαρακτήρες ορατού κειμένου σελίδας περνάει στο context του Claude πριν κοπεί (`truncated:true`). ΝΕΟ 18/08 - πριν hardcoded `8000`, δεν ήταν παράμετρος. |
| 500026 | Jarvis - Item Copy Fields | ParamValueString | (hardcoded λίστα, βλ. `JarvisItems.DefaultItemCopyFields`) | Όχι | `JarvisItems.cs` (`GetItemCopyFieldsWhitelistRaw`) - comma-delimited στήλες `MTRL` που επιτρέπεται να αντιγραφούν από πρότυπο είδος στο `create_item`/`get_item_template` (ΙΔΙΟ idiom με το `CarryOverFieldsByPhysicalTable` του DR feature) - ΚΑΙ whitelist για server-side validation, όχι μόνο "τι διαβάζουμε". ΝΕΟ 18/08. |
| 500027 | Jarvis - Πρόσθετες Οδηγίες Διαχειριστή | ParamValueString | (καμία - παραλείπεται αν λείπει) | Όχι | `JarvisTools.cs` (`GetOptionalParamString`) → `JarvisAgentClient.cs` (`BuildSystemPrompt`, τέλος prompt, ΣΕ ΚΑΘΕ mode) - ελεύθερο κείμενο "εκπαίδευσης"/business context, ΧΩΡΙΣ redeploy ("κάτι σαν skill", ρητό αίτημα χρήστη 18/08). ⚠️ Ρητά πλαισιωμένο ως ΣΥΜΠΛΗΡΩΜΑΤΙΚΟ - ΔΕΝ ακυρώνει κανόνες ασφαλείας/επιβεβαίωσης (mitigation έναντι prompt injection μέσω της παραμέτρου, συζητήθηκε ρητά). Χειρίσου την πρόσβαση σε αυτή την παράμετρο σαν ευαίσθητη (ελέγχει πραγματικά τη συμπεριφορά του Jarvis). |
| 500028 | Jarvis - Bulk Import Max Iterations | ParamValue | 40 | Όχι | `JarvisAgentClient.cs` (`AskAsync`, νέο προαιρετικό `maxIterations` όρισμα) - override του σταθερού `MaxIterations=14` ΜΟΝΟ στο γενικό chat και στο Browser mode (`JarvisShell.xaml.cs`, δύο call sites) - ΝΕΟ 18/08, ρητό αίτημα χρήστη ("bulk import ειδών από αρχείο/σελίδα" χρειάζεται πολλά tool calls στη σειρά, π.χ. `get_item_template`/`create_item` ανά γραμμή τιμοκαταλόγου). ΧΩΡΙΣ κόστος για κανονικές συζητήσεις - το όριο είναι ΜΟΝΟ οροφή ασφαλείας, δεν "καταναλώνεται" ποτέ σε κανονική χρήση. |
| 500029 | Jarvis - Μοντέλο AI: Atlas (γενικό chat) | ParamValueString | `claude-opus-5` (= το σταθερό `Model` const) | Όχι | `JarvisAgentClient.cs` (`ResolveAgentModel`) - ΝΕΟ 19/08, agent-clustering restructuring. Admin-only override, ΟΧΙ στο UI χειριστή. ⚠️ per-company (ΟΧΙ per-Soft1-user) - βλ. README. |
| 500030 | Jarvis - Μοντέλο AI: Forge (item creation) | ParamValueString | `claude-opus-5` | Όχι | Ίδιο idiom με 500029, agent "Forge" (`itemMode`). |
| 500031 | Jarvis - Μοντέλο AI: Compass (trader/ΑΦΜ creation) | ParamValueString | `claude-opus-5` | Όχι | Ίδιο idiom με 500029, agent "Compass" (`traderMode`). |
| 500032 | Jarvis - Μοντέλο AI: Echo (email/επαφές/reminders) | ParamValueString | `claude-opus-5` | Όχι | Ίδιο idiom με 500029, agent "Echo" (`emailMode`). |
| 500033 | Jarvis - Μοντέλο AI: Sprint (courier vouchers) | ParamValueString | `claude-opus-5` | Όχι | Ίδιο idiom με 500029, agent "Sprint" (`courierMode`). |
| 500034 | Jarvis - Μοντέλο AI: Scout (browser/scraping) | ParamValueString | `claude-opus-5` | Όχι | Ίδιο idiom με 500029, agent "Scout" (`browserMode`). |
| 500035 | Jarvis - Μοντέλο AI: Sage (help) | ParamValueString | `claude-opus-5` | Όχι | Ίδιο idiom με 500029, agent "Sage" (`helpMode`). |
| 500040-500059 | Jarvis - Commercial Dashboard Panels (20 slots) | **ΚΑΙ ΟΙ ΔΥΟ** ParamValue+ParamValueString | (κενό = panel ανενεργό) | Όχι | `DashboardPanels.cs` (`BuildDashboardText`) - ΝΕΟ 20/08, ρητό αίτημα χρήστη: το Commercial dashboard δεν καλεί πια agent, τρέχει SQL απευθείας. `ParamValue` = τύπος γραφήματος (`1`=bar, `2`=line, `3`=pie, `4`=doughnut - οτιδήποτε άλλο/κενό πέφτει σε bar). `ParamValueString` = SQL query, ΕΝΑ placeholder `:1` (η επιλεγμένη ημερομηνία, π.χ. `WHERE F.TRNDATE = :1`). Σχήμα αποτελέσματος: στήλη 0 = labels, στήλες 1..N = ένα dataset η καθεμία (label = όνομα στήλης). Κενό `ParamValueString` = το slot παραλείπεται σιωπηλά (σταδιακή συμπλήρωση 4→20 χωρίς rebuild). Οι 4 πρώτοι κωδικοί (500040-500043) αντιστοιχούν στα ΠΑΛΙΑ AI-driven panels - **SQL ΔΕΝ έχει ακόμα γραφτεί/επιβεβαιωθεί** (χρειάζεται είτε τον χρήστη είτε ζωντανό test με τον Jarvis chat να επιβεβαιώσει τις σωστές στήλες γραμμών-ειδών πριν μπουν οι τιμές): `500040`=Top 10 πελάτες με τζίρο ημέρας, `500041`=Top 10 προϊόντα σε τεμάχια ημέρας, `500042`=Top 10 προϊόντα με τζίρο ημέρας, `500043`=Τρέχουσες τιμές ανά προϊόν (χρησιμοποιεί `MTRL.pricew`, ΟΧΙ πωλήσεις συγκεκριμένης ημέρας - το `:1` μπορεί να αγνοηθεί σε αυτό το query). `500044-500059` (16 slots) ελεύθερα για μελλοντικά panels. |
| 500060 | WelcomeStores - Stock Companies | ParamValueString | — | **Ναι** | `WelcomeStoresInventoryService.cs` - comma/semicolon separated COMPANY ids που συμμετέχουν στο multi-company stock lookup (π.χ. `1000,2000,2001,2003,2052`). Δεν υπάρχει hardcoded fallback. |
| 500061 | WelcomeStores - Master Item Company | ParamValue | — | **Ναι** | `WelcomeStoresInventoryService.cs` - COMPANY id της master εταιρίας από την οποία γίνεται η canonical αναζήτηση είδους (τρέχουσα εγκατάσταση: `1000`). Δεν υπάρχει hardcoded fallback. |
| 500062 | WelcomeStores - Purchase Order Series by Company | ParamValueString | — | **Ναι (για Παραγγελία)** | `WelcomeStoresInventoryService.ResolvePurchaseOrderSeries` - mapping `COMPANY=SERIES` χωρισμένο με `;` (π.χ. `1000=120;2000=220`). Η SERIES επαληθεύεται στην logged-in εταιρία ότι ανήκει στο `PURDOC` / `SOSOURCE=1251` πριν γίνει write. |

## Οδηγός: πώς γράφεις SQL για ένα Dashboard Panel (500040-500059)

Ρητό αίτημα χρήστη 20/08 ("θα πρέπει να μου δώσεις μια οδηγία για το πώς
πρέπει να συντάσσονται τα ερωτήματα"). Κανόνες:

1. **Ένα `SELECT`**, τουλάχιστον 2 στήλες.
2. **Στήλη 1** = οι ετικέτες (X-άξονας) - π.χ. όνομα πελάτη/προϊόντος.
   Κείμενο.
3. **Στήλες 2, 3, ...** = αριθμητικά δεδομένα. ΚΑΘΕ στήλη γίνεται δικό
   της dataset/χρώμα στο γράφημα, και το **όνομα της στήλης** γίνεται η
   ετικέτα του (π.χ. `SUM(F.SUMAMNT) AS Τζίρο` - αυτό το "Τζίρο" θα δει
   ο χειριστής στο legend).
4. **Ημερομηνία**: αν το query τη χρειάζεται, βάλε ΑΚΡΙΒΩΣ `:1` όπου
   χρειάζεται (π.χ. `WHERE F.TRNDATE = :1`). Αν δεν τη χρειάζεται (π.χ.
   τιμοκατάλογος), μην το βάλεις καθόλου - δεν είναι υποχρεωτικό.
5. **Σειρά/όριο γραμμών**: βάλε ο ίδιος `ORDER BY`/`TOP N` στο SQL - ΔΕΝ
   εφαρμόζεται κανένα αυτόματο όριο από τον κώδικα.
6. **Ωμοί αριθμοί** στις στήλες τιμών - χωρίς €, χωρίς κόμματα χιλιάδων
   (η μορφοποίηση/decimal places γίνεται στο ίδιο το γράφημα).
7. **`ParamValue`** (η ΑΛΛΗ στήλη του ΙΔΙΟΥ param, όχι ξεχωριστό
   ParamCode) = τύπος γραφήματος: `1`=bar, `2`=line, `3`=pie,
   `4`=doughnut (οτιδήποτε άλλο/κενό → bar).
8. Άδειο `ParamValueString` = το slot είναι ανενεργό (παραλείπεται
   σιωπηλά, δεν εμφανίζεται καθόλου panel).
9. Αν το query σκάσει (λάθος SQL) ή γυρίσει 0 γραμμές, το ΣΥΓΚΕΚΡΙΜΕΝΟ
   panel απλά δεν εμφανίζεται - ΔΕΝ ρίχνει τα υπόλοιπα panels/όλο το
   dashboard (βλ. `DashboardPanels.BuildDashboardText`, try/catch ανά
   panel).

## Roadmap / αναφερόμενα στο README αλλά ΔΕΝ υλοποιημένα ακόμα

Αυτά αναφέρονται στο README σαν ιδέες για μελλοντικό email-order/eshop
automation flow - **δεν διαβάζονται από κανένα σημείο του τρέχοντος
κώδικα** (επιβεβαιώθηκε με αναζήτηση, 18/08). Κρατιούνται εδώ μόνο για
αναφορά, ΜΗΝ τα θεωρήσεις ενεργά:

| ParamCode | Σκοπός (σχεδιασμένος) |
|---|---|
| 50003 | Σειρά Καταχώρησης Παραγγελίας AI (email-orders flow) |
| 50004 | Σειρά Λιανικής AI eShop |
| 50005 | Σειρά τιμολόγησης AI eShop (όταν ζητηθεί τιμολόγιο) |
| 50006 | On/off αυτόματο email ενημέρωσης πελάτη (email-orders flow) |
| 50007 | On/off αυτόματο email ενημέρωσης πελάτη (eshop flow) |

## Πώς να προσθέσεις νέα παράμετρο

1. Διάλεξε το επόμενο ελεύθερο `ParamCode` (σειριακά, `500XXX`) - έλεγξε
   πρώτα το `grep -rhoE "500[0-9]{3}" Core/ UI/JarvisShell.xaml.cs | sort -un`
   για να μη διπλασιάσεις κάποιο ήδη υπάρχον.
2. Reuse το ήδη υπάρχον `JarvisTools.GetCrmTaskOptionalParam(xSupport,
   paramCode, defaultValue)` για απλά numeric params με ασφαλές default -
   ΜΗΝ ξαναγράφεις το ίδιο SQL/try-catch pattern από την αρχή.
3. Ενημέρωσε ΚΑΙ αυτό το αρχείο ΚΑΙ το σχετικό σχόλιο στον κώδικα (ίδιο
   idiom με τα υπόλοιπα - το `ParamCode` πάντα σαν comment δίπλα στο
   query/named const).
