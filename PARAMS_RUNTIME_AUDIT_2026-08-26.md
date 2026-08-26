# S1Jarvis cccParams runtime audit — 2026-08-26

## Συμπέρασμα για DR / DocReader

Έλεγχος των ενεργών DR flows (`DrDocumentPatternResolver`, `DrExpenseDocumentRegistrar`,
`DrDocumentAuditMarker`, `DrItemCodeResolver`, `JarvisShell.DrRecognitionFlow`) δεν έδειξε
υποχρεωτικό DR-specific `cccParams` ParamCode. Το DR χρησιμοποιεί το κοινό `500000` μόνο
για debug file logging. Το `500026` σχετίζεται λειτουργικά με item-copy behavior και
ακολουθεί το ίδιο whitelist idiom με DR carry-over fields, αλλά έχει hardcoded fallback και
δεν είναι υποχρεωτικό.

Άρα missing DR parameter από μόνο του δεν πρέπει να θεωρείται η αιτία για process exit.
Πιθανότερα σημεία αποτυχίας στο DR είναι Soft1 object/schema/runtime operations
(`MTRL.CCCMAPITEMS`, FINDOC custom audit fields, WebView2 event handling, XModule/PostData).
Τα περισσότερα DR operations ήδη επιστρέφουν controlled errors. Το runtime startup πλέον
έχει επιπλέον fail-safe parameter audit και isolated optional-feature initialization ώστε
missing/invalid configuration να μη διαφεύγει προς το Soft1 host.

## Runtime hardening

- `JarvisParameterAudit.Run(XSupport)` εκτελείται πριν δημιουργηθεί το `JarvisShell`.
- Η επιθεώρηση `cccParams` δεν πετάει exception προς Soft1.
- Missing required feature params καταγράφονται στο debug log.
- Email credentials (500019-500021) είναι feature-scoped: η απουσία τους δεν μπλοκάρει boot.
- Provider health / AI usage UI / aggregation αρχικοποιούνται ανεξάρτητα. Failure σε ένα
  optional feature δεν εμποδίζει να ανοίξει το Jarvis shell.
- Αν αποτύχει ο constructor του Jarvis shell, επιστρέφεται ασφαλές error view αντί να
  διαφύγει exception στον Soft1 host.

## Παράμετροι

| ParamCode | Περιγραφή | Τιμές / μορφή | Default αν λείπει | Υποχρεωτική; |
|---|---|---|---|---|
| 500000 | Debug file logging Jarvis/Courier/DocReader | `0`=off, `1`=on | off | Όχι |
| 500002 | Courier dynamic receiver mapping | `ParamValueString`: SELECT query template | hardcoded FINDOC/MTRDOC/TRDR mapping | Όχι |
| 500008 | Knowledge Base / Q&A log SERIES | θετικό Soft1 `SERIES` id | κανένα | Ναι, όταν χρησιμοποιείται το Q&A log feature |
| 500009 | Δεκαδικά σε reports AI | ακέραιος αριθμός δεκαδικών (λογικό εύρος εφαρμόζεται από κώδικα) | `2` | Όχι |
| 500011 | Max rows direct export | ακέραιος `>=0`, `0`=χωρίς όριο | `5000` | Όχι |
| 500012 | CRM Tasks SERIES | θετικό Soft1 `SERIES` id | κανένα | Ναι, όταν γίνεται `create_crm_task` |
| 500013 | CRM task ACTSTATES | ακέραιο Soft1 state id | `1001` | Όχι |
| 500014 | CRM task ACTSTATUS | ακέραιο Soft1 status id | `1` | Όχι |
| 500015 | Dashboard Tasks auto-refresh | λεπτά, θετικός ακέραιος | `5` | Όχι |
| 500016 | Order-entry confidence threshold | ποσοστό `1..100` (π.χ. `85`) | `85` | Όχι |
| 500017 | Order Prompt Log SERIES | θετικό Soft1 `SERIES` id | κανένα | Ναι, όταν γίνεται order prompt logging |
| 500018 | Native Soft1 form ανά SOSOURCE | `sosource=FormName;...` | καμία override | Όχι |
| 500019 | Email OAuth Client ID | non-empty string | κανένα | Ναι μόνο για Email feature |
| 500020 | Email OAuth Tenant ID | non-empty string | κανένα | Ναι μόνο για Email feature |
| 500021 | Email OAuth Client Secret | non-empty secret value | κανένα | Ναι μόνο για Email feature |
| 500022 | Email Inbox max emails | θετικός ακέραιος (`$top`) | `100` | Όχι |
| 500023 | Outlook Calendar max events | θετικός ακέραιος (`$top`) | `100` | Όχι |
| 500024 | Dashboard Tasks max rows | θετικός ακέραιος | `100` | Όχι |
| 500025 | Browser `read_page_content` max chars | θετικός ακέραιος χαρακτήρων | `40000` | Όχι |
| 500026 | Item copy fields whitelist | comma-separated `MTRL` field names | hardcoded whitelist | Όχι |
| 500027 | Πρόσθετες οδηγίες διαχειριστή | ελεύθερο `ParamValueString` business context | κενό / καμία οδηγία | Όχι |
| 500028 | Bulk Import max iterations | θετικός ακέραιος | `40` για bulk flow | Όχι |
| 500029 | AI model Atlas | model identifier string | `claude-opus-5` | Όχι |
| 500030 | AI model Forge | model identifier string | `claude-opus-5` | Όχι |
| 500031 | AI model Compass | model identifier string | `claude-opus-5` | Όχι |
| 500032 | AI model Echo | model identifier string | `claude-opus-5` | Όχι |
| 500033 | AI model Sprint | model identifier string | `claude-opus-5` | Όχι |
| 500034 | AI model Scout | model identifier string | `claude-opus-5` | Όχι |
| 500035 | AI model Sage | model identifier string | `claude-opus-5` | Όχι |
| 500040-500059 | Commercial Dashboard panel slots | `ParamValue`: `1` bar, `2` line, `3` pie, `4` doughnut; άλλο/κενό => bar. `ParamValueString`: SELECT SQL, προαιρετικά `:1` για ημερομηνία | κενό slot = ανενεργό | Όχι |

### Dashboard slots 500040-500043

- `500040`: Top 10 πελάτες με τζίρο ημέρας.
- `500041`: Top 10 προϊόντα σε τεμάχια ημέρας.
- `500042`: Top 10 προϊόντα με τζίρο ημέρας.
- `500043`: τρέχουσες τιμές ανά προϊόν (`MTRL.PRICEW`), όχι υποχρεωτικά ημερήσιο query.
- `500044-500059`: ελεύθερα slots για επόμενα panels.

## Σημαντική διάκριση

`Υποχρεωτική` σημαίνει υποχρεωτική για το συγκεκριμένο feature, όχι για να ξεκινήσει το
Soft1/Jarvis. Μετά το hardening, missing configuration πρέπει να απενεργοποιεί ή να αποτυγχάνει
μόνο το σχετικό feature με controlled error/log και όχι να τερματίζει το Soft1 process.
