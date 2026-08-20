# S1Jarvis

Jarvis assistant object για το Soft1 — WPF/WebView2 UI, ανοίγει από συντόμευση
στο Menu, μιλάει στο Soft1 μέσω του S1 SDK (`Softone.Lib.dll`, in-process).

## Κατάσταση

| Phase | Τι είναι | Status |
|---|---|---|
| 0 | Menu shortcut → ανοίγει shell με πρόσβαση σε session context/DB | ✅ Δουλεύει, επιβεβαιωμένο στο Soft1 |
| 1 | WebView2 chat UI (animated orb + composer + transcript) | ✅ Δουλεύει, επιβεβαιωμένο στο Soft1 |
| 2a | Licensing μέσω Nexus (`/access/check`) | ✅ Επιβεβαιωμένο (curl στο production endpoint: `allowed:true`) |
| 2b | Claude API tool-use loop μέσω Nexus (`/agent/vision`) | ✅ Επιβεβαιωμένο ζωντανά (14/08) - query_data, σχήμα-ανακάλυψη, quick-reply, attach/vision |
| 3 | Reports σε CSV/Excel/Dashboard | ✅ Export Excel/CSV/PDF + Dashboard με live γραφήματα (14/08) - βλ. λεπτομέρειες παρακάτω |
| 4 | Email/e-shop/courier integration (βλ. "Roadmap ιδέες" παρακάτω) | Σε εξέλιξη - Email/Calendar curtain ✅ (17/08), αποστολή/απάντηση email ✅ κώδικας (18/08, ΕΚΚΡΕΜΕΙ ζωντανό test + Mail.Send permission στο Azure AD, βλ. λεπτομέρειες παρακάτω), JARVISCOURIER ✅ (17-18/08, βλ. "✅ JARVISCOURIER" παρακάτω) - E-shop (WooCommerce) εκκρεμεί |

## Roadmap ιδέες (καταγεγραμμένες 14/08 - θα αναλυθούν μία-μία, ΟΧΙ ακόμα σχεδιασμένες)

1. **Email agent** — διαδικασία ώστε ο agent να συνδέεται στο email του
   εκάστοτε χρήστη που μιλάει, να διαβάζει τα emails, και:
   - **Ταξινόμηση ανά email**: αναγνωρίζει αν είναι παραγγελία διαβάζοντας
     ΚΑΙ θέμα ΚΑΙ περιεχόμενο, ψάχνοντας για: τη λέξη "παραγγελία"/"Order"
     στο θέμα, κωδικούς ειδών ή περιγραφές, τη λέξη "ποσότητα"/"qty" με
     αντίστοιχες ποσότητες. Απαιτείται **δείκτης αναγνώρισης**: αν έχει
     αναγνωρίσει πάνω από **90%** των δεδομένων ως είδη, καταχωρεί την
     παραγγελία (κάτω από αυτό → όχι αυτόματη καταχώρηση, βλ. wizard
     παρακάτω).
     - Νέα παραγγελία → την καταχωρεί.
     - Διορθωτική παραγγελία (αναφέρεται σε προηγούμενη) → ΔΕΝ καταχωρεί
       καινούρια, χειρίζεται σαν update της προηγούμενης (TBD πώς
       ακριβώς αναγνωρίζει ότι είναι διόρθωση).
     - Μετά από κάθε καταχώρηση (νέα ή διόρθωση) → αυτόματη απάντηση στον
       πελάτη με την εξέλιξη του αιτήματός του.
   - **Αναγνώριση πελάτη (TRDR)** από τον αποστολέα:
     a. Πρώτα ψάχνει το email αποστολέα μέσα στο πεδίο `TRDR.EMAIL`.
     b. Δευτερευόντως, το κομμάτι πριν το "@" ίσως έχει συγγένεια με το
        `TRDR.NAME`.
     Θα δοθεί **ρητή επιχειρησιακή οδηγία** (όχι τεχνική λύση): οι πελάτες
     θα ενημερωθούν να έχουν καταχωρημένο το email τους στο σχετικό πεδίο
     του Soft1, και να βάζουν τη λέξη "order"/"παραγγελία" στο θέμα.
   - **Σειρά καταχώρησης**: νέα παράμετρος **"Σειρά Καταχώρησης
     Παραγγελίας AI"**, `ParamCode = 50003` (ίδιο pattern με το
     `cccParams`/`DebugParamCode=500000` που ήδη χρησιμοποιείται) - εκεί
     δηλώνεται παραμετρικά η σειρά (`SERIES`) στην οποία ο agent καταχωρεί
     την παραγγελία. Λύνει το ανοιχτό ερώτημα "ποιο κύκλωμα" - ΔΕΝ το
     αποφασίζει δυναμικά η AI, είναι σταθερή παράμετρος.
   - **Wizard για μη-αντιστοιχισμένα είδη**: όταν μια γραμμή παραγγελίας
     δεν έχει αντιστοίχιση κωδικού Soft1, wizard όπου ο χειριστής επιλέγει
     τον κωδικό είδους/υπηρεσίας και το σύστημα αποθηκεύει την
     αντιστοίχιση για την επόμενη φορά - **το S1DocReader έχει ήδη έτοιμο
     αυτό το κομμάτι** (`Soft1Bridge.FindItemBySupplierCode` /
     `SaveSupplierCodeMapping` πάνω στον πίνακα `MTRSUPCODE`,
     `PromptItemLookup`/`Soft1ItemLookupForm` για το popup - βλ.
     `S1DocReader/Soft1/Soft1Bridge.cs`). Ο χρήστης θα εξηγήσει αργότερα
     ακριβώς πώς θέλει να γίνει η αποθήκευση εδώ - **κρατημένο σαν
     σημείωση, όχι αποφασισμένο ακόμα**.
   - **Courier αντιστοίχιση**: πίνακας παραμέτρων courier ώστε αν ο
     πελάτης ζητάει στο email αποστολή με συγκεκριμένο courier, να γεμίζει
     αυτόματα το σχετικό πεδίο στην παραγγελία.
   - **Απολογισμός** (recap στον χρήστη/χειριστή, ΟΧΙ αυτόματο για τα μη-
     παραγγελίες): για τα υπόλοιπα emails (αυτά που δεν αναγνωρίστηκαν σαν
     παραγγελία), δίνει σύνοψη περιεχομένου ώστε ο χρήστης να δώσει
     οδηγίες ανάλογα, π.χ.:
     - "Μοίρασε αυτό σε συνάδελφο Χ" → δημιουργία CRM task (ίδιο pattern
       με `SOACTION`/`ACTLINES`, βλ. S1Courier's `SOTASK` hook - ήδη
       αναφερόμενο σαν Phase 2c στόχος).
     - "Στείλε απάντηση στον αποστολέα με περιεχόμενο Χ" → ο Jarvis
       στέλνει το email.
   Επεκτείνει το ήδη υπάρχον Phase 4 "Outlook integration".

   **Email curtain (ΝΕΟ 17/08, ρητό αίτημα χρήστη - βλ. session notes):**
   Ξεχωριστή κουρτίνα `#emailCurtain` (index.html, ίδιο slide-up idiom με
   Dashboard/Help/DR - ΧΩΡΙΣ native pane, σε αντίθεση με το Browser mode),
   trigger με exact-match "email" στο composer. Δύο κάθετα tabs (ίδιο
   idiom με τα Dashboard tabs) + ΣΤΑΘΕΡΟ πλάτος chat pane δεξιά (ίδια
   φιλοσοφία με το DR 30/70 split, ΧΩΡΙΣ σέρσιμο) - το chat μιλάει
   ΚΑΙ για τα δύο tabs (νέο `emailMode` στο JarvisAgentClient.AskAsync/
   BuildSystemPrompt, νέο `_emailConversation` στο JarvisShell.xaml.cs,
   ίδιο idiom με το Browser mode). Tools σε Email mode: query_data,
   export_query_to_file, open_document, create_crm_task, read_email,
   download_email_attachment (ΧΩΡΙΣ open_url/read_page_content - άσχετα
   εδώ, ΧΩΡΙΣ get_conversion_targets/create_order - άσχετα με email/
   calendar workflow).
   - **Calendar tab spec** (verbatim, session notes 17/08): συγχρονίζει
     το Outlook calendar του λογαριασμού (Graph API, χρειάζεται ΝΕΟ
     `Calendars.Read` Application permission - ο χρήστης πρέπει να το
     προσθέσει στο ίδιο Azure AD App Registration/Application Access
     Policy με το ήδη υπάρχον `Mail.Read`, βλ. `JarvisEmailAccess.cs`
     header) ΚΑΙ εμφανίζει SOACTIONs ανοιχτά στις ώρες FROMDATE - scope
     **ACTOR=τρέχων χρήστης** (default απόφαση 17/08, ΟΧΙ ORDEREDBY - το
     Tasks dashboard tab ήδη χρησιμοποιεί ORDEREDBY για διαφορετικό σκοπό,
     βλ. παρακάτω "Dashboard 'Tasks - Εργασίες'"). Αν `SOACTION.DURATION`
     είναι NULL -> placeholder 30 λεπτά, αλλιώς
     `DATEDIFF(MINUTE, 0, DURATION)` λεπτά (βλ. "SOACTION.DURATION" στα
     Επιβεβαιωμένα SDK facts πιο πάνω). Διπλό-κλικ σε εγγραφή -> ΚΟΙΝΟ
     Modal (το ήδη υπάρχον "tasks" modal/#taskCard) και για τις δύο
     περιπτώσεις: Soft1 SOACTION -> συμπληρωμένο από τα στοιχεία της
     εγγραφής· Outlook event -> field mapping ώστε να γεμίζει το ίδιο
     modal. **Εξαίρεση Outlook**: όταν δεν υπάρχει αρχική SOACTION
     (καθαρό Outlook event), δεν μπορεί να γίνει "Ολοκλήρωση" - μόνο
     "Επόμενη ενέργεια" (νέο SOACTION, `SOACTIONS` parent field ΚΕΝΟ -
     δεν υπήρχε αρχική ενέργεια να δείξει), με `REMARKS` = περίληψη
     μηνύματος/ραντεβού + η οδηγία ενέργειας που έδωσε ο χειριστής.
   - **Email tab**: spec εκκρεμεί ακόμα - ο χρήστης θα το περιγράψει
     αργότερα.
   - **Calendar tab - υλοποιήθηκε (17/08)**: `JarvisEmailAccess.
     GetCalendarEventsAsync` (Graph `calendarView`, ΟΧΙ `/events` - επεκτείνει
     recurring events· `Prefer: outlook.timezone="GTB Standard Time"` ώστε οι
     ώρες να έρχονται ήδη σε τοπική ώρα Ελλάδας, συγκρίσιμες απευθείας με το
     `SOACTION.FROMDATE`) + `read_calendar` tool (chat, ίδιο idiom με
     read_email) + `JarvisTools.GetSoactionCalendarEntries` (ACTOR=τρέχων
     χρήστης, end=FROMDATE+DURATION/placeholder 30') + `JarvisShell.
     HandleEmailGetCalendarAsync` (merge, Outlook sync failure ΔΕΝ κρύβει τις
     Soft1 εγγραφές - δείχνει warning) + απλή χρονολογική λίστα στο UI
     (`renderEmailCalendarList`, index.html) με date picker.
   - **Κοινό Modal - υλοποιήθηκε (17/08, task #34)**: double-click σε
     γραμμή -> ΞΑΝΑΧΡΗΣΙΜΟΠΟΙΕΙ ΑΥΤΟΥΣΙΟ το ήδη υπάρχον `taskCompleteModal`
     (Dashboard Tasks tab). Soft1 εγγραφή -> `openCalendarSoft1Modal`
     (synthetic "button" με τα data-attributes, ίδιο flow/Ολοκλήρωση+
     Επόμενη ενέργεια ΧΩΡΙΣ καμία αλλαγή στο υπάρχον `submitTaskComplete`).
     Outlook event -> `openCalendarOutlookModal` (ΚΡΥΒΕΙ "Ολοκλήρωση" -
     δεν υπάρχει soaction να ολοκληρωθεί, μόνο "Δημιουργία ενέργειας" ->
     `openCalendarOutlookNextAction`: ανοίγει ΑΠΕΥΘΕΙΑΣ το taskCard,
     `parentSoactionId=null` - ρητή απαίτηση χρήστη, το `SOACTIONS` μένει
     κενό - `description` = "Από Outlook: <θέμα> (<τοποθεσία>) — <ώρα>" +
     η σημείωση του χειριστή). Επιβεβαιωμένο ζωντανά (headless DOM test,
     17/08) - και τα δύο paths, σωστό state σε κάθε βήμα.
     **Γνωστό follow-up**: μετά από "Ολοκλήρωση" από το Calendar tab, η
     λίστα ΔΕΝ κάνει auto-refresh (μόνο το Dashboard Tasks tab το κάνει) -
     χρειάζεται χειροκίνητο "↻ Ανανέωση".
   - **Email tab - υλοποιήθηκε (17/08, task #35, verbatim spec χρήστη)**:
     "θα φέρνει τα email που είναι αδιάβαστα την τελευταία εβδομάδα
     (date-7) με φίλτρα ημερομηνιακά - θα μπορεί να αλλάζει και να πατά
     ανανέωση. Το chat είναι αυτό που θα αναλαμβάνει να ψάξει πιο σύνθετα
     πράγματα." `JarvisEmailAccess.GetInboxEmailsAsync` (Graph `$filter`
     `isRead eq false and receivedDateTime ge <date>`, ΞΕΧΩΡΙΣΤΟ από το
     `read_email` tool - deterministic UI fetch, ΟΧΙ AI) + date picker
     (default σήμερα-7) + "↻ Ανανέωση" + λίστα (Ημερομηνία/Αποστολέας/
     Θέμα/κουμπί "Ενέργεια"). Το "Ενέργεια" **ξαναχρησιμοποιεί ΑΥΤΟΥΣΙΑ**
     το `openCalendarOutlookModal`/`openCalendarOutlookNextAction` του
     Calendar tab (`entry.source:'email'`, γενικεύτηκε το prefix "Από
     Email:"/"Από Outlook:") - ρητό αίτημα χρήστη "θα ακολουθεί την λογική
     του calendar όπως το αναλύσαμε εκεί". Επιβεβαιωμένο ζωντανά (headless
     DOM test, 17/08).
   - **Flag/συνημμένα/detail modal - υλοποιήθηκε (17/08, task #36)**:
     ❗ θαυμαστικό αν `flag.flagStatus==='flagged'` (Graph). 2η γραμμή με
     ΟΝΟΜΑΤΑ συνημμένων (Graph `$expand=attachments($select=id,name,
     contentType,size)` - ΧΩΡΙΣ contentBytes στη λίστα, βαρύ) - κάθε όνομα
     είναι link (κλικ = `email_download_attachment` με συγκεκριμένο
     `attachmentName`, deterministic - ξαναχρησιμοποιεί ΑΥΤΟΥΣΙΟ το ήδη
     υπάρχον `ExecuteDownloadEmailAttachment`, κλικ = download+auto-open
     άμεσα) + κουμπί "⬇ Όλα" αν >1 συνημμένα (χωρίς `attachmentName` -
     αποτέλεσμα: κλικαριστά `.file-link` links, ίδιο idiom με chat
     exports). Double-click σε "Object" -> `#emailDetailModalOverlay`
     ("σαν να είναι Outlook" - `JarvisEmailAccess.GetEmailDetailAsync`,
     ξεχωριστό per-email request, ΟΧΙ μαζί με τη λίστα - το σώμα/HTML είναι
     βαρύ). Σώμα σε **sandboxed iframe** (`sandbox=""` - ΚΑΝΕΝΑ δικαίωμα,
     ΟΧΙ scripts/forms/popups - το HTML ενός email είναι ΑΝΑΞΙΟΠΙΣΤΟ
     περιεχόμενο) ή `<div>` για text-only bodies. Header ΕΧΕΙ ΚΑΙ κουμπί
     "📋 Επόμενη Ενέργεια" (ΝΕΟ 17/08 - ίδιο `openCalendarOutlookModal`
     με το "Ενέργεια" της λίστας, απλά κλείνει πρώτα το detail modal).
     Επιβεβαιωμένο ζωντανά (headless DOM test, 17/08 - flag/attachments/
     dblclick/iframe sandbox/download+auto-open/close όλα δοκιμασμένα).
   - **Ταβάνια λιστών - παραμετρικά (ΝΕΟ 17/08, ρητό αίτημα χρήστη)**: όλα
     default **100** αν λείπει η παράμετρος (`JarvisTools.
     GetCrmTaskOptionalParam` - ΝΕΟ πλέον `internal`, ΟΧΙ `private`, ώστε
     να το ξαναχρησιμοποιεί ΚΑΙ το `JarvisEmailAccess.cs`):
     - `500022` "Jarvis - Email Inbox Max Emails" (Email tab, Graph `$top`)
     - `500023` "Jarvis - Calendar Outlook Max Events" (Calendar tab
       Outlook side, Graph `$top`)
     - `500024` "Jarvis - Dashboard Tasks Max Rows" (Dashboard "Tasks -
       Εργασίες" tab, SQL `TOP` - **ΔΙΟΡΘΩΘΗΚΕ από hardcoded 200 σε
       παραμετρικό 100 default**, ρητή απόφαση χρήστη)
     Το Calendar tab **Soft1-πλευρά** (SOACTION, `GetSoactionCalendarEntries`)
     ΕΠΙΤΗΔΕΣ ΔΕΝ έχει κανένα ταβάνι - φυσικά bounded από το
     ημερήσιο date-range φίλτρο, ρητή απόφαση χρήστη ("να τις φέρνει όλες").
   - **Chat vs main window - διάκριση (ΝΕΟ 17/08, ρητό αίτημα χρήστη)**:
     "η απόκριση έγινε ως απάντηση μέσα στο chat box - οι πληροφορίες για
     φιλτράρισμα θέλω να γίνονται στο main παράθυρο... στο chat box θέλω
     να μένει ΜΟΝΟ chat." Νέα tools **`filter_email_inbox`**/
     **`filter_calendar`** (`JarvisEmailAccess.cs`) - ΔΕΝ φέρνουν τα ίδια
     τα δεδομένα (καμία κλήση Graph εδώ), απλά ενεργοποιούν callback
     (`onFilterEmailInbox`/`onFilterCalendar`, νέα params στο
     `JarvisAgentClient.AskAsync`/`ExecuteTool`) που στέλνει postMessage
     (`email_set_inbox_filter`/`email_set_calendar_filter`) στο index.html -
     ΕΚΕΙΝΟ αλλάζει το date filter, γυρνάει στο σωστό tab, ΚΑΙ ξανακαλεί
     το ΙΔΙΟ deterministic fetch (`email_get_inbox`/`email_get_calendar`)
     που ήδη χρησιμοποιεί το toolbar - ΜΙΑ πηγή αλήθειας για τη λίστα, το
     chat απλά την "τηλεχειρίζεται". System prompt (emailMode) ρητά
     απαγορεύει στον Claude να απαντήσει με λίστα email/events μέσα στο
     chat όταν πρόκειται για αλλαγή φίλτρου - μόνο σύντομη επιβεβαίωση.
     Το `read_email`/`read_calendar` παραμένουν ΜΟΝΟ για σημειακές
     ερωτήσεις που ΔΕΝ αντιστοιχούν σε αλλαγή του κύριου φίλτρου (π.χ.
     "ήρθε απάντηση από τον Χ;") - εκεί απαντάει κανονικά στο chat.
   - **Σύνθετο φίλτρο (ημερομηνία + searchText) - ΝΕΟ 17/08, ρητό αίτημα
     χρήστη ("συνθέτει φίλτρο, δηλαδή ημερομηνία και κάτι ακόμα που θα του
     πω")**: `filter_email_inbox`/`filter_calendar` δέχονται ΚΑΙ
     προαιρετικό `searchText` (ΜΑΖΙ με την ημερομηνία, ΟΧΙ εναλλακτικά) -
     αποστολέας/λέξη-κλειδί θέματος (email) ή λέξη-κλειδί θέματος
     εργασίας/ραντεβού (calendar). Φιλτράρισμα **client-side στο C#**
     (ΟΧΙ Graph `$search` - ήδη τεκμηριωμένος conflict με
     `$filter`/`$orderby`, βλ. `ExecuteReadEmail`) πάνω στο ήδη-fetch-αρισμένο
     (date-bounded) σύνολο· στο Calendar tab Soft1-πλευρά, SQL
     `A.COMMENTS LIKE :N` (ΥΠΟ ΣΥΝΘΗΚΗ στο query string - ΟΧΙ πάντα
     `:N IS NULL OR...` idiom, πιο προβλέψιμο). Callbacks
     `onFilterEmailInbox`/`onFilterCalendar` έγιναν `Action<string,string,string>`
     (date, searchText, insight - βλ. επόμενο bullet) σε όλη την αλυσίδα.
     Ενεργό searchText δείχνεται με **chip** (`🔍 <κείμενο> ✕`) πάνω από τη
     λίστα - παραμένει ενεργό ΚΑΙ σε επόμενα χειροκίνητα "↻ Ανανέωση"/
     αλλαγή ημερομηνίας (δεν πετιέται σιωπηλά), καθαρίζει μόνο με ρητό
     κλικ στο ✕. Επιβεβαιωμένο ζωντανά.
   - **"insight" - 2ο ζωντανό bug fix (17/08)**: παρόλο το παραπάνω, ένα
     σύνθετο ερώτημα ("δείξε τις εργασίες με μοναδικό θέμα σήμερα + αν
     υπάρχουν Outlook events") έδωσε σωστή απάντηση αλλά ΞΑΝΑ μέσα στο
     chat text (ο Claude παρέκαμψε ΤΕΛΕΙΩΣ το filter_calendar επειδή το
     ανέλυσε σαν "καθαρά αναλυτικό" αίτημα - το system prompt έδειχνε
     filter_calendar/read_calendar σαν binary επιλογή, όχι "και τα δύο
     μαζί"). **Fix**: νέο προαιρετικό param **`insight`** στο
     `filter_email_inbox`/`filter_calendar` - όταν το αίτημα έχει
     αναλυτικό κομμάτι, ο Claude καλεί ΠΡΩΤΑ query_data/read_X, ΜΕΤΑ
     filter_X με το εύρημα ΣΤΟ insight param (ΟΧΙ στο chat reply). Το JS
     δείχνει το insight σε **δική του κάρτα** (`.email-insight-card`,
     πράσινη, ΠΑΝΩ από τη λίστα/κάτω από το toolbar) - "στιγμιότυπο" της
     συγκεκριμένης ερώτησης, καθαρίζει σε ΚΑΘΕ επόμενο χειροκίνητο refresh/
     αλλαγή ημερομηνίας (`manualRefreshEmailInbox`/`manualRefreshEmailCalendar`
     wrappers γύρω από τα toolbar events) - ΔΕΝ παραμένει σαν το φίλτρο
     searchText (διαφορετική σημασιολογία: "ανάλυση ΤΗΣ ΣΤΙΓΜΗΣ" vs
     "ενεργό φίλτρο"). Η τελική chat απάντηση μένει ΜΟΝΟ 1 σύντομη
     επιβεβαίωση - ρητή απαίτηση χειριστή "στο chat box θέλω να μένει ΜΟΝΟ
     chat", ΑΚΟΜΑ ΚΙ όταν υπάρχει αναλυτικό περιεχόμενο. Επιβεβαιωμένο
     ζωντανά (insight εμφανίζεται σωστά, καθαρίζει σωστά σε manual refresh).
   - **"hideRepeatedSubjects" - δοκιμάστηκε, ΑΠΟΤΥΧΕ, ΑΦΑΙΡΕΘΗΚΕ (17/08,
     3ο→4ο ζωντανό bug report)**: ακόμα και με το insight, ο χειριστής
     ζήτησε ρητά "εργασίες με μοναδικό θέμα" ΞΑΝΑ και η ΛΙΣΤΑ έμεινε
     αφιλτράριστη (screenshot: insight card σωστό, λίστα ίδια 1230+
     Endress επαναλήψεις). Πρώτη προσπάθεια: νέο param
     `hideRepeatedSubjects` (bool) σε `filter_calendar` + SQL
     `GROUP BY COMMENTS HAVING COUNT(*)=1` - ΑΠΕΔΕΙΧΘΗ ΕΛΑΤΤΩΜΑΤΙΚΟ: ο
     χειριστής εξήγησε ότι το "επαναλαμβανόμενο" θέμα έχει **διαφορετική
     ώρα μέσα στον ίδιο τον τίτλο** - άρα ΚΑΘΕ γραμμή είναι τεχνικά
     byte-μοναδικό string, το `COUNT(*)=1` δεν φιλτράρει ΤΙΠΟΤΑ (ψευδο-
     επιτυχία). Προστέθηκε ΚΑΙ ντετερμινιστικό checkbox στο toolbar -
     αλλά ο χειριστής το χαρακτήρισε ρητά **"άχρηστο checkbox"** και ζήτησε
     πλήρη αφαίρεση, αφού η υποκείμενη λογική δεν δούλευε σωστά ούτως ή
     άλλως. **Και τα δύο αφαιρέθηκαν εντελώς** (tool param, SQL κλάδος,
     checkbox HTML/CSS/JS, state vars) - μόνο ιστορικά σχόλια έμειναν στον
     κώδικα εξηγώντας γιατί.
   - **"show_calendar_entries" - η ΓΕΝΙΚΕΥΜΕΝΗ λύση που αντικατέστησε το
     hideRepeatedSubjects (17/08)**: ρητή οδηγία χειριστή - "θέλουμε να
     εξαιρεί αυτός [ο Claude] με τις οδηγίες που παίρνει, γιατί το
     κατάφερε στις προηγούμενες δοκιμές (μέσω query_data/ελεύθερο SQL) -
     το μόνο πρόβλημα είναι να είναι εντός του Main παραθύρου." ΑΝΤΙ να
     προσπαθούμε να προβλέψουμε ΚΑΘΕ πιθανό filtering pattern με νέο
     hardcoded SQL param (whack-a-mole), το νέο tool
     **`show_calendar_entries`** αφήνει τον Claude να υπολογίσει
     ΟΠΟΙΑΔΗΠΟΤΕ λογική χρειάζεται μέσω `query_data` (ελεύθερο SQL - LIKE,
     SUBSTRING, PATINDEX, ό,τι χρειαστεί για να αγνοήσει το μεταβλητό
     κομμάτι) και απλά ΜΕΤΑΦΕΡΕΙ το ΗΔΗ-υπολογισμένο αποτέλεσμα (λίστα
     `{soactionId, subject, start, end, statusLabel}`) ΑΠΕΥΘΕΙΑΣ στη λίστα
     του Calendar tab - `renderEmailCalendarList` ΑΥΤΟΥΣΙΟ, ΧΩΡΙΣ κανένα
     δικό του νέο fetch/re-filter. Νέο callback
     `onShowCalendarEntries: Action<string, JArray>` σε όλη την αλυσίδα
     (AskAsync/ExecuteTool/JarvisShell) → postMessage
     `email_set_calendar_results` → JS καλεί `renderEmailCalendarList`
     απευθείας με τα entries. System prompt ρητά προτρέπει προς αυτό το
     tool ΓΙΑ ΚΑΘΕ φιλτράρισμα που το `filter_calendar.searchText` (απλό
     LIKE) δεν μπορεί να εκφράσει. Επιβεβαιωμένο ζωντανά (checkbox
     πράγματι έφυγε, `show_calendar_entries` δείχνει ΑΚΡΙΒΩΣ τις εγγραφές
     που δόθηκαν).
2. **E-shop integration** — σύνδεση σε e-shop για κατέβασμα παραγγελιών,
   με τα API credentials αποθηκευμένα παραμετρικά (ανά πελάτη/εταιρία,
   ίδιο πνεύμα με `CCCAIDOCPARAMS`/`cccParams` πατέντες που ήδη
   χρησιμοποιούνται στο S1DocReader/S1Jarvis).
   - **Πλατφόρμες** (τυποποιημένα carts, με αυτή τη σειρά προτεραιότητας):
     1. **WooCommerce** πρώτα.
     2. **PrestaShop** μετά.
     3. **Shopify** αργότερα, αν χρειαστεί - να γίνει μαζί (ο χρήστης
        θέλει να είναι παρών/να το δουλέψουμε μαζί, όχι μόνος του).
   - **Ταυτοποίηση πελάτη**: με το email που έκανε την παραγγελία (ίδια
     λογική με #1) - το σύστημα ελέγχει αν ο πελάτης είναι ήδη "ανοιχτός"
     (υπάρχει) στο ERP με βάση αντίστοιχο email.
   - **Δίλημμα λιανική/τιμολόγιο** (όταν ο πελάτης βρέθηκε): αν η
     παραγγελία είναι λιανικής → παράμετρος **"Σειρά Λιανικής AI eShop"**
     (`ParamCode 50004`). Αν ο πελάτης ζήτησε έκδοση τιμολογίου → άλλη
     παράμετρος σειράς, **`ParamCode 50005`**.
   - **Αντιστοίχιση τρόπου πληρωμής** και **αντιστοίχιση τρόπου
     αποστολής**: ίδιοι πίνακες αντιστοίχισης με αυτούς που συζητήσαμε
     στο #1 (courier mapping) - reused, όχι ξεχωριστό μηχανισμό.
   - **Αντιστοίχιση κωδικών ειδών**: SKU του cart ↔ πεδίο `CODE` ή `CODE1`
     του πίνακα `MTRL`.
   - **Ειδοποιήσεις πελάτη parametrized** (νέο, εφαρμόζεται ΚΑΙ στο #1
     ΚΑΙ στο #2 - βλ. πίνακας παραμέτρων παρακάτω): αν θα στέλνονται
     αυτόματα emails ενημέρωσης θα είναι ΚΙ ΑΥΤΟ on/off παραμετρικό, ξεχω-
     ριστά για το κάθε flow (`50006` για email-orders/#1, `50007` για
     eshop/#2).
3. **Αρχιτεκτονική απαίτηση για #1 και #2**: να χτιστούν με τρόπο που να
   επιτρέπει ΜΕΛΛΟΝΤΙΚΑ έναν κεντρικό Orchestrator, ρυθμισμένο σε
   scheduler, να τρέχει την ίδια δουλειά αυτόματα/χωρίς χειροκίνητο
   trigger από χρήστη μέσα στο chat. Δηλαδή: η λογική (διάβασμα
   email/e-shop, ό,τι ακολουθεί) πρέπει να ζει σε ένα επίπεδο που δεν
   εξαρτάται από το UI/chat του Jarvis, ώστε να καλείται είτε από
   χρήστη είτε από scheduler.
   - **Polling - διπλό μονοπάτι**: (α) μελλοντικά, κεντρικός agent που
     ΜΟΝΟΣ του, τακτικά/scheduled, κατεβάζει emails/eshop παραγγελίες και
     τις καταχωρεί αυτόματα· (β) ΚΑΙ on-demand από τον ίδιο τον Jarvis
     μέσα από το chat, όποτε το ζητήσει ο χρήστης.
   - Όταν είναι **on-demand** (μέσω Jarvis), πρέπει να καταγράφεται σε
     **task** ποιος και πότε ζήτησε το κατέβασμα παραγγελιών (audit trail).
   - Και στα δύο μονοπάτια (scheduled ΚΑΙ on-demand) στέλνονται τα ίδια
     emails ενημέρωσης στους πελάτες (βλ. `50006`/`50007` παραπάνω).
4. **S1Courier integration** — να μπορεί ο Jarvis να χρησιμοποιεί το
   S1Courier (υπό προϋποθέσεις) για παραγγελίες που έχουν ήδη
   πακεταριστεί, ώστε να εκδίδει αυτόματα voucher αποστολής.
5. **Knowledge base** — δύο επίπεδα, σχεδιασμένα 15/08, βλ. αναλυτικά στο
   "Επόμενο (Phase 2c)" παρακάτω:
   - **Manuals tier** (στατικό): Black Book/πίνακες/οδηγίες χρήσης, bulk-
     loaded μία φορά σε ξεχωριστό πίνακα γνώσης.
   - **Learned-Q&A tier** (δυναμικό, μεγαλώνει): κάθε φορά που ο Jarvis
     βοηθάει χειριστή με πρόβλημα/ερώτημα, καταγράφεται σαν `SOACTION`
     εγγραφή στη δεσμευμένη `SERIES=30000` (✅ ζωντανό test 15/08 - βλ.
     "ΥΛΟΠΟΙΗΘΗΚΕ" section - **ΟΧΙ** `SOACTIONCODE`, γεμίζει μόνο του) -
     reuse του ήδη υπάρχοντος CRM μηχανισμού αντί για νέο πίνακα, ώστε
     να μένει και σαν audit trail μέσα στις κανονικές οθόνες CRM του
     Soft1.
   - **Σειρά αναζήτησης**: Learned-Q&A ΠΡΩΤΑ, Manuals σαν fallback - ρητή
     οδηγία στο system prompt, όχι στην κρίση του μοντέλου.
   - Επίσης χρησιμεύει σαν η ήδη καταγεγραμμένη ανάγκη να έχει ο Jarvis
     έτοιμα (χωρίς trial-and-error κάθε φορά) σχήματα/queries που ήδη
     έχουν δουλέψει - βλ. "ΓΝΩΣΤΟ SCHEMA" στο system prompt
     (`JarvisAgentClient.BuildSystemPrompt`).
6. **DR - Document Reader integration** (σχεδιασμένο 15/08, βλ. flow
   παρακάτω) — ενσωμάτωση των λειτουργιών του ήδη υπάρχοντος
   **`S1DocReader`** project
   (`C:\Users\gkirkmalis.JETOIL.000\source\repos\S1DocReader`) ΜΕΣΑ στον
   Jarvis, ΟΧΙ σαν ξεχωριστό NETDLL/entitlement - reuse του ήδη υπάρχοντος
   κώδικα (extraction models/prompts/Soft1 bridge) όπου γίνεται, νέος
   κώδικας μόνο όπου το context διαφέρει (βλ. "Τι αλλάζει" παρακάτω).

   **Trigger**: ρητή εντολή **"DR"** στο κύριο composer (ίδιο πνεύμα με
   "Dashboard"/"Help"/"Browser") ανοίγει ΝΕΑ κουρτίνα (ίδιο μοτίβο με
   Dashboard/Help/Browser curtains) - εκεί ο χειριστής ανεβάζει **λίστα**
   παραστατικών (PDF/Excel/Word/JPG - το ήδη υπάρχον 📎 paperclip button
   μπήκε επίτηδες γι' αυτό) και ο Jarvis τα επεξεργάζεται **ένα-ένα**.

   **Nexus entitlement**: ξεχωριστός έλεγχος όταν εκτελείται η ρητή εντολή
   DR - **ΝΕΟ, δικό του toolName `JARVISDOCREADER`** (ΔΙΟΡΘΩΘΗΚΕ 15/08 - ΟΧΙ
   το `AiTools.DocReader`/`"DOCREADER"` του S1DocReader, αυτό είναι το
   toolName του ΞΕΧΩΡΙΣΤΟΥ standalone WPF προϊόντος, δικό του εμπορικό
   SKU/agent) πάνω στο ίδιο `POST /access/check` pattern που ήδη
   χρησιμοποιεί ο Jarvis (`Access/JarvisLicenseGuard.cs`) - ΞΕΧΩΡΙΣΤΟ
   entitlement από το γενικό `S1JARVIS`, ώστε να ενεργοποιείται/
   απενεργοποιείται ανεξάρτητα ανά πελάτη. **Καθαρό feature-gate, ΧΩΡΙΣ
   δικό του AI agent στο Nexus** - οι AI κλήσεις δρομολογούνται μέσω του
   ΗΔΗ υπάρχοντος agent account του Jarvis (`_agentAccountRef`) - ο ίδιος ο
   Jarvis είναι ο agent, όχι ξεχωριστός (εμπορική διάκριση του χρήστη:
   standalone DocReader = δικός του agent που αναλώνει tokens, ενσωματωμένο
   JarvisDocReader = ο ίδιος ο Jarvis).

   **Flow (ανά παραστατικό, επιβεβαιωμένο ζωντανά με τον χρήστη 15/08)**:
   1. **Ταυτοποίηση εκδότη**: εξάγει ΑΦΜ εκδότη από τον τίτλο, ψάχνει στο
      Soft1 (`TRDR`, ΧΩΡΙΣ να ξέρει ακόμα SODTYPE - ψάχνει και τα δύο).
      - **Βρέθηκε** → διαβάζει SODTYPE (12/13) ΚΑΙ ψάχνει **ιστορικό**
        παραστατικών ίδιου τύπου για αυτόν τον συναλλασσόμενο, βάσει
        προθέματος αριθμού παραστατικού (π.χ. "ΤΔΑ" από "ΤΔΑ000001" -
        συνήθως το πρόθεμα ΔΗΛΩΝΕΙ τον τύπο) - βρίσκει έτσι τη σειρά που
        συνήθως χρησιμοποιείται γι' αυτόν (ίδια λογική εμπειρικής
        επιβεβαίωσης με το `alreadyConvertedTo` του `get_conversion_targets`,
        βλ. "ΥΛΟΠΟΙΗΘΗΚΕ" section).
      - **ΔΕΝ βρέθηκε** → αυτόματη δημιουργία νέου συναλλασσόμενου μέσω
        ΑΑΔΕ lookup, reuse ΤΩΝ ΗΔΗ ΥΠΑΡΧΟΝΤΩΝ
        `Soft1Bridge.GetAfmDataFromAade` + `Soft1Bridge.CreateTrader` (βλ.
        `S1DocReader/Soft1/Soft1Bridge.cs`) - ΚΑΙ ρωτάει τον χειριστή
        (μιας και δεν υπάρχει ιστορικό να το συμπεράνει): κύκλωμα
        (**checkbox** Αγορές/Δαπάνες - πιθανό SOSOURCE 1251/1253, βλ. ήδη
        υπάρχον dictionary στο `JarvisTools.DocumentObjectsBySosource`) +
        σειρά (**ελεύθερο πεδίο**, χειροκίνητο προς το παρόν - όχι ακόμα
        dropdown/lookup).
   2. **Ανάγνωση γραμμών**: εξάγει line items από την εικόνα/PDF. Αν
      υπάρχει **myDATA QR code/link** πάνω στο παραστατικό, cross-check μαζί
      του (δευτερεύουσα πηγή αλήθειας, πιο ακριβής από OCR - myDATA έχει
      δομημένα δεδομένα, όχι scan).
   3. **Review οθόνη** (μέσα στην κουρτίνα, ΠΡΙΝ την καταχώρηση):
      - Όλες οι γραμμές matched → έτοιμο για καταχώρηση.
      - Κάποιες ΔΕΝ matched, δύο υποπεριπτώσεις:
        - (α) **Το είδος δεν υπάρχει καθόλου** στο Soft1 → θέλει άνοιγμα
          δημιουργίας είδους - **DEFERRED, θα σχεδιαστεί αναλυτικά
          αργότερα** (ρητά του χρήστη 15/08, ΜΗΝ χτιστεί ακόμα).
        - (β) **Το είδος υπάρχει αλλά δεν είναι αντιστοιχισμένο** (κανένα
          `MTRSUPCODE` για αυτόν τον κωδικό εκδότη) → κουμπί ανοίγει
          διάλογο αναζήτησης μητρώου για χειροκίνητη αντιστοίχιση - reuse
          λογικής από `Soft1Bridge.FindItemBySupplierCode`/
          `SaveSupplierCodeMapping` (ίδιοι πίνακες), αλλά ΝΕΟ UI (το
          `PromptItemLookup` του S1DocReader ανοίγει native WinForms popup -
          ΔΕΝ ταιριάζει στο chat/curtain context του Jarvis, χρειάζεται
          δικό του mechanism).
   4. **Καταχώρηση**: ο χειριστής πατάει το κουμπί όταν όλα έτοιμα/
      αντιστοιχισμένα.
   5. **Μετά την καταχώρηση**: ανοίγει **αυτόματα** (μάλλον υποχρεωτικά) το
      νέο παραστατικό στην οθόνη του Soft1 για επιβεβαίωση - reuse ΤΟΥ ΗΔΗ
      ΥΠΑΡΧΟΝΤΟΣ `open_document` tool (mode=locate, βλ. `JarvisTools.cs`)
      πάνω στο νέο FINDOC id.

   **Τι reuse-άρεται ΑΥΤΟΥΣΙΟ από S1DocReader** (self-contained, δεν
   εξαρτώνται από ήδη-ανοιχτό XModule): `Soft1Bridge.FindTraderByAfm`,
   `GetCompanyAfm`, `FindItemBySupplierCode`, `SaveSupplierCodeMapping`,
   `GetAfmDataFromAade`, `CreateTrader`, `GetParamValue` - και τα μοντέλα
   δεδομένων (`Models/ExtractionModels.cs`) + το extraction API
   (`Core/IDocumentAgentClient`/`PromptBuilder.cs`).

   **Τι ΑΛΛΑΖΕΙ / χρειάζεται νέο κώδικα** (ΔΕΝ κάνει απευθείας reuse):
   - `Soft1Bridge.CreateDocument` δουλεύει πάνω σε **ήδη ανοιχτό, ήδη σε
     insert-mode** `XModule` (έρχεται από το `[WorksOn("PURDOC"/"SALDOC")]`
     hook του S1DocReader, όπου ο χειριστής έχει ήδη πατήσει "Νέο" μέσα σε
     ανοιχτό παραστατικό πριν πατήσει το κουμπί DocReader). Ο Jarvis
     **ΔΕΝ** έχει τέτοιο ανοιχτό context (standalone panel) - πρέπει ο
     ΙΔΙΟΣ να κάνει πρώτα `XSupport.CreateModule("PURDOC"/"SALDOC")` +
     `InsertData()` (ίδιο pattern με το forum-sourced μετασχηματισμό
     snippet, βλ. memory `soft1-document-conversion-internals`), ΜΕΤΑ να
     γεμίσει FINDOC/MTRLINES όπως ήδη κάνει το `CreateDocument`.
   - `Soft1Bridge.PromptItemLookup` ανοίγει native WinForms popup - χρειάζεται
     ΝΕΟ, chat/curtain-native mechanism αντί γι' αυτό (βλ. review οθόνη
     #3β πιο πάνω).
   - Νέα κουρτίνα στο `index.html` (multi-file upload, per-file status
     list, review UI) - ΔΕΝ υπάρχει ακόμα κανένα από αυτά.
   - Ξεχωριστό Nexus entitlement wiring (`DOCREADER` toolName) στο
     `JarvisShell.xaml.cs`/`Access/`.

   **Στάδια υλοποίησης (προτεινόμενη σειρά)**:
   1. ✅ **Ολοκληρώθηκε 15/08** - "DR" trigger (client-side command
      recognition) + νέα κενή κουρτίνα + Nexus entitlement check
      (`JARVISDOCREADER`).
   2. ✅ **Ολοκληρώθηκε 16/08** - Multi-file upload UI μέσα στην κουρτίνα
      (dropzone με drag&drop + κλασικό file picker, `.pdf/.xlsx/.xls/
      .doc/.docx/.png/.jpg/.jpeg`, πολλαπλή επιλογή, όριο 20MB/αρχείο) +
      per-file status list (`drFiles` array στο `index.html` - κάθε entry
      `{id, file, name, size, status: pending|processing|done|error,
      statusText}`, badges + κουμπί αφαίρεσης όσο είναι `pending`/`error`).
      Το κουμπί "Επεξεργασία" υπάρχει ήδη στο UI αλλά είναι ΑΚΟΜΑ
      no-op (μόνο ενημερωτικό μήνυμα) - η πραγματική επεξεργασία
      ανά αρχείο μπαίνει στο Στάδιο 3, πάνω στο ΙΔΙΟ `drFiles` array
      (state machine `pending → processing → done/error` ήδη έτοιμη στο UI).
   3. 🔶 **Στάδιο 3α ολοκληρώθηκε 16/08 (ρητά περιορισμένο σκοπείο, απόφαση
      χρήστη - "μέχρι το άνοιγμα του συναλλασσόμενου, μετά βήμα-βήμα")**:
      - `JarvisAgentClient.DetectDocumentIssuerAsync` - one-shot vision call
        (Haiku, ΕΚΤΟΣ του κύριου multi-turn tool-loop, ΙΔΙΟ proxy/
        agentAccountRef με τον Jarvis) εξάγει ΑΦΜ/επωνυμία/τύπο/αριθμό
        εκδότη. Ίδιο prompt/JSON σχήμα με S1DocReader's
        `ProxyAgentClient.DetectAfmAsync` (proven). Μόνο PDF/εικόνα (ίδιος
        περιορισμός με το κύριο chat attachment - το Anthropic API δεν
        δέχεται raw Excel/Word) - Excel/Word αρχεία στην ουρά αποτυγχάνουν
        νωρίς με σαφές μήνυμα.
      - `JarvisTools.ExecuteFindTraderByAfm` - αναζήτηση `TRDR` με το ΑΦΜ
        **ΧΩΡΙΣ φίλτρο SODTYPE** (επιβεβαιωμένο ζωντανά 16/08: ο εκδότης
        μπορεί να είναι πελάτης/προμηθευτής/**χρεώστης/πιστωτής** - όλοι
        ζουν στο ΙΔΙΟ `TRDR`, διαφοροποιούνται μόνο από `SODTYPE`).
        `TraderObjectsBySodType`: `{12:SUPPLIER, 13:CUSTOMER, 15:DEBTOR,
        16:CREDITOR}` - ΜΟΝΟ αυτά τα 4 επιβεβαιωμένα, οτιδήποτε άλλο
        SODTYPE αναφέρεται ως "άγνωστος τύπος", ΚΑΝΕΝΑ άνοιγμα (ΔΕΝ
        μαντεύουμε object names).
      - `JarvisTools.ExecuteOpenTrader` + `"trader:"`-equivalent κουμπί
        "Άνοιγμα" στη λίστα αρχείων (`OBJECTNAME[AUTOLOCATE=trdrId]`,
        ΙΔΙΟ Dispatcher.BeginInvoke reentrancy fix με το `open_document`).
      - UI: το "Επεξεργασία" κουμπί (Στάδιο 2) είναι πλέον ενεργό - loop
        sequential ανά `pending` αρχείο, νέο status `identified` (μπλε
        badge) + `detail` γραμμή περιγραφής κάτω από το μέγεθος αρχείου.
      - 🔶 **Στάδιο 3β ολοκληρώθηκε 16/08** - `JarvisTools.
        ExecuteFindTraderSeriesHistory`: `SELECT SERIES,FINCODE,SOSOURCE,
        TRNDATE,PRJC,INST,TRDBRANCH FROM FINDOC WHERE TRDR=trdrId AND
        ISCANCEL=0` (join πάνω σε trdrId - ΗΔΗ γνωστό από το βήμα 3α,
        ισοδύναμο με το AFM join που έδωσε ο χρήστης), ομαδοποίηση ανά
        `(SERIES,SOSOURCE)`, best match = η ομάδα της οποίας το FINCODE
        μοιράζεται πρόθεμα με το `docType` που αναγνώρισε το AI (ρητή
        τεχνική δοσμένη από τον χρήστη ζωντανά), fallback στην πιο
        πρόσφατη σειρά. `PRJC`/`INST`/`TRDBRANCH` από την best-match
        εγγραφή εμφανίζονται σαν πληροφοριακό carry-over hint στο detail
        κάθε αρχείου - **ΚΑΘΑΡΑ ΠΛΗΡΟΦΟΡΙΑΚΟ ακόμα**, καμία καταχώρηση
        δεν γίνεται σε αυτό το στάδιο, η πραγματική ερώτηση "θες να
        περάσουν;" στον χειριστή μπαίνει στο στάδιο καταχώρησης
        (παρακάτω).
      - 🔶 **Στάδιο 3γ ολοκληρώθηκε 16/08 - ΑΑΔΕ auto-create**. Reuse
        ΑΥΤΟΥΣΙΟ pattern από `S1DocReader.Soft1.Soft1Bridge`
        (`GetAfmDataFromAade`/`CreateTrader`) - ΜΟΝΟ SODTYPE=12/SUPPLIER
        (το DR διαβάζει έγγραφα ΠΟΥ ΕΛΑΒΕ ο χειριστής, ο εκδότης είναι
        λογικά πάντα προμηθευτής σε αυτό το context, ίδιο σκεπτικό με το
        S1DocReader). Κουμπί **"Δημιουργία νέου Προμηθευτή"** εμφανίζεται
        στη γραμμή αρχείου ΜΟΝΟ όταν ο συναλλασσόμενος ΔΕΝ βρέθηκε -
        ανοίγει inline panel: ΑΑΔΕ στοιχεία (επωνυμία/διεύθυνση/ΔΟΥ) +
        **προγεμισμένο, επεξεργάσιμο** πεδίο CODE.
        - `JarvisTools.SuggestNextTraderCode` - CODE πρόταση με ΕΠΙΣΗΜΗ
          τεχνική από το BlackBook (X.SQL reference, Example 1 -
          επιβεβαιωμένο ζωντανά από τον χρήστη 16/08):
          `SELECT ISNULL((SELECT MAX(ISNULL(TRY_PARSE(CODE AS INT),0))
          FROM TRDR WHERE ...),0) + 1` - το `TRY_PARSE` αγνοεί με
          ασφάλεια μη-αριθμητικούς κωδικούς (λύνει το πρόβλημα "9">"10"
          αλφαβητικά ενός varchar CODE). Δεύτερο πέρασμα ελέγχει
          zero-padding format από δείγμα των 50 πιο πρόσφατων ίδιου
          SODTYPE - αν όλοι έχουν το ΙΔΙΟ μήκος, η πρόταση γίνεται pad σε
          αυτό.
        - Αν ο χειριστής αλλάξει το CODE, **duplicate-check πριν το
          insert** (`SELECT COUNT(*) FROM TRDR WHERE SODTYPE=12 AND
          CODE=...`) - ρητή οδηγία χρήστη ("να μη φας τα μούτρα σου
          τσάμπα", βλ. memory `s1jarvis-dr-trader-autocreate-code-rule`).
        - Επιτυχής δημιουργία -> το αρχείο περνάει στο ΙΔΙΟ "βρέθηκε"
          σχήμα με τον found-path (ενεργοποιείται "Άνοιγμα") **ΚΑΙ**
          εμφανίζεται κουμπί **"Ολοκληρώθηκε ✓"** (ρητό αίτημα χρήστη
          16/08: ο χειριστής πρέπει να ανοίξει/ελέγξει/συμπληρώσει τα
          στοιχεία του νέου συναλλασσόμενου στο Soft1 ΚΑΙ να το
          επιβεβαιώσει ρητά - `entry.awaitingConfirm` "παγώνει" το αρχείο
          μέχρι το κλικ, τίποτα δεν προχωράει μόνο του). Το πραγματικό
          "συνέχισε στο επόμενο βήμα" μπαίνει στο Στάδιο 4/5 - το κλικ
          προς το παρόν απλά καθαρίζει το flag.
      - **ΥΠΟΛΕΙΠΕΤΑΙ** (ανήκει πλέον νοητά στο #21/#22, ΟΧΙ στην
        ταυτοποίηση εκδότη): επιλογή Αγορά/Δαπάνη - αυτό ταξινομεί ΤΟ
        ΠΑΡΑΣΤΑΤΙΚΟ (SOSOURCE/είδη-vs-υπηρεσίες-vs-λίστα δαπανών), όχι
        τον συναλλασσόμενο - ανήκει στην εξαγωγή γραμμών/καταχώρηση,
        εκκρεμούν διευκρινίσεις (λίστα δαπανών μηχανισμός, SOSOURCE
        Είδη/Υπηρεσία).
      - 🔶 **duplicate-check, ΝΕΟ 16/08 (μετακινήθηκε ΕΔΩ ρητά από τον
        χρήστη - "μετά την ταυτοποίηση συναλλασσόμενου", αρχικά είχε
        μπει μετά την εξαγωγή γραμμών)**: `JarvisTools.
        ExecuteCheckDuplicateDocument` - τρέχει ΜΟΝΟ όταν `trader.found`
        (ΝΕΟΣ συναλλασσόμενος -> λογικά αδύνατο να υπάρχει ήδη το
        παραστατικό, παραλείπεται εντελώς - γλυτώνει και το ακριβό Opus
        full-extraction call του Σταδίου 4 αν αποδειχτεί διπλότυπο πριν
        καν φτάσουμε εκεί). Ψάχνει ΔΥΟ πηγές (`FINDOC` απευθείας +
        `TRDTRN` - επιβεβαιωμένο στο schema: ΤΟ `TRDTRN` έχει ΚΑΙ ΑΥΤΟ
        στήλη `FINDOC` ίδιο όνομα, FK πίσω στο ίδιο `FINDOC.FINDOC` - ΔΕΝ
        έχει `INSDATE`/`REMARKS` εκεί, μόνο `TRNDATE`/`COMMENTS`/
        `FINCODE`/`TRDR`/`SOSOURCE`). Ταύτιση `FINCODE` (LIKE, περιέχει
        τον αριθμό) **ΣΕ ΣΥΝΔΥΑΣΜΟ ΜΕ ΤΗΝ ΗΜΕΡΟΜΗΝΙΑ** (μόνο ημερομηνία,
        ΟΧΙ ώρα) - ρητή, αυστηρή οδηγία χρήστη - η σύγκριση ημερομηνίας
        γίνεται σε C# (`ParseFlexibleDate`, ανεκτικό σε μορφές) μιας και
        δεν είναι γνωστή εκ των προτέρων η ακριβής μορφή που θα γράψει
        το AI. Χρειάστηκε να προστεθεί `doc_date` στο lightweight prompt
        του Σταδίου 3α (`DetectDocumentIssuerAsync` - πριν ζητούσε μόνο
        ΑΦΜ/επωνυμία/τύπο/αριθμό, ΤΩΡΑ και ημερομηνία). Βρέθηκε -> κόκκινο
        banner (`.dr-dup-warning`) ΑΜΕΣΩΣ μετά την ταυτοποίηση, ΠΡΙΝ καν
        την εξαγωγή γραμμών, με κουμπί "Άνοιγμα υπάρχοντος" (reuse ΤΟΥ
        ΗΔΗ υπάρχοντος `open_document`) - ΠΛΗΡΟΦΟΡΙΑΚΟ/προειδοποιητικό,
        ΔΕΝ μπλοκάρει τη συνέχεια (ο χειριστής αποφασίζει).
   4. 🔶 **Ολοκληρώθηκε 16/08, στο ρητά συμφωνημένο σκοπείο** ("μόνο
      εξαγωγή/εμφάνιση τώρα") - Βήμα 2-3 (εξαγωγή γραμμών + myDATA
      cross-check + review οθόνη):
      - `JarvisAgentClient.ExtractDocumentLinesAsync` - ΔΕΥΤΕΡΗ, πιο βαθιά
        AI κλήση (Opus, ΟΧΙ Haiku - χρειάζεται ακρίβεια σε ποσά/ΦΠΑ) ΜΕΤΑ
        την ταυτοποίηση εκδότη (Στάδιο 3α) - πλήρες "generic prompt" JSON
        σχήμα (issuer/document_info/line_items/totals/aade_link),
        proven από S1DocReader's `PromptBuilder`. ΧΩΡΙΣ το "learned
        profile"/targeted κομμάτι (Nexus label-learning) - συζητήθηκε
        ρητά 16/08 (βλ. session notes: cross-tenant, ΚΟΙΝΗ βάση labels σε
        όλους τους πελάτες S1DocReader, `/profiles/*` στο Nexus, ήδη
        υπάρχον) - αποφασίστηκε να μπει σαν βελτιστοποίηση ΜΕΤΑ που θα
        δουλεύει το generic baseline, ΟΧΙ τώρα.
      - myDATA "cross-check" (ρητή διευκρίνιση χρήστη 16/08): **ΔΕΝ**
        είναι live API call - το link πάνω στο παραστατικό είναι ΗΔΗ
        ανοιχτό, χωρίς key (δείχνει το πλήρες παραστατικό στη μορφή
        ΑΑΔΕ). Το AI απλά διαβάζει/αντιγράφει το link αν υπάρχει - κουμπί
        "Άνοιγμα myDATA link" ανοίγει στον ΕΞΩΤΕΡΙΚΟ browser
        (`OpenExternalUrl`, ΝΕΟ - `Process.Start` με ρητό http/https-only
        έλεγχο πριν, μιας και το link προέρχεται από AI εξαγωγή).
      - `JarvisTools.ExecuteMatchExtractedItems` - αντιστοίχιση κάθε
        γραμμής με `MTRSUPCODE` (κωδικός ΕΚΔΟΤΗ -> δικό μας `MTRL`), JOIN
        `MTRUNIT` για το SHORTCUT (`MTRL.MTRUNIT1` είναι FK, ΟΧΙ κείμενο -
        επιβεβαιωμένο στο schema). Review εμφανίζει ✓/⚠ ανά γραμμή.
      - Κουμπί "Εξαγωγή γραμμών" ενεργό μόλις υπάρχει `trdrId` (βρέθηκε Ή
        μόλις δημιουργήθηκε) - ΔΕΝ περιμένει το "Ολοκληρώθηκε" (η
        ανάγνωση του εγγράφου είναι ανεξάρτητη από επιβεβαίωση
        συναλλασσόμενου).
      - **ΥΠΟΛΕΙΠΕΤΑΙ** (ρητά εκτός σκοπείου τώρα): UI αναζήτησης/
        αντιστοίχισης για unmatched γραμμές (το είδος υπάρχει αλλά δεν
        έχει ακόμα `MTRSUPCODE` mapping) - "έρχεται σε επόμενο βήμα" ήδη
        στο ίδιο το review κείμενο.
      - 🔶 **UI enhancement, ΝΕΟ 16/08 (ρητό αίτημα χρήστη μετά την πρώτη
        ζωντανή δοκιμή)**: split-pane preview στην κουρτίνα DR - αριστερά
        (`#drPreviewPane`) προεπισκόπηση του ΦΥΣΙΚΟΥ αρχείου (`<img>` για
        εικόνες, `<iframe>` για PDF - το built-in Chromium PDF viewer του
        WebView2 το ρεντεράρει αυτόματα, καμία βιβλιοθήκη), δεξιά
        (`#drWorkflowPane`) η ήδη υπάρχουσα ροή, αναλλοίωτη. Σέρσιμο
        splitter (`#drSplitter`, mousedown/mousemove/mouseup στο
        `document`) - αρχικό 40/60, όρια 20%-70%. Αυτόματο show/hide
        (ΟΧΙ κλικ σε γραμμή, ρητή επιλογή χρήστη) - `setDrActiveFile(entry)`
        καλείται ΜΟΝΟ στα δύο σημεία που πραγματικά "διαβάζεται" ένα
        έγγραφο (sequential identify loop + "Εξαγωγή γραμμών" κλικ),
        δείχνει πάντα το αρχείο ΥΠΟ ΤΡΕΧΟΥΣΑ επεξεργασία. `URL.
        createObjectURL` (ΟΧΙ FileReader/base64 ξανά - πιο αποδοτικό),
        `revokeObjectURL` σε `removeDrFile`/`clearDrFiles` για να μην
        τρέχει μνήμη. Standalone (CREATEAADEAFM) entries ΔΕΝ έχουν
        `entry.file` -> ποτέ δεν ενεργοποιούν το preview (λογικό, δεν
        υπάρχει έγγραφο για μια χειροκίνητη αναζήτηση ΑΦΜ).
   5. 🔶 **Στάδιο 5 (#22) ολοκληρώθηκε 16/08** - Βήμα 4-5 (καταχώρηση
      παραστατικού + αυτόματο άνοιγμα):
      - `JarvisTools.ExecuteRegisterDrDocument` - το line table ανά sosource
        είναι **100% επιβεβαιωμένο ζωντανά** μέσω των Soft1 Web Services
        (`getObjectTables`, `https://www.softone.gr/ws/`, κλήθηκε ζωντανά
        16/08 πάνω στην πραγματική βάση της Jetoil) - `SALDOC`(1351)/
        `PURDOC`(1251) -> `ITELINES` (virtual view πάνω σε physical
        `MTRLINES` - επιβεβαιώθηκε ότι `dbname=MTRLINES` και για τα δύο)·
        `LINCUSDOC`(1353)/`LINSUPDOC`(1253) -> **`LINLINES` ΜΟΝΟ** (ΟΧΙ
        `SRVLINES` - αρχική υπόθεση διορθώθηκε ρητά από το ζωντανό API
        response)· `ITEITEDOC`(5151) -> `ITELINES` + `HEADERLINE`
        (παραγόμενο είδος, εκτός σκοπείου εδώ). Πλήρες reference σε
        `S1_HeaderLines_Mapping.xlsx` (confidence-tiered, χτίστηκε στην
        ίδια συνεδρία μέσω screenshots από το Designer + BlackBook Web
        Services παράδειγμα + ζωντανό `GetXStrings('SOSOURCE')`/
        `('SODTYPE')` batch job + τελικά `getObjectTables`).
      - **Write mechanism**: `XModule.GetTable(...).Add()` + `Current[...]=
        value` ανά πεδίο + `Current.Post()` ΑΝΑ ΓΡΑΜΜΗ μέσα σε loop, τελικό
        `module.PostData()` μία φορά - **proven ζωντανό precedent από το
        ΙΔΙΟ S1DocReader** (χρήστης 16/08, `CreateDocument` method,
        αντιγράφηκε αυτούσιο το idiom). ΔΕΝ χρησιμοποιείται το SBSL
        `COPY`/`PASTE`/`DBLOCATE` μηχανισμό (αν και υπάρχει ζωντανά στο
        .NET SDK - επιβεβαιώθηκε via reflection πάνω στο πραγματικό
        `Softone.Lib.dll`: `XModule.Copy()`/`Paste()`/`LocateData()`/
        `InsertData(bool)` όλα υπάρχουν) - ΔΕΝ βρέθηκε κανένα precedent
        για το πώς επεξεργάζεσαι τις ΗΔΗ αντιγραμμένες γραμμές μετά το
        Paste (το `XTable`.NET ΔΕΝ έχει `First`/`Next`/`EOF` σε αντίθεση
        με το SBSL idiom) - ΕΠΙΤΗΔΕΣ δεν μαντεύτηκε.
      - **Strategy A** (template duplication): ψάχνει προηγούμενη εγγραφή
        ίδιου trader+series+sosource, σκοράρει candidates με **matching
        coefficient** (ρητό αίτημα χρήστη 16/08 - "μήπως να φτιάξουμε
        συντελεστή ταιριάσματος;"): `0.5×format-match` (σκελετός κωδικού
        παραστατικού με ψηφία->`#`) `+ 0.35×item-overlap` (Jaccard, matched
        `MTRL` της τρέχουσας εξαγωγής έναντι των γραμμών του candidate)
        `+ 0.15×recency` (φθίνουσα συνάρτηση μηνών) - threshold `0.3`,
        **ΟΧΙ πια "πάρε το πιο πρόσφατο"** (ρητή διόρθωση χρήστη). Αν το
        template έχει ΑΚΡΙΒΩΣ 1 γραμμή, αθροίζει **και αξία και ποσότητα**
        (ρητό 16/08) από όλες τις matched PDF-γραμμές σε αυτή τη μία -
        λύνει ακριβώς το αρχικό παράδειγμα χρήστη (πολλές γραμμές PDF ->
        μία συγκεντρωτική γραμμή Soft1). Αν το template έχει >1 γραμμές,
        **fallback σε Strategy B** (ασφαλέστερο παρά να μαντέψουμε
        proportional κατανομή που δεν ζητήθηκε ρητά).
      - **Strategy B** (per-item ιστορικό): για κάθε matched γραμμή (μέσω
        `MTRSUPCODE`, ήδη από το Στάδιο 4), ψάχνει το πιο αξιόπιστο
        ιστορικό προφίλ ανάμεσα σε προηγούμενες γραμμές **ίδιου trader +
        ίδιου `MTRL`**, σκοραρισμένο με format-match του γονικού `FINCODE`
        (ρητή διευκρίνιση χρήστη 16/08 - "ίδιος trader με τον ίδιο κωδικό
        παραστατικού ως format", ΟΧΙ απλά "πιο πρόσφατο ανεξαρτήτως
        trader").
      - **`CarryOverFieldsByPhysicalTable`** (ρητό αίτημα χρήστη 16/08):
        αντί για hardcoded C# ιδιότητες (`Inst`/`Prjc`/`Cntr`/`BusUnits`
        σκόρπια σε if-statements), τα "extra" πεδία που κουβαλάμε από το
        ιστορικό είναι **ένα comma-delimited string ανά physical πίνακα**
        (`["MTRLINES"] = "INST,PRJC,CNTR,BUSUNITS"`) - επέκταση σε νέο
        πεδίο = μία προσθήκη στο CSV string, καμία άλλη αλλαγή κώδικα.
        Static v1 (ΟΧΙ ακόμα live schema-discovery/`SELECT *`+diff-από-
        default) - συζητήθηκε ρητά ότι το πλήρες δυναμικό θα χρειαστεί το
        `getObjectTables`/`getObjects` schema-discovery mechanism (#27) αν
        χρειαστεί ποτέ πραγματική multi-tenant genericity.
      - Unmatched γραμμές (χωρίς `MTRSUPCODE`) **ΔΕΝ μπλοκάρουν** την
        καταχώρηση - επιστρέφονται σε `pendingLines`, το UI δείχνει
        προειδοποίηση (δημιουργία νέου είδους = #23, deferred).
      - **Review/commit gate στο UI** (`renderDrRegisterPanel`, ίδιο idiom
        με το "Ολοκληρώθηκε ✓" gate του trader auto-create) - δείχνει τη
        σειρά-στόχο (auto από `seriesHistory.bestGuess` αν υπάρχει, αλλιώς
        `<select>` από τα ίδια `candidates`) και το κουμπί **"Καταχώρηση
        ✓"** - ΚΑΝΕΝΑ `PostData()` δεν τρέχει χωρίς ρητό click.
      - **Αυτόματο άνοιγμα** μετά από επιτυχή καταχώρηση - server-side
        (`JarvisShell.HandleDrRegisterDocumentAsync`), reuse του ήδη
        υπάρχοντος `OpenDocument`/`ExecuteOpenDocument` (mode=locate),
        ΧΩΡΙΣ αντιγραφή λογικής.
   6. (Deferred, ξεχωριστό) δημιουργία νέου είδους όταν δεν υπάρχει καθόλου.
   7. **(Deferred, ρητό αίτημα χρήστη 19/08)** — email → αυτόματος
      εντοπισμός τιμολογίων ΑΓΟΡΑΣ → αυτόματη εισαγωγή μέσω DR. Ο
      Jarvis να διαβάζει τα email του χειριστή, να ξεχωρίζει ΠΟΙΑ από
      αυτά είναι τιμολόγια αγοράς (πιθανό συνημμένο PDF/εικόνα
      παραστατικού από προμηθευτή) και να τα περνάει στο ήδη υπάρχον
      DR flow (§6 πιο πάνω) για αυτόματη εξαγωγή/καταχώρηση - ΧΩΡΙΣ ο
      χειριστής να χρειάζεται να τα κατεβάσει/ανεβάσει χειροκίνητα.
      **Ρητά sequenced**: γίνεται ΜΟΝΟ αφού η αναγνώριση παραστατικών
      του DR είναι αξιόπιστη πρώτα (§6 σε παραγωγή, ΟΧΙ ακόμα - βλ.
      "Κατάσταση" πιο πάνω). ⚠️ **Σημαντικό, μέχρι τότε**: το
      `read_email` tool ΗΔΗ υπάρχει και δουλεύει (§Email agent/curtain
      πιο πάνω) - ο Jarvis ΔΕΝ πρέπει ΠΟΤΕ να απαντάει σαν να είναι
      θεμελιωδώς αδύνατο να διαβάσει email/να πάρει πληροφορίες από
      εκεί (ρητή διόρθωση χρήστη 19/08, μετά από ζωντανό bug όπου ο
      Jarvis έδωσε τέτοια απάντηση λόγω routing gap - βλ. session
      notes, ΗΔΗ διορθωμένο `emailReadHit`).

### Παράμετροι (`cccParams`) — καταγεγραμμένες μέχρι στιγμής

Συγκεντρωτικά, ώστε να μη χαθεί ο συσχετισμός καθώς μεγαλώνει η λίστα.
Κανένας πίνακας/παράμετρος ΔΕΝ έχει δημιουργηθεί ακόμα στη βάση — μόνο
σχεδιασμός.

| ParamCode | Όνομα | Σκοπός | Idea |
|---|---|---|---|
| 500000 | (debug flag) | Ενεργοποιεί DebugLog file logging - ΗΔΗ υπάρχον, χρησιμοποιείται από S1Courier/S1DocReader/S1Jarvis | - |
| 50003 | Σειρά Καταχώρησης Παραγγελίας AI | Σειρά (`SERIES`) στην οποία καταχωρείται παραγγελία που ήρθε από email | #1 |
| 50004 | Σειρά Λιανικής AI eShop | Σειρά λιανικής πώλησης για eshop παραγγελίες | #2 |
| 50005 | (σειρά τιμολόγησης AI eShop) | Σειρά όταν ο πελάτης eshop ζήτησε έκδοση τιμολογίου | #2 |
| 50006 | Αποστολή ενημέρωσης παραγγελίας email AI αυτόματα | On/off για αυτόματο email ενημέρωσης πελάτη (email-orders flow) | #1 |
| 50007 | Αποστολή ενημέρωσης eshop AI αυτόματα | On/off για αυτόματο email ενημέρωσης πελάτη (eshop flow) | #2 |
| 500008 | s1Jarvice - Knowledge Base Series | ✅ **Ήδη υπάρχει, επιβεβαιώθηκε 15/08 από screenshot Soft1** - `ParamValue=30000` = `SERIES` για τις `SOACTION` εγγραφές του Jarvis (Q&A log + `create_crm_task`). ΔΕΝ hardcoded στον κώδικα - διαβάζεται με `SELECT ParamValue FROM cccParams WHERE ParamCode=500008` | #5/Phase 2c |
| 500009 | Πλήθος Δεκαδικών σε Reports AI | 🔶 **Πρόταση, ΔΕΝ επιβεβαιωμένη ακόμα** (ίδιο μοτίβο με το αρχικό "50008" πριν επιβεβαιωθεί το 500008 - χρειάζεται να δημιουργηθεί/επιβεβαιωθεί στο Soft1). `ParamValue` = πλήθος δεκαδικών ψηφίων που πρέπει να χρησιμοποιεί ο Jarvis σε ΚΑΘΕ αριθμητική τιμή σε reports/πίνακες/κάρτες - ρητή οδηγία στο system prompt (`JarvisAgentClient.BuildSystemPrompt`). Ασφαλές default `2` αν λείπει η παράμετρος (`JarvisTools.GetReportDecimalPlaces` - ΔΕΝ σπάει το chat, μόνο DebugLog) | νέο 15/08 |
| 500011 | s1Jarvice - Μέγιστες Γραμμές σε Απευθείας Εξαγωγή Αρχείου AI | 🔶 **Πρόταση, ΔΕΝ επιβεβαιωμένη ακόμα - ο χρήστης θα τη δημιουργήσει στο Soft1 με `ParamValue=5000`** (ρητή τιμή "προς το παρόν"). `0` = χωρίς όριο (εξάγει ΟΛΑ τα αποτελέσματα). Χρησιμοποιείται από το νέο tool `export_query_to_file` (`JarvisTools.GetDirectExportMaxRows`) - ΞΕΧΩΡΙΣΤΟ όριο από το 200-row cap του `query_data`, μιας και τα δεδομένα εδώ ΔΕΝ περνάνε από το context του Claude. Ασφαλές default `5000` αν λείπει η παράμετρος | νέο 15/08 |

---

## ⚠️ Αρχιτεκτονική αλλαγή: ο Jarvis ΔΕΝ έχει δικό του Anthropic API key

Ο Jarvis μιλάει στο **Nexus** (`https://nexus-itexperts-api.azurewebsites.net`
— το ίδιο production licensing/AI-proxy backend που ήδη χρησιμοποιεί το
S1Courier και το DocReader, repo:
`C:\Users\gkirkmalis.JETOIL.000\source\repos\Nexus`), όχι απευθείας την
Anthropic:

1. **`POST /access/check`** (`Access/JarvisLicenseGuard.cs`, ίδιο μοτίβο με
   `S1Courier.Access.LicenseGuard`) — στέλνει
   `{serial, companyCode, branchCode, soft1UserId, toolName: "S1JARVIS"}`,
   παίρνει πίσω `{allowed, agentAccountRef, validUntil}`. Τρέχει στο
   `NavigationCompleted` του WebView2, πριν ενεργοποιηθεί το chat. Αν
   αρνηθεί, το composer κλειδώνει (`window.setDisabled(message)` στο
   `index.html`) — δεν εμφανίζεται καθόλου popup.
2. **`POST /agent/vision`** (Phase 2b, επόμενο) — ο client στέλνει το έτοιμο
   Anthropic request JSON (μηνύματα + tool definitions) + το
   `agentAccountRef` που πήραμε στο βήμα 1· ο Nexus βάζει το πραγματικό,
   encrypted-at-rest key server-side, προωθεί στην Anthropic, γυρνάει το ωμό
   response (`rawResponseJson`) — ο Jarvis διαβάζει `tool_use` blocks από
   εκεί και τρέχει το δικό του loop (Claude → tool_use → εκτέλεση πάνω σε
   `XSupport` → tool_result → ξανά Claude → ...).

Entitlement ήδη δημιουργημένο στο Nexus admin UI: customer "Cetracore
Jetoil", tool `S1JARVIS`, company/branch `1000`/`1000`, user `1`, agent
`acc_agentfactory`, ισχύς έως `31/08/2027`.

## Αρχιτεκτονική (ό,τι πραγματικά δουλεύει τώρα)

```
Menu Job (type "Dll Form", Action/File = ".S1Jarvis.dll;JarvisHostForm")
  → JarvisHostForm (WinForms Form)
      → ElementHost (System.Windows.Forms.Integration)
          → JarvisShell (WPF UserControl)
              → WebView2 → UI/web/index.html (orb avatar + chat UI)
```

- **`SoftoneIntegration/S1Init.cs`** — entry point, γεμίζει το static
  `JarvisCore.XSupport` στο login (`TXCode.Initialize()`).
- **`UI/JarvisHostForm.cs`** — WinForms `Form`, public χωρίς-παραμέτρους
  constructor (το "Dll Form" loader το φτιάχνει με reflection). Φιλοξενεί το
  `JarvisShell` μέσω `ElementHost`.
- **`UI/JarvisShell.xaml(.cs)`** — WPF UserControl: αρχικοποιεί το WebView2
  (με ρητό `userDataFolder` στο `%LOCALAPPDATA%` — **όχι** το default, που
  ψάχνει να γράψει δίπλα στο `Xplorer.exe` μέσα στο Program Files και σκάει
  χωρίς admin), υπολογίζει το personalized greeting, το κάνει inject στη
  σελίδα μετά το `NavigationCompleted`, και διαβάζει μηνύματα του χρήστη μέσω
  `WebMessageReceived`.
- **`UI/web/index.html`** — το πραγματικό UI: animated orb (CSS `@property`
  για smooth random hue transitions — δουλεύει επειδή το WebView2 είναι
  Chromium, δεν χρειάζεται cross-browser fallback), chat composer στυλ Claude,
  transcript που ανοίγει με μετάβαση της σφαίρας σε μικρό avatar πάνω-δεξιά
  μόλις ξεκινήσει η συζήτηση (`app.classList.add('active')`).
  Copy-άρεται (`Link`) σε `web\index.html` δίπλα στο DLL στο build.
- **`SoftoneIntegration/JarvisObject.cs`, `UI/JarvisWindow.xaml(.cs)`** —
  **αχρησιμοποίητα**, μένουν σαν τεκμηρίωση αποτυχημένων προσπαθειών (βλ.
  "Ιστορικό" παρακάτω). Μην τα αγγίξεις/σβήσεις χωρίς λόγο, εξηγούν γιατί το
  project είναι όπως είναι.

## Επιβεβαιωμένα Soft1 SDK facts (μη τα ξαναψάχνεις)

Βγήκαν είτε από πραγματικό `ildasm` πάνω στο `Softone.Lib.dll`, είτε από τον
χρήστη με ζωντανό test, είτε από το BlackBook — όχι εικασίες:

- `XSupport.ConnectionInfo.CompanyId` / `.BranchId` / `.UserId` (int) — **όχι**
  `.User`. Επίσης υπάρχουν `.UserName`, `.ComputerName`, `.LoginTime`,
  `.LoginDate`, `.IsAdministrator`, `.YearId`, `.PeriodId`.
- `XSupport.ConnectionInfo.SerialNum` — **serial number της εγκατάστασης
  Soft1** (ξεχωριστό από `CompanyId`) - ήδη χρησιμοποιείται σαν κλειδί
  στο υπάρχον licensing (`JarvisLicenseGuard`/`AccessCheckRequest.Serial`,
  μαζί με `CompanyCode`/`BranchCode`/`Soft1UserId`, καλεί
  `POST {ServiceUrl}/access/check` στο Nexus). Επιβεβαιωμένο 15/08 -
  ίδιο ζευγάρι (SerialNum, CompanyId) θα δέσει και το knowledge base
  (βλ. "Knowledge base - multi-tenant binding" παρακάτω) - reuse του
  ήδη υπάρχοντος μηχανισμού, όχι κάτι καινούργιο.
- Προσωπικό όνομα χρήστη: `PRSN.NAME` μέσω `PRSN.USERS = ConnectionInfo.UserId`
  (αν υπάρχει σύνδεση person↔user)· fallback `USERS.NAME` μέσω
  `USERS.USERS = ConnectionInfo.UserId` (π.χ. για generic λογαριασμούς χωρίς
  PRSN, τύπου "Administrator"). Δες `JarvisShell.xaml.cs::GetDisplayName()`.
- CRM tasks = πίνακες `SOACTION`/`ACTLINES` (χρησιμοποιούνται ήδη στο
  S1Courier, `SoftoneIntergration/SALDOC.cs`, class `SOTASK`).
- Πλήρες schema (όλοι οι πίνακες/στήλες της jetoilDemo) στο
  `C:\Claude\Skills\S1Jetoil_TableInfo.csv` — grep πάνω του πριν γράψεις νέο
  SQL, μη μαντεύεις ονόματα στηλών.
- `SOACTION.DURATION` (native, `datetime`) — **ΟΧΙ** ημερομηνία, raw
  duration/TimeSpan encoding (μέτρημα από το SQL Server datetime epoch
  `1900-01-01 00:00:00`, π.χ. η τιμή `1,06:00:00` = 1 ημέρα + 6 ώρες = 30
  ώρες συνολικά). Read: `DATEDIFF(MINUTE, 0, SOACTION.DURATION)`. Κανείς
  δεν πρέπει να γράφει/διαβάζει αυτό απευθείας ως λεπτά.
- `SOACTION.cccMidDur` (custom field, Designer, `int`, UI caption
  **"Διάρκεια Σε Λεπτά"**) — ΝΕΟ 17/08, ρητό αίτημα χρήστη: απλός ακέραιος
  αριθμός λεπτών, **ΞΕΧΩΡΙΣΤΟ** από το native `DURATION` πιο πάνω. ΚΟΙΝΟ
  πεδίο ανάμεσα σε manual καταχώρηση (native φόρμα εργασίας Series 6) και
  Jarvis (`create_crm_task` tool → `durationMinutes` param →
  `CreateCrmRecordCore`, βλ. `JarvisTools.cs`) - καμία πλευρά δεν χρειάζεται
  ποτέ να αγγίξει raw datetime/TimeSpan format.
- Menu Job types (BlackBook §C.2): "Object (Edit Master)" (κανονικό
  Designer object), **"Dll Form"** (`.Mydll.dll;ClassName` — αυτό
  χρησιμοποιούμε), "Report", "Web Page", κ.ά. `BROWSERONLY=1` param ανοίγει
  ένα object κατευθείαν σε browser mode (άσχετο με το CCCJARVIS μας).

## Build & deploy

- `S1Jarvis.csproj`, Debug config, χτίζει με output path που καταλήγει στο
  `C:\Program Files\Soft1Core\` (χρειάζεται admin/elevated VS για να γράψει
  εκεί, αλλιώς access-denied στο copy step).
- **NETDLL registration:** το S1Courier είναι ήδη καταχωρημένο ως NETDLL. Αν
  το Soft1 σας δέχεται πάνω από ένα `NETDLL=` entry, πρόσθεσε
  `NETDLL=S1Jarvis.dll` δίπλα. Αν όχι, θα χρειαστεί οι δύο DLLs να ενωθούν σε
  ένα assembly (δεν χάνεται κώδικας, μόνο μεταφέρονται αρχεία).
- Menu Job: type **"Dll Form"**, Action/File `.S1Jarvis.dll;JarvisHostForm`.
- WebView2 Runtime: το Soft1 ήδη χρησιμοποιεί `webview2loader.dll` εσωτερικά,
  άρα πολύ πιθανό να υπάρχει ήδη στο μηχάνημα.
- **Ένα-αρχείο deploy (ΝΕΟ 20/08, ρητό αίτημα χρήστη)**: το `web/index.html`
  και το `web/vendor/chart.umd.min.js` ΔΕΝ αντιγράφονται πια σαν loose
  αρχεία δίπλα στο DLL - είναι `EmbeddedResource` ΜΕΣΑ στο ίδιο το
  `S1Jarvis.dll` (`<LogicalName>` ρητά ορισμένο στο `.csproj`:
  `S1Jarvis.web.index.html`, `S1Jarvis.web.vendor.chart.umd.min.js`). Το
  WebView2 δεν κάνει navigate σε embedded resource απευθείας - σερβίρονται
  μέσω `WebResourceRequested` σε εικονικό domain
  (`https://s1jarvis.local/...`, δεν χρειάζεται πραγματικά να υπάρχει/να
  υπάρχει internet - πιάνεται από το filter πριν βγει δίκτυο), βλ.
  `JarvisShell.xaml.cs::ServeEmbeddedWebResource`. Deploy πλέον = **μόνο**
  `S1Jarvis.dll` (+`S1Jarvis.pdb` προαιρετικά για debugging) - όχι πια
  φάκελος `web\`.

## ✅ Phase 2b — Claude API tool-use loop (επιβεβαιωμένο ζωντανά, 14/08)

- **`Core/JarvisAgentClient.cs`** — το loop: χτίζει το Anthropic Messages API
  request (`model=claude-opus-5`, `system`, `tools`, `messages`), το στέλνει
  στο Nexus (`/agent/vision`, ίδιο pattern με `S1DocReader`'s
  `ProxyAgentClient`), διαβάζει `stop_reason`/`content` από το
  `rawResponseJson`, εκτελεί `tool_use` blocks, loop μέχρι `end_turn`
  (`MaxIterations=14`, ανέβηκε από 6→10→14 όσο φαίνονταν πραγματικά queries
  να χρειάζονται περισσότερα βήματα). Περνάει πίσω ολόκληρο το `content`
  array σε κάθε turn (thinking blocks πρέπει να ταξιδεύουν αναλλοίωτα).
  - **"ΓΝΩΣΤΟ SCHEMA"** στο system prompt (TRDR/FINDOC/SERIES/TRDBALSHEET/
    CCCLOADING+CCCLOADCOMPS) - επιβεβαιωμένα facts από ζωντανές δοκιμές,
    ώστε να μη χάνει iterations ξανα-ανακαλύπτοντάς τα με INFORMATION_SCHEMA.
  - **"ΑΠΟΦΑΣΙΣΤΙΚΟΤΗΤΑ"** οδηγία - σταμάτα να κάνεις queries μόλις έχεις
    αρκετά δεδομένα, μην "ψάχνεις για σιγουριά".
  - **Force-final-answer δικλείδα**: στο ΤΕΛΕΥΤΑΙΟ iteration αφαιρείται το
    `tools` από το request - ο Claude αναγκάζεται να απαντήσει με ό,τι έχει
    ήδη συλλέξει αντί να καταλήξει στο γενικό "έφτασα το όριο" μήνυμα.
  - **Quick-reply σύμβαση** (`❓ ερώτηση` + `> επιλογή` γραμμές) - για
    ασαφή/πολύ ευρέα ερωτήματα, ο Jarvis ρωτάει διευκρινιστικά με
    κλικαριστά κουμπιά αντί να iterate άσκοπα (index.html quick-reply-btn).
  - **Attachments** (εικόνα/PDF) - `AskAsync` overload με
    `attachmentBase64/attachmentMimeType`, χτίζει σωστό Anthropic
    vision content block. ΠΡΟΣΟΧΗ: ποτέ κενό `{"type":"text","text":""}`
    block (το API το απορρίπτει με 400) - μπαίνει ΜΟΝΟ αν υπάρχει
    πραγματικό κείμενο. "document" ΜΟΝΟ για `application/pdf` - όχι Excel.
  - **Stop button** (`CancelCurrent()`/`CancellationTokenSource`) -
    reference-checked cleanup στο `finally` (`ReferenceEquals(_cts, myCts)`)
    ώστε επικαλυπτόμενες κλήσεις (π.χ. dashboard override-refresh) να μην
    "τυφλώνουν" η μία την ακύρωση της άλλης.
- **`Core/JarvisTools.cs`** — `query_data` tool, read-only SQL. Blocklist
  επικίνδυνων λέξεων με **word-boundary regex** (`\bEXEC\b`, όχι
  `IndexOf` - bug: έπιανε "EXEC" μέσα σε "Executiondate" σαν substring).
- Μορφοποίηση απάντησης (`index.html` mini-markdown parser, ΚΑΙ για τα δύο
  roles user/assistant πλέον): Markdown tables (πίνακες + export buttons),
  κάρτες (`### heading` + `**label**: value`), `📊 τίτλος`+table → bar chart.

## ✅ Orb "Legend" + UI polish (15/08)

- **Καινούργιο C#→JS κανάλι** (`onProgress` callback στο
  `JarvisAgentClient.AskAsync`, νέο `Action<string>` optional param):
  σε ΚΑΘΕ ενδιάμεσο iteration του tool-use loop (όχι στο τελευταίο - εκεί
  έρχεται κατευθείαν η τελική απάντηση), καλείται `BuildProgressCaption`
  - παίρνει το ΠΡΑΓΜΑΤΙΚΟ `thinking` block (Claude Opus 5, thinking on by
  default) και βγάζει την πρώτη πρόταση σαν σύντομο caption, ή fallback
  σε γενικό μήνυμα ανά tool name αν δεν υπάρχει thinking block.
  `JarvisShell.xaml.cs` (`JsonThinkingUpdate`) το στέλνει σαν
  `{"type":"thinking_update","text":"..."}` μέσω
  `PostWebMessageAsString` - συνδεδεμένο στα 2 chat call sites (κανονικό
  + attachment), ΟΧΙ στο dashboard (έχει ήδη δικό του progress bar).
- **`index.html` #orbCaption**: "legend" κάτω από το orb - εμφανίζεται
  ΜΟΝΟ όταν το orb είναι στη μικρή θέση πάνω-δεξιά (`.app.active`, fixed
  `top:60px; right:20px` - ακριβώς κάτω από το 40x40 orb). Το message
  listener αναγνωρίζει το `thinking_update` type ξεχωριστά από την
  τελική απάντηση (δεν καλεί `endThinking()`, δεν προσθέτει στο
  transcript) - καθαρίζεται αυτόματα μέσα στο `endThinking()` όταν
  έρθει η τελική απάντηση.
  - **✅ Ζωντανό test 15/08**: δούλεψε ("Ψάχνει στη βάση δεδομένων…"),
    αλλά ο χρήστης ζήτησε πιο "αφηγηματικό" log αντί για μία γραμμή:
    "τι βρήκε/τι φιλτράρισε/τι ξαναέψαξε/τι ξαναφιλτράρισε". Δύο
    διορθώσεις:
    1. **Δεύτερο caption ανά iteration** (`BuildResultCaption`, νέο) -
       ΜΕΤΑ την εκτέλεση του tool, διαβάζει το `rowCount`/`truncated`
       από το JSON του `query_data` αποτελέσματος και λέει "Βρήκε N
       εγγραφές" / "Δεν βρήκε αποτελέσματα - ξαναψάχνει…" / "Σφάλμα -
       δοκιμάζει διαφορετική προσέγγιση…". Μαζί με το ήδη υπάρχον
       caption πρόθεσης (πριν το tool), κάθε iteration βγάζει ΔΥΟ
       γραμμές (πρόθεση + αποτέλεσμα).
    2. **Accumulate, όχι replace** (`index.html` `setThinkingCaption`) -
       κάθε `thinking_update` προστίθεται σαν νέα γραμμή σε ένα μικρό
       "log" (μέχρι 5 γραμμές, FIFO - παλιότερη φεύγει), αντί να
       αντικαθιστά την προηγούμενη. CSS: `max-height:96px` (~5 γραμμές
       στο 11px/1.35), `overflow-y:auto` (custom scrollbar), `white-
       space:pre-line`. Καθαρίζεται σε κάθε νέα ερώτηση
       (`beginThinking`/`endThinking` και τα δύο καλούν
       `setThinkingCaption('')` - defensive διπλό clear).
- **Δεκαδικά ψηφία σε reports** (νέο 15/08, βλ. πίνακα παραμέτρων -
  `ParamCode 500009`): ρητή system-prompt οδηγία - `reportDecimalPlaces`
  διαβάζεται ΜΙΑ φορά πριν το loop (`JarvisTools.GetReportDecimalPlaces`,
  ασφαλές default 2 αν λείπει η παράμετρος).
- **Στοίχιση στηλών πινάκων** (νέο 15/08, ρητή απόφαση χρήστη - **ΑΝΤΙΣΤΡΟΦΟ
  από τη συνηθισμένη σύμβαση**): κείμενο ΔΕΞΙΑ, αριθμοί ΑΡΙΣΤΕΡΑ. Δύο
  επίπεδα: (1) ρητή system-prompt οδηγία - ο Jarvis δηλώνει τη στοίχιση
  στη Markdown separator row με `:` (`|---:|`=δεξιά, `|:---|`=αριστερά),
  χρήσιμο ειδικά για στήλες σαν "ΑΦΜ" (μοιάζουν αριθμητικές αλλά είναι
  ταυτότητα/κείμενο). (2) `index.html` auto-detect fallback
  (`autoDetectAlign`/`resolveColumnAlign`/`isNumericCell`, threshold 60%
  ίδιο με το `upgradeTablesToCharts`) - αν ο Jarvis δεν το δηλώσει ρητά,
  το UI μαντεύει από το περιεχόμενο. Εφαρμόζεται σε `tableBlockToHtml`
  (και στον πίνακα-κάτω-από-γράφημα του `chartBlockToHtml`) - ΟΧΙ στο
  ίδιο το bar-chart grid (label/bar/value), που έχει δική του σταθερή
  διάταξη.
- **Preview + ρητή επιλογή αποθήκευσης για μεγάλα reports** (νέο 15/08 -
  αντικατέστησε παλιά hardcoded συμπεριφορά): πριν, όταν ένα αποτέλεσμα
  ήταν "πολύ μεγάλο", το system prompt έλεγε στον Jarvis σε απλά λόγια να
  δώσει **σύνοψη/ομαδοποίηση** + να προτείνει έτοιμο `SELECT` για να το
  τρέξει ο χρήστης μόνος του στο Soft1 Report/SQL - κανένας πραγματικός
  αριθμός/μηχανισμός, καθαρά κρίση του μοντέλου πάνω σε ασαφές κείμενο
  ("~50-100 γραμμές"). ✅ Νέα συμπεριφορά (ρητή απόφαση χρήστη - **ΟΧΙ
  παραμετρικό "ταβάνι"** για το preview, το 100 είναι σταθερό): πάνω
  από 100 γραμμές (`totalRowCount` από το `query_data` tool result -
  **ΔΙΟΡΘΩΘΗΚΕ** 15/08, πριν το πεδίο `rowCount` ήταν ΗΔΗ το κομμένο
  πλήθος ≤200 ακόμα κι όταν υπήρχαν περισσότερες πραγματικά) →
  **preview με τις ΠΡΩΤΕΣ 100 πραγματικές γραμμές** (όχι
  σύνοψη/αθροίσματα) + ρητή δήλωση πόσες βρέθηκαν συνολικά + ❓/>
  quick-reply ερώτηση αν θέλει να αποθηκευτούν ΟΛΑ σε αρχείο.
  - Αν "Ναι": ✅ **λύθηκε πλήρως 15/08** (ο χρήστης ρητά ζήτησε να μην
    περιοριστεί στο query_data's 200-cap) - νέο tool
    **`export_query_to_file`** (`JarvisTools.cs`): τρέχει το ΙΔΙΟ SELECT
    ΑΠΕΥΘΕΙΑΣ στη βάση και γράφει το αποτέλεσμα ΑΠΕΥΘΕΙΑΣ σε αρχείο
    (Excel/CSV, ίδιο `XlsxWriter`/`CsvWriter` με τα υπάρχοντα exports,
    ίδιο path convention `Έγγραφα\Jarvis Exports\`) - τα δεδομένα ΔΕΝ
    περνάνε ΚΑΘΟΛΟΥ από το context του Claude, άρα δεν έχει το 200-row
    όριο του `query_data`. Δικό του, ΞΕΧΩΡΙΣΤΟ παραμετρικό όριο:
    `ParamCode 500011` ("Μέγιστες Γραμμές σε Απευθείας Εξαγωγή Αρχείου
    AI") - 🔶 πρόταση, ΔΕΝ επιβεβαιωμένη ακόμα - **`0` = χωρίς όριο
    (εξάγει ΟΛΑ)**, αλλιώς μέγιστο πλήθος. Default αν λείπει η
    παράμετρος: **5000** (ρητή τιμή χρήστη "προς το παρόν"). Κόψιμο
    γίνεται ΣΕ MEMORY (C#, `Take`), ΟΧΙ με SQL `TOP` wrapper - ένα SELECT
    με δικό του `ORDER BY` δεν επιτρέπεται ως subquery χωρίς δικό του
    TOP/OFFSET, θα έσκαγε σε νόμιμα queries. SQL validation
    (`ValidateSelectOnly`) έγινε κοινό helper - το μοιράζονται
    `query_data` ΚΑΙ `export_query_to_file`, μία πηγή αλήθειας.
- **Legend + σφαίρα ΚΑΙ μέσα στο Help mode** (νέο 15/08, ζωντανό feedback
  του χρήστη): αρχικά το `onProgress` ΔΕΝ συνδεόταν στο Help mode (το
  σκεπτικό ήταν "το κεντρικό orb είναι κρυμμένο πίσω από την κουρτίνα,
  άχρηστο") - ο χρήστης το θέλει ΚΑΙ εκεί. Λύση: **δική της** μικρή
  σφαίρα + caption στην τίτλος-γραμμή του Help καμβά
  (`#helpOrbWrap`/`#helpStatusCaption`, ΝΕΑ κλάση `.help-orb-wrap` - ΟΧΙ
  `.orb-wrap`, ώστε να ΜΗΝ πιάνει τους κανόνες `.app.active .orb-wrap`
  fixed-positioning που θα συγκρουόταν με το κεντρικό orb). Reuse των
  ίδιων `.orb-glow`/`.orb-core` στοιχείων + keyframes (`spin`/
  `pulseGlow`/`pulseCore`), μικρότερο μέγεθος. Ορατή ΚΑΙ όσο η κουρτίνα
  είναι συμπτυγμένη (η τίτλος-γραμμή προεξέχει πάντα). Νέο μήνυμα type
  `help_status` (`JsonHelpStatus` στο `JarvisShell.xaml.cs`) - ΞΕΧΩΡΙΣΤΟ
  από το κεντρικό `thinking_update`, single-line replace (ΟΧΙ
  accumulate σαν το κεντρικό `#orbCaption` - πολύ λιγότερος χώρος στην
  τίτλος-γραμμή).
  - **✅ Γενικεύτηκε 15/08**: η κλάση μετονομάστηκε `.help-orb-wrap` →
    **`.mini-orb-wrap`** (ίδια αλλαγή `.help-status`→`.mini-status`,
    `.help-status-caption`→`.mini-status-caption`) - το ΙΔΙΟ στοιχείο
    προστέθηκε ΚΑΙ στο **Dashboard titlebar** (`#dashboardOrbWrap`/
    `#dashboardStatusCaption`, νέο μήνυμα type `dashboard_status`/
    `JsonDashboardStatus`, wired στο `HandleDashboardQueryAsync`'s
    `onProgress`) - πριν το Dashboard είχε μόνο το indeterminate
    `#dashboardProgress` bar, τώρα έχει ΚΑΙ το ίδιο ζωντανό feedback με
    το Help mode.
  - **✅ Κενό composer↔τίτλος-γραμμή ΚΑΙ για Help mode** (νέο 15/08): το
    `.app.dashboard-active .composer-dock{padding-bottom:62px}` υπήρχε
    ΜΟΝΟ για το Dashboard - το Help mode δεν είχε αντίστοιχο κενό,
    η συμπτυγμένη λωρίδα του ακουμπούσε το composer. Προστέθηκε
    `.app.help-active` (ίδιο 62px, ίδιο ύψος λωρίδας 46px και στα δύο).
  - **✅ Orb επιστρέφει στην αρχική θέση αν δεν στάλθηκε ποτέ μήνυμα**
    (νέο 15/08): αν ο χειριστής ανοίξει/κλείσει Dashboard ή Help mode
    ΧΩΡΙΣ να στείλει ΚΑΝΕΝΑ πραγματικό μήνυμα (ούτε στο κύριο chat, ούτε
    στο Help mode - το αυτόματο καλωσόρισμα του Help mode ΔΕΝ μετράει),
    η οθόνη γυρνάει στην αρχική/κεντρική κατάσταση (`maybeRevertToIdle()`,
    one-way latch `anyRealMessageSent` - μόλις σταλεί ΚΑΤΙ πραγματικό,
    ΔΕΝ ξαναγυρνάει ποτέ). Πριν, το `.app.active` έμενε για πάντα μόλις
    ανοιχτεί ΟΠΟΙΟΔΗΠΟΤΕ panel, ό,τι κι αν έγινε μετά.
- **Custom scrollbar** (global `*::-webkit-scrollbar` - WebView2 =
  Chromium, δουλεύει): λεπτό, ημιδιάφανο thumb, διάφανο track - όχι το
  κλασικό γκρι OS scrollbar. Καλύπτει `.transcript`/`.table-wrap`/
  `.dashboard-body` και ό,τι scrollable προστεθεί μελλοντικά (π.χ. Help
  mode καμβάς) αυτόματα, χωρίς ξεχωριστό κανόνα το καθένα.
- **`HelpCommands`** (νέα client-side εντολή, ίδιο μοτίβο exact-match με
  `Dashboard`/`Help`): σε αντίθεση με αυτές τις δύο (που ανοίγουν άλλο
  panel, ΔΕΝ αγγίζουν το transcript), το `HelpCommands` απαντάει ΜΕΣΑ στο
  chat - στατικό κείμενο, καμία κλήση API, ξαναχρησιμοποιεί το ήδη
  υπάρχον "κάρτα" rendering (`**Ετικέτα**: τιμή`) για να δείξει τη λίστα
  εντολών/χρησιμότητά τους. **Single source of truth**: το βοηθητικό
  υπότιτλο στην αρχική οθόνη (`.greeting-hint`) ΔΕΝ απαριθμεί πλέον τις
  εντολές μία-μία - παραπέμπει ΜΟΝΟ στο `HelpCommands` ("Γράψε
  HelpCommands για να δεις τις διαθέσιμες εντολές"), ώστε η λίστα να
  ζει σε ΕΝΑ σημείο (μέσα στο `send()`) αντί να συντηρείται διπλά.
- **`CREATEAADEAFM (ΑΦΜ) [CUS]`** (ΝΕΟ 16/08, ρητό αίτημα χρήστη - ελεύθερη
  σειρά παραμέτρων, regex-based parsing στο `send()`, ΟΧΙ exact-match σαν
  τα υπόλοιπα): standalone εντολή, **ΑΝΕΞΑΡΤΗΤΗ** από την κουρτίνα DR (ρητή
  διόρθωση χρήστη 16/08 - "θέλω να είναι ανεξάρτητο") - εμφανίζεται σαν
  **inline κάρτα ΜΕΣΑ στο κύριο transcript** (`renderStandaloneDrCard`,
  `.msg.assistant.dr-standalone-card`), ΟΧΙ μέσα στην κουρτίνα/
  `drFileListEl`. Επαναχρησιμοποιεί ΑΥΤΟΥΣΙΑ την ΙΔΙΑ rendering/action
  υποδομή με το DR file-upload flow μέσω κοινών functions (καμία διπλή
  υλοποίηση):
  - `renderDrEntryRow(f)` - το ΚΟΙΝΟ template γραμμής/panels, καλείται ΚΑΙ
    από το `renderDrFileList()` (κουρτίνα) ΚΑΙ από το
    `renderStandaloneDrCard()` (transcript).
  - `rerenderDrEntry(entry)` - αποφασίζει ΠΟΥ να ξαναφτιάξει το HTML
    ανάλογα με `entry.standalone` (κουρτίνα ή δικό του DOM node).
  - `handleDrEntryClick(e)` - ΕΝΑΣ κοινός delegated click handler,
    προσαρτημένος ΚΑΙ στο `drFileListEl` ΚΑΙ στο `#transcript`.
  - `removeDrFile(id)` - αφαιρεί είτε από τη λίστα (κουρτίνα) είτε το
    standalone DOM node, ανάλογα.

  **Χωρίς `CUS`** (ΝΕΟ 16/08, ρητή διόρθωση - πριν σιωπηλό default σε
  Προμηθευτή): sodType=null ("ασαφές") -> ΓΕΝΙΚΗ αναζήτηση (οποιοδήποτε
  SODTYPE, `ExecuteFindTraderByAfm`) ΑΝΤΙ για σιωπηλή υπόθεση. Στο
  αποτέλεσμα εμφανίζεται **type picker** (`renderDrTypePicker`):
  - **Βρέθηκε** (σε κάποιο τύπο X) -> κουμπιά για τους 3 ΑΛΛΟΥΣ τύπους
    ("θέλεις να τον ανοίξεις/δημιουργήσεις και ως άλλο τύπο;" - το ΙΔΙΟ
    ΑΦΜ μπορεί να υπάρχει ΚΑΙ ως πολλούς τύπους ταυτόχρονα, ξεχωριστές
    εγγραφές).
  - **ΔΕΝ βρέθηκε πουθενά** -> κουμπιά για ΚΑΙ τους 4 τύπους (Προμηθευτής/
    Πελάτης/Χρεώστης/Πιστωτής) - ο χειριστής διαλέγει, ΔΕΝ μαντεύουμε.
  - Κλικ σε οποιονδήποτε τύπο -> ΝΕΑ ανεξάρτητη κάρτα/γραμμή scoped σε
    εκείνον τον τύπο (`startManualAadeLookup(afm, sodType, standalone)` -
    κάθε τύπος ΕΙΝΑΙ πραγματικά ξεχωριστή εγγραφή TRDR, φυσικό να έχει
    δική του γραμμή).

  **Με `CUS`**: sodType=13, scoped αναζήτηση κατευθείαν
  (`ExecuteFindTraderByAfmAndSodType`) - ΧΩΡΙΣ type picker, ίδια
  συμπεριφορά με πριν. Βρέθηκε -> "Άνοιγμα". Δεν βρέθηκε -> ΑΚΡΙΒΩΣ το
  ΙΔΙΟ ΑΑΔΕ auto-create flow με το DR (`ExecuteGetAadeData`/
  `ExecuteCreateTraderFromAade` έγιναν sodType-aware - πριν ήταν hardcoded
  SODTYPE=12/SUPPLIER, ΤΩΡΑ δέχονται 12 ή 13).
- **`CLEAR`/`CLEARS`** (ΝΕΟ 16/08, ίδιο exact-match μοτίβο): καθαρίζει το
  κύριο transcript + επαναφέρει τη σφαίρα στο κέντρο (`clearMainChat()`)
  **ΚΑΙ** μηδενίζει το server-side `_conversation` (`chat_clear` command,
  `JarvisShell.xaml.cs`) - πραγματικό "ξέχασέ τα όλα", όχι μόνο οπτικό
  καθάρισμα. `CLEARS` κάνει το ΙΔΙΟ, αλλά ΠΡΩΤΑ αποθηκεύει τη συζήτηση σε
  `.md` αρχείο (`Έγγραφα\Jarvis Exports`, ίδιο σταθερό φάκελο/`BuildExportPath`
  με το export feature - ΚΑΝΕΝΑ save dialog, ίδιο ιστορικό ρίσκο με τα
  SaveFileDialog crashes). Το markdown χτίζεται client-side
  (`buildTranscriptMarkdown()`) από `dataset.raw` που κρατάει ΚΑΘΕ `addMessage`
  πάνω στο ίδιο το DOM element (ΩΜΟ κείμενο, πριν το markdown→HTML parsing) -
  ΟΧΙ από το `_conversation` (θα έσερνε μέσα raw Anthropic tool_use/
  tool_result/εικόνες blocks, θορυβώδες για αναγνώσιμο log).

**Επόμενο (Phase 2c) — σχεδιασμός 15/08:** `create_crm_task`/Q&A-log tool →
write σε `SOACTION` μέσω του Soft1 object API (`XSupport`/`XModule`/`XTable`
- **όχι** raw SQL INSERT, το `SOACTION` είναι business object με
SERIES/SERIESNUM/αρίθμηση).

- **✅ Επιβεβαιωμένο working pattern** (βρέθηκε 15/08 στο
  `SoftoneCore/SoftoneCommands.cs`, project του χρήστη -
  `InsertScannHistoric`/`InsertApiActionHistoric`/κ.ά., ήδη σε παραγωγή για
  παρόμοιο σκοπό - ιστορικό ενεργειών/API calls πάνω στο ίδιο `SOACTION`):
  ```csharp
  // INSERT
  XModule m = XConn.CreateModule("SOTASK");   // ίδιο module name, ΔΕΝ
                                               // χρειάζεται νέο Designer object
  XTable SOACTION = m.GetTable("SOACTION");
  try {
      m.InsertData();
      SOACTION.Current["SERIES"]       = jarvisQaSeries;   // ParamCode 500008
                                               // (SELECT ParamValue FROM
                                               // cccParams WHERE ParamCode=500008
                                               // -> 30000, επιβεβαιωμένο 15/08)
      // ΟΧΙ SOACTIONCODE - γεμίζει μόνο του (επιβεβαιώθηκε 15/08 ζωντανά,
      // βλ. "ΥΛΟΠΟΙΗΘΗΚΕ" section πιο κάτω)
      SOACTION.Current["COMMENTS"]     = "Jarvis Q&A";
      SOACTION.Current["REMARKS"]      = keywords;
      SOACTION.Current["cccInitRequest"] = initialRequest;
      SOACTION.Current["cccFinalResp"]   = finalResponse;
      SOACTION.Current["ACTOR"]        = XConn.ConnectionInfo.UserId;  // ο
      SOACTION.Current["ORDEREDBY"]    = XConn.ConnectionInfo.UserId;  // χειριστής
      SOACTION.Current["ACTSTATUS"]    = 3;   // "ολοκληρωμένο" (ίδια τιμή
                                               // με τα υπάρχοντα historic logs)
      int soactionId = m.PostData();          // ΕΠΙΣΤΡΕΦΕΙ το νέο id -
                                               // αυτό ταξιδεύει στο client
  } finally { SOACTION.Dispose(); m.Dispose(); }

  // UPDATE (rating, όταν κλικάρει το ⭐)
  XModule m2 = XConn.CreateModule("SOTASK");
  XTable SOACTION2 = m2.GetTable("SOACTION");
  try {
      m2.LocateData(soactionId);
      SOACTION2.Current.Edit(soactionId);
      SOACTION2.Current["SOSMALLINT"] = rating;   // 1-5
      m2.PostData();
  } finally { SOACTION2.Dispose(); m2.Dispose(); }
  ```
  - **Διόρθωση στο mapping πεδίων παρακάτω**: το πρόσωπο/χειριστής γράφεται
    σε `ACTOR`/`ORDEREDBY` (= `ConnectionInfo.UserId` απευθείας, smallint) -
    **όχι** `ACTPRSN` όπως είχε προταθεί αρχικά· αυτό είναι το πραγματικό,
    ήδη-δουλεμένο pattern σε 5+ σημεία του πηγαίου project.
  - `XConn` = το ίδιο `XSupport` connection object που ήδη χρησιμοποιεί ο
    Jarvis για `query_data`/`GetSQLDataSet` - καμία νέα σύνδεση/auth.
  - ⚠️ **Μην αντιγραφεί** το error-handling pattern του πηγαίου project
    (`catch { InsertScannHistoric(e.Message, ConsData); }` - recursive
    self-call για να καταγράψει το ίδιο το error, ρίσκο stack overflow αν
    ξαναποτύχει το write). Στον Jarvis, απλό logging/exception στο catch.
  - Πηγή: `C:\Users\gkirkmalis.JETOIL.000\OneDrive - CETRACORE-JETOIL
    S.A\Documents\Visual Studio 2019\Projects\SoftOneCore\SoftoneCommands.cs`
    (βλ. και `Soft1Core.cs` στον ίδιο φάκελο).
  - **Cross-check 15/08 με τα επίσημα SoftOne SDK παραδείγματα** (git:
    `SoftOne-Developers-Network/.NET-InProcess`/`.NET-OutProcess`/
    `.NET-WebServices`, clone στο scratchpad): επιβεβαιώνεται το ίδιο
    `CreateModule`/`GetTable`/`InsertData`/`LocateData`/`PostData` API
    (OutProcess Example1/2 - ίδιο `XSupport`/`XModule`/`XTable`, μέσω COM
    interop αντί για native TXCode injection, αλλιώς ίδιο object model).
    `.NET-WebServices` (REST/JSON, δικό του login/clientID) **ΔΕΝ είναι
    σχετικό** - ο Jarvis είναι ήδη in-process, άλλο μονοπάτι.
    - ✅ **Επιβεβαιώθηκε 15/08 από τον χρήστη**: σωστό είναι το
      `tbl.Current.Edit(recordId)` (πραγματικό ID) όπως στο
      `SoftoneCommands.cs` - ΟΧΙ το `Edit(0)`/row-index του επίσημου
      παραδείγματος. Αυτό είναι το pattern που θα χρησιμοποιηθεί στο
      UPDATE (rating) path.
    - Multi-table document pattern (π.χ. `FINDOC`+`ITELINES`+`MTRDOC`)
      χρειάζεται explicit `.Current.Post()` ανά πίνακα πριν το τελικό
      `PostData()` - **δεν μας αφορά** άμεσα (το `SOACTION` Q&A log είναι
      single-table, χωρίς `ACTLINES`), αλλά κρατιέται σαν σημείωση αν
      ποτέ χρειαστεί να γράψουμε και lines.

- **Mapping πεδίων** (επιβεβαιωμένο ότι είναι ελεύθερα - κανένα άλλο
  module/κώδικας δεν τα αγγίζει σήμερα - **✅ αποφασίστηκε 15/08: ΔΕΝ
  ανοίγουμε νέα custom πεδία στο `SOACTION`**, καλύπτεται πλήρως από ήδη
  υπάρχοντα ελεύθερα πεδία· το `SOACTION` το μοιράζονται ήδη πολλά
  modules, schema change εκεί είναι περιττό ρίσκο εφόσον δεν χρειάζεται):
  - ✅ **Τελικό 3-πεδίων mapping (15/08)** - καθαρός διαχωρισμός ρόλων,
    αντί για το αρχικό "REMARKS=keywords/σύνοψη μαζί":
    - `REMARKS` (varchar 2000) → **ΜΟΝΟ λέξεις-κλειδιά** (καθαρή λίστα,
      το βασικό πεδίο για `LIKE` αναζήτηση), φαίνεται και στην κανονική
      οθόνη CRM.
    - `cccInitRequest` (varchar MAX) → **περίληψη αιτήματος, 2 γραμμές**
      (τι ζήτησε/ρώτησε ο χειριστής - συμπυκνώνει ΟΛΟ τον διάλογο, όχι
      μόνο το πρώτο μήνυμα).
    - `cccFinalResp` (varchar MAX) → **παράγραφος με βήματα** - η λύση
      όπως τη έδωσε ο Jarvis, αναλυτική/αριθμημένη, ΟΧΙ σύντομη περίληψη.
    - **Marker convention** (νέο, ίδιο πνεύμα με `❓`/`📊` - βλ. "Help
      mode" ροή παρακάτω):
      ```
      ΛΕΞΕΙΣ-ΚΛΕΙΔΙΑ: λέξη1, λέξη2, λέξη3
      ΠΕΡΙΛΗΨΗ ΑΙΤΗΜΑΤΟΣ: <2 γραμμές>
      ΛΥΣΗ:
      1. <πρώτο βήμα>
      2. <δεύτερο βήμα>
      ```
      Το `index.html` το εξάγει (parse), το αφαιρεί/μορφοποιεί
      διαφορετικά για τον χειριστή, και στέλνει τα 3 κομμάτια στο
      write-tool.
  - ~~`SOACTIONCODE='JARVISQA'`~~ ✅ **ΔΙΟΡΘΩΘΗΚΕ 15/08 (ζωντανό test) -
    ΔΕΝ ορίζεται χειροκίνητα, γεμίζει μόνο του** (κανένα από τα working
    παραδείγματα SoftoneCommands.cs δεν το όριζε ποτέ - το είχαμε
    προσθέσει λάθος). Το `SERIES=30000` (δεσμευμένο ΜΟΝΟ για μας) είναι
    ήδη αρκετό tag ώστε οι queries μας να ξεχωρίζουν καθαρά από άλλα
    modules που γράφουν στο `SOACTION` (π.χ. `CCC*` tank telemetry,
    `CCCS1APV*` approval workflow - επιβεβαιώθηκε 15/08 ότι το
    `CCCS1APV*` δουλεύει μόνο υπό συγκεκριμένες παραμέτρους που δεν θα
    συναντήσουμε, δεν χρειάζεται άλλη προφύλαξη).
  - `ACTOR`/`ORDEREDBY` → ο χειριστής, `= ConnectionInfo.UserId` απευθείας
    (**διορθώθηκε 15/08** - όχι `ACTPRSN`, βλ. επιβεβαιωμένο pattern
    παραπάνω).
  - `SOSMALLINT` (smallint, ελεύθερο) → **βαθμολογία 1-5 αστέρια**
    (`NULL` = μη βαθμολογημένο). Αποφασίστηκε 15/08: αστέρια αντί για
    boolean confirmed/unconfirmed flag.
  - `SERIES` → μέσω `ParamCode 500008` (✅ `ParamValue=30000`,
    επιβεβαιώθηκε 15/08 - βλ. πίνακα παραμέτρων παραπάνω), ΔΕΝ hardcoded.
  - `SODTYPE`/`SOSOURCE`/`SERIESNUM` → **δεν χρειάζεται να τα ορίσουμε**
    (διορθώθηκε 15/08 - λάθος υπερβολική προφύλαξη στο αρχικό σχέδιο). Στο
    ίδιο το `SoftoneCommands.cs` κανένα από τα working παραδείγματα δεν τα
    ορίζει χειροκίνητα - μόνο το `SERIES`. Τα συμπληρώνει αυτόματα το
    Soft1 με βάση το `CreateModule("SOTASK")` + `SERIES`.
  - `TRNDATE`/`INSDATE` → αυτόματα.
- **Αλληλεπίδραση με Dashboard** (✅ αποφασίστηκε 15/08 - edge case,
  **διορθώθηκε ίδια μέρα**): το "Dashboard" γίνεται **global exact-match
  εντολή** (`/^dashboard$/i` - ΟΛΟΚΛΗΡΟ το μήνυμα, τίποτα άλλο - ίδιο
  regex με το ήδη υπάρχον, ασφαλές: δεν πυροδοτείται κατά λάθος από
  πραγματική περιγραφή προβλήματος όπως "δεν βλέπω το dashboard"),
  αναγνωρίσιμη ΚΑΙ μέσα στο δικό του chatbox του Help mode canvas (όχι
  μόνο στο κύριο composer). Όταν γραφτεί εκεί: **κλείνει το Help mode
  (σαν ▼, χωρίς καταγραφή) ΚΑΙ ανοίγει το Dashboard**, ΑΜΕΣΩΣ - **χωρίς
  επιβεβαίωση** (συνεπές με το ▼ που ήδη δεν ρωτάει τίποτα, και δεν
  χάνεται καμία αξία μιας και έτσι κι αλλιώς δεν καταγράφεται πρόωρο
  κλείσιμο).
- **"Help mode" - trigger + UI ροή** (αποφασίστηκε 15/08, ΝΕΟ UI component
  στο `index.html`, ξεχωριστό από το κύριο chat):
  1. Ο χειριστής γράφει **"help"** στο κύριο chat (λέξη-κλειδί, exact
     trigger - λεπτομέρεια υλοποίησης: client-side στο `index.html` ή
     server-side στο system prompt, ΔΕΝ έχει αποφασιστεί ακόμα).
  2. Το σύστημα ανοίγει μια **"κουρτίνα"** (slide-up panel) με κουμπάκι
     **▲** - ο χειριστής το πατάει και ανοίγει σε πλήρη **καμβά** (νέο,
     ξεχωριστό conversation thread μέσα στο ίδιο παράθυρο, όχι νέο
     window).
  3. Μέσα στον καμβά, ο agent ρωτάει τον χειριστή για τη φύση του
     προβλήματος - διάλογος (πιθανόν πολλαπλά turns) μέσα σε αυτό το
     ξεχωριστό thread.
  4. **Με το που δίνεται η λύση** (χρειάζεται νέα σύμβαση μορφοποίησης
     στην απάντηση του Jarvis, ίδιο πνεύμα με τα ήδη υπάρχοντα `❓`/`📊`
     markers, ώστε το `index.html` να ξέρει "αυτό είναι η τελική λύση"):
     - εμφανίζεται αυτόματα η βαθμολόγηση **1-5 ⭐**,
     - το chatbox μέσα στον καμβά γίνεται **ανενεργό** (read-only, καμία
       άλλη ενέργεια δυνατή),
     - η μοναδική επιλογή που μένει είναι να κλείσει η κουρτίνα.
  5. **Override / πρόωρη έξοδος**: οποιαδήποτε στιγμή μέσα στη
     συνομιλία, κουμπάκι **▼** κλείνει την κουρτίνα πρόωρα (π.χ. ο
     χειριστής κατάλαβε μόνος του τι έπρεπε να κάνει, δεν χρειάζεται να
     ολοκληρώσει). Προτεινόμενο default (να επιβεβαιωθεί): σε πρόωρο
     κλείσιμο ΔΕΝ γράφεται `SOACTION` - καμία αξία σε μισή/άκυρη
     καταγραφή στο learned-Q&A tier.
  - **Data mapping**: ✅ βλ. τελικό 3-πεδίων mapping + marker convention
    ("Mapping πεδίων" section παραπάνω, `REMARKS`/`cccInitRequest`/
    `cccFinalResp`).
    - Το write (INSERT) γίνεται στο βήμα 4 (με το που φανεί η λύση,
      `SOSMALLINT=NULL`)· το rating είναι ξεχωριστό δεύτερο write
      (UPDATE στο ίδιο `SOACTION` id, όχι νέο INSERT) όταν κλικάρει το
      αστέρι.
- **Σειρά αναζήτησης** (νέο tool, π.χ. `search_knowledge`, ή επέκταση του
  `query_data` με ρητή οδηγία): πρώτα `SOACTION WHERE SERIES=30000 AND
  (SOSMALLINT IS NULL OR SOSMALLINT>=4)` (✅ διορθώθηκε 15/08 - `SERIES`
  αντί για `SOACTIONCODE`, βλ. "Mapping πεδίων" παραπάνω) ταξινομημένο
  με υψηλότερη βαθμολογία πρώτα· εγγραφές με `SOSMALLINT IN (1,2)` ΔΕΝ
  επαναχρησιμοποιούνται σαν πηγή απάντησης (γνωστές κακές απαντήσεις).
  Μετά, fallback στον στατικό manuals πίνακα.
- **Ανοιχτά** (ενημερώθηκε 15/08, μετά από cross-check του χρήστη ότι όλα
  τα πεδία υπάρχουν πράγματι στο `SOACTION`):
  1. ~~τιμή `ParamCode 500008`/`SODTYPE`/`SOSOURCE`/`SERIESNUM`~~ ✅ λυμένο.
  2. ~~αν καταγράφεται το ενδιάμεσο διάλογο~~ ✅ λυμένο (περιλήψεις, βλ.
     "Data mapping" παραπάνω).
  3. ~~ελεύθερα πεδία στο `SOACTION`~~ ✅ επιβεβαιωμένο (14/14 πεδία
     υπάρχουν στο σχήμα).
  4. ✅ **Λύθηκε 15/08** - δύο ξεχωριστά triggers, ίδιο πνεύμα με το ήδη
     δουλεμένο μηχανισμό του Dashboard (`📊` marker):
     - **Άνοιγμα κουρτίνας**: καθαρά client-side στο `index.html` - JS
       βλέπει literal "help", ανοίγει την κουρτίνα ΑΜΕΣΩΣ, χωρίς να
       περάσει καν από το API (γρήγορο, deterministic, δεν χρειάζεται
       κρίση της AI).
     - **"Εδώ είναι η λύση"** (μέσα στην κουρτίνα): server-side/AI-driven
       - ίδιος ΑΚΡΙΒΩΣ μηχανισμός με το `📊`/`❓` του Dashboard/quick-reply:
       ο Jarvis βάζει το νέο marker (βλ. "Data mapping" παραπάνω) στην
       απάντησή του όταν κρίνει ότι τελείωσε, το `index.html` το διαβάζει
       και τότε εμφανίζει τα ⭐ + κλειδώνει το chatbox. Μόνο εδώ χρειάζεται
       η AI, γιατί μόνο αυτή ξέρει πότε "τελείωσε".
  5. ✅ **Λύθηκε 15/08** - τελικό marker: `ΛΕΞΕΙΣ-ΚΛΕΙΔΙΑ:.../ΠΕΡΙΛΗΨΗ
     ΑΙΤΗΜΑΤΟΣ: <2 γραμμές>/ΛΥΣΗ:\n1. ...\n2. ...` → `REMARKS`/
     `cccInitRequest`/`cccFinalResp` αντίστοιχα (βλ. "Mapping πεδίων").
  6. **Δεν έχει επαληθευτεί ζωντανά ακόμα**: ότι το CSV
     (`S1Jetoil_TableInfo.csv`) αντιστοιχεί στην production βάση (όχι
     "jetoilDemo") - το επόμενο live test θα το επιβεβαιώσει έμμεσα (αν
     το πρώτο πραγματικό INSERT πετύχει).

### ✅ ΥΛΟΠΟΙΗΘΗΚΕ 15/08 (πλήρες Help mode - χρειάζεται live test)

Όλο το παραπάνω σχέδιο γράφτηκε σε κώδικα σήμερα:

- **`Core/JarvisTools.cs`**: `TryParseQaMarker` (regex parse του marker
  block), `CreateQaLogSoAction`/`RateQaLogSoAction` (INSERT/UPDATE στο
  `SOACTION` μέσω `XModule`/`XTable`, ίδιο confirmed pattern με
  `SoftoneCommands.cs`), `GetQaLogSeries` (διαβάζει `ParamCode 500008`
  ζωντανά, δεν είναι hardcoded).
- **`Core/JarvisAgentClient.cs`**: νέο `helpMode` param στο `AskAsync`/
  `BuildSystemPrompt` - ξεχωριστές οδηγίες (ρώτα για το πρόβλημα, κλείσε
  με το marker block όταν έχεις πλήρη λύση).
- **`UI/JarvisShell.xaml.cs`**: `_helpConversation` (ξεχωριστό history),
  handlers για `help_start`/`help_message`/`help_rate`, καλεί
  `TryParseQaMarker`→`CreateQaLogSoAction` όταν βρεθεί το marker, στέλνει
  πίσω `help_reply`/`help_solution` JSON.
- **`UI/web/index.html`**: πλήρης κουρτίνα/καμβός (`#helpCurtain`, ίδιο
  ▲/▼ pattern με το Dashboard), δικό του transcript/composer/⭐ rating
  widget, exact-match "help" trigger στο `send()`, exact-match "Dashboard"
  ΚΑΙ μέσα στο help composer (κλείνει Help + ανοίγει Dashboard, χωρίς
  επιβεβαίωση).
- **Επίσης σήμερα** (μικρότερα, ανεξάρτητα UI additions): `#orbCaption`
  ("Legend" κάτω από το orb - `onProgress` callback στο `AskAsync`, real
  thinking-block captions), custom scrollbar (`::-webkit-scrollbar`).

**Πριν το θεωρήσουμε "έτοιμο"**: χρειάζεται ένα πραγματικό ζωντανό run
μέσα στο Soft1 - να επιβεβαιωθεί ότι (α) το `SOACTION` INSERT πετυχαίνει
με τα πραγματικά `SODTYPE`/`SOSOURCE` (κανένα δεν ορίζεται χειροκίνητα,
βλ. πάνω - πρέπει να δούμε ότι το Soft1 πράγματι τα συμπληρώνει σωστά
μόνο του), (β) ο Claude πράγματι παράγει το marker στη σωστή μορφή live
(το regex είναι αυστηρό), (γ) το UPDATE rating (`.Current.Edit(id)`)
πετυχαίνει όπως αναμένεται.

### ✅ Πρώτο ζωντανό test 15/08 - 2 ευρήματα, ΚΑΙ τα δύο διορθώθηκαν

Το βασικό flow **δούλεψε** (INSERT/marker parsing/⭐ UI) με την πρώτη -
δύο θέματα βρέθηκαν στην πράξη:

1. **`SOACTIONCODE`**: το είχαμε ορίσει χειροκίνητα (`="JARVISQA"`) στο
   `CreateQaLogSoAction` - ΛΑΘΟΣ, το πεδίο γεμίζει μόνο του (συνεπές με
   το ότι κανένα working παράδειγμα στο `SoftoneCommands.cs` δεν το
   όριζε ποτέ - το είχαμε ήδη διαπιστώσει για τα `SODTYPE`/`SOSOURCE`
   αλλά μας ξέφυγε ότι ισχύει και εδώ). ✅ Αφαιρέθηκε - το `SERIES=30000`
   (ήδη δεσμευμένο ΑΠΟΚΛΕΙΣΤΙΚΑ για μας) είναι ήδη αρκετό tag, δεν
   χρειάζεται δεύτερο πεδίο.
2. **Ο Jarvis έκλεινε με τη λύση (και τα ⭐) ΚΑΤΕΥΘΕΙΑΝ στην πρώτη
   απάντηση**, χωρίς πραγματικό διάλογο - η παλιά οδηγία ("αν χρειάζεται,
   ρώτα") άφηνε πλήρη διακριτική ευχέρεια στο μοντέλο. ✅ **Νέα ροή,
   deterministic** (αντί για ad-hoc "μην κλείσεις στο πρώτο turn" hack):
   - Οι ερωτήσεις του Jarvis (ΚΑΙ διευκρινιστικές ΚΑΙ η τελική) γίνονται
     ΠΑΝΤΑ με το ❓/> quick-reply format (ίδια σύμβαση με το κύριο chat) -
     **πριν ήταν ρητά απαγορευμένο** μέσα στο Help mode ("γράφει
     ελεύθερα, δεν υπάρχουν κουμπιά") - αντεστράφη.
   - Μετά τη λύση, ο Jarvis ρωτάει ΠΑΝΤΑ `❓ Θέλεις κάτι άλλο;` με
     επιλογές `Όχι, τίποτα άλλο` / `Ναι, έχω κι άλλη ερώτηση`.
   - Το marker block (άρα και η καταγραφή/⭐) εμφανίζεται **ΜΟΝΟ** ως
     απάντηση στο "Όχι, τίποτα άλλο" - ποτέ νωρίτερα, δομικά αδύνατο να
     κλείσει στο πρώτο μήνυμα αφού δεν υπάρχει ακόμα τίποτα να
     επιβεβαιωθεί.
   - **`index.html`**: τα quick-reply κουμπιά (❓/>) δούλευαν ΜΟΝΟ μέσα
     στο κύριο `transcript` (delegated click listener εκεί) - προστέθηκε
     ΞΕΧΩΡΙΣΤΟΣ listener στο `helpTranscript` (καλεί νέο
     `sendHelpMessageText(text)` helper, το ίδιο pattern με το
     `sendMessage(text)` του κύριου chat) ώστε τα κουμπιά να δουλεύουν
     ΚΑΙ μέσα στον καμβό του Help mode.

## 🆕 Browser mode (15/08) - πραγματικό browsing μέσα στον Jarvis

Τρίτο mode (μετά το Dashboard/Help) - ο χειριστής γράφει **"browser"**
(exact-match) → κουρτίνα (συμπτυγμένη πρώτα, ίδιο 2-βήμα pattern) → ▲ →
η οθόνη χωρίζεται **70% (αριστερά) πραγματικό browser / 30% (δεξιά)
chatbox**. Ο χειριστής γράφει διεύθυνση στο address bar αριστερά ΚΑΙ
μιλάει στον Jarvis δεξιά· ο Jarvis, όταν του ζητηθεί ρητά, μπορεί να
ανοίξει ο ίδιος μια σελίδα στο address bar εκ μέρους του χειριστή.

- **🐛 Θεμελιώδης τεχνική απόφαση**: ΟΧΙ `<iframe>` μέσα στο ήδη
  υπάρχον `index.html`/`webView` - πολλά πραγματικά sites (Google,
  τράπεζες, κρατικές υπηρεσίες) μπλοκάρουν embedding με
  `X-Frame-Options`/CSP, θα έδειχνε άδεια σελίδα. Αντ' αυτού: **δεύτερο,
  ξεχωριστό `WebView2` control** στο ίδιο WPF (`browserView`,
  `JarvisShell.xaml`) - πραγματικό top-level browsing, χωρίς
  περιορισμούς.
- **Layout** (`JarvisShell.xaml`, ΠΡΩΤΗ δομική αλλαγή στο XAML - πριν
  ήταν ένα μόνο `Grid`+ένα `WebView2`): 2-στηλο `Grid`
  (`browserColumn`=0 κανονικά/70% όταν ανοιχτό, `chatColumn`=* -
  100% κανονικά/30% όταν Browser mode ενεργό). Αριστερά:
  native `TextBox` (address bar) + `Button`s (←/Go/✕) + `browserView`.
  Δεξιά: το ΗΔΗ υπάρχον `webView`/`index.html` - απλά συρρικνώνεται.
  **Το δεξί "chatbox" ΔΕΝ είναι ξεχωριστό native control** - είναι ΝΕΑ
  κουρτίνα (`#browserCurtain`) ΜΕΣΑ στο ίδιο `index.html`, ίδιο
  ΑΚΡΙΒΩΣ pattern με το Help mode (δικό της transcript/composer/mini-orb
  Legend, `_browserConversation` ξεχωριστό history) - απλά, μιας και το
  `webView` έχει ήδη συρρικνωθεί σε 30% από το native layer, η κουρτίνα
  γεμίζει φυσικά ό,τι χώρο απομένει, χωρίς δικό της column-split.
- **Slide-in animation** (ζητήθηκε ρητά από τον χρήστη, με ρητή
  προειδοποίηση για το ρίσκο): `TranslateTransform` πάνω στο
  `browserPane` (`DoubleAnimation`, X: `-targetWidth → 0` όταν ανοίγει,
  `EaseOut`/`EaseIn`, 320ms) - standard, χαμηλού ρίσκου WPF pattern.
  **ΣΚΟΠΙΜΑ ΔΕΝ** προσπαθήσαμε να "animate-άρουμε" το ίδιο το
  `GridLength` (δεν υποστηρίζεται native, θα χρειαζόταν custom
  `AnimationBase` class - περιττό ρίσκο). Το functional state (πλάτος
  στήλης/visibility) μπαίνει ΑΜΕΣΩΣ, ανεξάρτητα από το animation -
  **fallback αν ποτέ αποδειχθεί προβληματικό ζωντανά**: αφαίρεση ΜΟΝΟ
  του `BeginAnimation()` call, όλα τα υπόλοιπα ήδη δουλεύουν σωστά χωρίς
  αυτό (instant εμφάνιση αντί για slide).
- **Address bar**: 100% native WPF (`TextBox`+`Button`s, ΟΧΙ HTML) - ο
  χειριστής γράφει URL/κείμενο, Enter ή "Go" → `NavigateBrowserFromAddressBar`
  → `NormalizeUrl` (προσθέτει `https://` αν λείπει, ή γίνεται Google
  search αν δεν μοιάζει URL) → `browserView.CoreWebView2.Navigate(url)`.
  `NavigationCompleted` συγχρονίζει την address bar όταν ο χειριστής
  κλικάρει link ΜΕΣΑ στη σελίδα (ίδια συμπεριφορά με κανονικό browser).
  Καμία round-trip μέσω JS/postCommand - καθαρά native event handling.
- **`open_url` tool** (`JarvisTools.cs`) - ΝΕΟ, εκτίθεται ΜΟΝΟ στο
  Browser mode (`browserMode:true` στο `AskAsync` - όχι `query_data`/
  `export_query_to_file` εκεί, εστιασμένο tool surface). Δεν χρειάζεται
  `XSupport` - καθαρά UI ενέργεια. **Δεν επιστρέφει περιεχόμενο
  σελίδας** στο context του Claude - μόνο ανοίγει τη σελίδα· το system
  prompt το εξηγεί ρητά (ώστε ο Jarvis να μην προσποιείται ότι "βλέπει"
  τι δείχνει η σελίδα μετά). `onNavigate` callback (νέο param στο
  `AskAsync`, ίδιο idiom με το `onProgress`) - το `JarvisShell` το
  υλοποιεί σαν `url => NavigateBrowserView(url)`, ΚΟΙΝΗ μέθοδο με την
  address bar (μία πηγή αλήθειας για navigate+ενημέρωση address bar).
  System prompt ρητά λέει: navigate **ΜΟΝΟ μετά από ρητή προτροπή** του
  χειριστή, ΠΟΤΕ αυτόνομα.
- **Νέα μηνύματα**: `browser_open`/`browser_close` (JS→C#, ανοίγουν/
  κλείνουν το native pane), `browser_start`/`browser_message` (JS→C#,
  ίδιο με `help_start`/`help_message`), `browser_reply`/`browser_status`
  (C#→JS, ίδιο με `help_reply`/`help_status`), **`browser_closed`**
  (C#→JS, ΝΕΟ - ειδοποιεί το index.html όταν το κλείσιμο ξεκίνησε από τη
  NATIVE πλευρά, π.χ. το ✕ στην address bar, ώστε η HTML κουρτίνα να
  συγχρονιστεί - χωρίς αυτό θα έμενε "ανοιχτή" οπτικά χωρίς το
  πραγματικό browser δίπλα της).
- **Reuse existing infra**: `_webView2Env` (το ΙΔΙΟ `CoreWebView2Environment`
  με το κύριο `webView`, lazy init του `browserView` μόνο στο πρώτο
  άνοιγμα - όχι eager, γλιτώνει resources αν δεν χρησιμοποιηθεί ποτέ).
  `maybeRevertToIdle()`/`anyRealMessageSent`/`.app.browser-active`
  (composer padding) - ίδιος μηχανισμός με Dashboard/Help, επεκτάθηκε.
- **🔧 ΔΕΝ έχει γίνει compile/live test ακόμα** (πρώτη δομική αλλαγή σε
  XAML σήμερα, δεν μπορεί να επιβεβαιωθεί χωρίς Visual Studio) - να
  δοκιμαστεί: (α) ανοίγει σωστά το native pane/animation, (β) η address
  bar navigate-άρει σωστά, (γ) το `open_url` tool πράγματι ανοίγει
  σελίδα όταν ζητηθεί, (δ) το κλείσιμο (▼ ΚΑΙ ✕) συγχρονίζει σωστά ΚΑΙ
  τις δύο πλευρές.
- **Γνωστός περιορισμός**: το πλάτος της native στήλης μπαίνει σαν
  ΣΤΑΘΕΡΟ pixel value (υπολογισμένο τη στιγμή του ανοίγματος) - αν ο
  χειριστής αλλάξει μέγεθος στο παράθυρο ΕΝΩ το Browser mode είναι
  ανοιχτό, η αναλογία 70/30 ΔΕΝ ξαναϋπολογίζεται αυτόματα. Δεν
  υλοποιήθηκε resize-handler σήμερα (εκτός scope).

## ✅ Phase 3 — Export & Dashboard (14/08)

- **Export Excel/CSV/PDF** (`Core/XlsxWriter.cs`/`CsvWriter.cs`, καθαρό
  .NET, ΧΩΡΙΣ εξωτερικό NuGet - `XlsxWriter` πάνω σε
  `System.IO.Compression.ZipArchive`): κουμπιά κάτω από κάθε πίνακα/κάρτα
  στο chat. PDF μέσω του built-in `CoreWebView2.PrintToPdfAsync` (Chromium
  print σε κρυφό `#printArea` με `@media print` CSS) - καμία PDF βιβλιοθήκη.
  Auto-save σε `Έγγραφα\Jarvis Exports\` με timestamp filename (ΟΧΙ
  `SaveFileDialog` - βλ. "⚠️ Crash lessons" παρακάτω). Το path γυρνάει σαν
  clickable mini-markdown link (`[όνομα](path)`) → `Process.Start`.
  - **🐛 Bug βρέθηκε+διορθώθηκε 15/08** (ζωντανό test): τίτλος γραφήματος
    με παρένθεση μέσα (π.χ. "...(καθαρή αξία €)...") → το αρχείο
    δημιουργούνταν ΣΩΣΤΑ (το C# ποτέ δεν είχε πρόβλημα), αλλά το
    `INLINE_LINK_RE` (`index.html`) σταματούσε στην ΠΡΩΤΗ `)` που έβρισκε
    - αν αυτή ήταν ΜΕΣΑ στο path (όχι το πραγματικό τέλος), το link
    κοβόταν στη μέση. Δύο ορατά συμπτώματα, ΜΙΑ αιτία: (α) "Το αρχείο
    δεν βρέθηκε" με κομμένο path στο σημείο της παρένθεσης, (β) το
    "υπόλοιπο" κομμάτι του path έμενε σαν σκέτο κείμενο ΜΕΤΑ το link,
    δίνοντας την εντύπωση "διπλού" timestamp/ονόματος στο μήνυμα
    επιτυχίας. Διόρθωση: `([^)]+)` → `(.+?)\)(?=\s|$)` (non-greedy +
    lookahead, η ')' πρέπει να ακολουθείται από whitespace/τέλος, όχι
    οποιαδήποτε ')' μέσα στο path). ΕΠΙΣΗΣ: `suggestFilename` πλέον
    αφαιρεί `[]()` από τους προτεινόμενους τίτλους (πρόληψη, όχι μόνο
    διόρθωση) + debounce (1.5s) στα export κουμπιά (άσχετο με αυτό το
    συγκεκριμένο bug, αλλά προστατεύει από διπλό-κλικ σε exports που θα
    έπαιρναν το ίδιο timestamp - η ακρίβεια είναι μόνο μέχρι το
    δευτερόλεπτο).
- **Dashboard** ("κουρτίνα"): ο χειριστής γράφει "Dashboard" στο chat
  (client-side μόνο, δεν πάει στο backend) → εμφανίζεται
  `#dashboardCurtain` (ΕΝΑ ενιαίο sliding στοιχείο - τίτλος-γραμμή +
  περιεχόμενο μαζί, `transform: translateY` animation, βελάκι αλλάζει
  φορά ΜΟΝΟ στο `transitionend`, ΟΧΙ στο κλικ). Date filter → κάθε query
  (άνοιγμα/refresh) τρέχει τον Jarvis με **δικό του, ΑΠΟΜΟΝΩΜΕΝΟ ιστορικό**
  (`new List<JObject>()`, ΟΧΙ το κοινό `_conversation`) - επιβεβαιωμένο
  bug: μοιρασμένο ιστορικό έκανε τον Jarvis να "αγκυρώνεται" στην
  προηγούμενη ημερομηνία. Το κουμπί "Ανανέωση" κάνει πάντα **override**
  (ακυρώνει το τρέχον με το Stop sentinel, ξεκινάει νέο αμέσως) -
  `requestId` ταξιδεύει JS↔C# ώστε καθυστερημένες/ακυρωμένες απαντήσεις να
  αγνοούνται. Auto-upgrade: οποιοσδήποτε 2-στηλος πίνακας με αριθμητική
  στήλη γίνεται bar chart ΑΥΤΟΜΑΤΑ, ό,τι κι αν έγραψε ο Jarvis γύρω του
  (δεν βασίζεται 100% στο να θυμάται το `📊` πρόθεμα).
- **Attach/paste στο composer**: 📎 εικονίδιο (native `<input type="file">`,
  ΟΧΙ C# dialog) + paste εικόνας (screenshot) + paste Excel (TSV →
  Markdown table αυτόματα, ΠΡΙΝ τον έλεγχο για εικόνα - το Excel βάζει
  ΚΑΙ tab-separated κείμενο ΚΑΙ bitmap preview στο clipboard ταυτόχρονα).

### ⚠️ Crash lessons (μη τα ξανακάνεις)

Επιβεβαιωμένο LIVE crash (ολόκληρο το Soft1, "External exception/
EExternalException"), ΔΥΟ φορές, με ΔΥΟ διαφορετικά dialog APIs
(`Microsoft.Win32.SaveFileDialog` WPF, μετά `System.Windows.Forms.
SaveFileDialog`) - το κοινό τους σημείο ήταν `ShowDialog()` μέσα στο
`CoreWebView2_WebMessageReceived` callback (πιθανό COM/RPC reentrancy
conflict). **Κανόνας**: ΚΑΝΕΝΑ modal dialog (native ή C#) μέσα σε αυτό το
callback - `Process.Start`/`PrintToPdfAsync`/native `<input type="file">`
είναι ασφαλή (δεν ανοίγουν δικό τους nested message loop στο thread μας),
`ShowDialog()` όχι. Αν χρειαστεί ποτέ κάτι σαν file-picker, χρησιμοποίησε
πάντα το native browser `<input type="file">`, όχι C# dialog.

**ΤΡΙΤΗ εμφάνιση, ΝΕΑ αιτία, 18/08** - ζωντανό bug report χρήστη
("E-Abort") δοκιμάζοντας το νέο .xlsx attachment feature. ΙΔΙΑ γενική
κατηγορία (COM/RPC reentrancy μέσα στο `CoreWebView2_WebMessageReceived`
callback) αλλά ΔΙΑΦΟΡΕΤΙΚΗ αιτία από τα δύο dialog crashes πιο πάνω:
`HandleReadOfficeDocument` ήταν **sync `void`, χωρίς `await`** - το ZIP/XML
parsing ενός πραγματικού `.xlsx` έτρεχε ΣΥΓΧΡΟΝΑ πάνω στο UI thread, ΜΕΣΑ
στο callback - ο native Delphi/Soft1 host δεν έπαιρνε τον έλεγχο πίσω
έγκαιρα -> COM timeout/abort. **Διορθώθηκε**: `Task.Run` γύρω από το
`DocumentReaders.ReadOfficeDocumentAsText` call, `HandleReadOfficeDocumentAsync`
(`await`-ed από το callback, ίδιο idiom με ΟΛΟΥΣ τους άλλους handlers στην
αλυσίδα). **Γενικευμένος κανόνας** (πέρα από το "κανένα dialog"): ΚΑΝΕΝΑ
sync/blocking call μέσα στο `CoreWebView2_WebMessageReceived` - ΑΚΟΜΑ κι
αν "φαίνεται γρήγορο" (parsing, IO, οτιδήποτε όχι-instant) - πάντα
`await Task.Run(...)` ή ήδη-async API. Θα χρειαστεί να ελεγχθεί αν
υπάρχουν ΑΛΛΑ sync deterministic handlers σε αυτό το αρχείο με το ίδιο
ρίσκο (δεν ελέγχθηκαν όλα ρητά σε αυτή τη διόρθωση, μόνο το συγκεκριμένο
που ανέφερε ο χρήστης).

### Backlog / μικρές βελτιώσεις

- ~~Clickable export path~~ — ✅ **ολοκληρώθηκε** (βλ. Phase 3 παραπάνω):
  `[όνομα](path)` mini-markdown link, click → `Process.Start`. Επιβεβαιωμένο
  ζωντανά ότι το `Process.Start` ΕΙΝΑΙ ασφαλές μέσα στο
  `WebMessageReceived` callback (σε αντίθεση με τα `ShowDialog()`).

Καμία άλλη εκκρεμότητα καταγεγραμμένη εδώ αυτή τη στιγμή - βλ. "Roadmap
ιδέες" (#4/#5) πιο πάνω για τα μεγάλα ανοιχτά θέματα.

---

## ✅ JARVISCOURIER (17-18/08) — ζωντανά επιβεβαιωμένο (δημιουργία + ακύρωση voucher)

Ρητό αίτημα χρήστη 17/08: φέρε τη λειτουργικότητα του (ήδη υπάρχοντος, sibling)
S1Courier project μέσα στον Jarvis, με ΔΙΚΟ ΤΟΥ entitlement (ώστε να
ενεργοποιείται/απενεργοποιείται ανεξάρτητα ανά πελάτη), σε δική του κουρτίνα.

**Reuse, όχι επανάχτισμα**: reference στο ήδη-deployed `S1Courier.dll`
(HintPath στο `.csproj`, ΙΔΙΟ idiom με `Softone.Lib.dll`/`Newtonsoft.Json.dll` -
ΟΧΙ ProjectReference, ώστε να μη σέρνει μέσα όλα τα NuGet dependencies του
S1Courier). Ο Jarvis χρησιμοποιεί αυτούσιες τις `ICourierProvider`/
`ShipmentRequest`/`ShipmentResult`/`CancelResult`/`CourierProviderFactory`/
τους 4 providers (ACS/ΕΛΤΑ/Γενική/Courier Center) - καμία δεύτερη υλοποίηση
courier-API integration.

**Entitlement**: `AccessConfig.CourierToolName = "JARVISCOURIER"`, μέσω του
ήδη υπάρχοντος `JarvisLicenseGuard.CheckAccessSilent` (ίδιο pattern με
`JARVISDOCREADER`) - ΞΕΧΩΡΙΣΤΟ από το `S1COURIER` του standalone προϊόντος.

**v1 scope** (ρητή απόφαση χρήστη): ΜΟΝΟ μεμονωμένη έκδοση/ακύρωση voucher
(με ή χωρίς παραστατικό) - ΟΧΙ μαζική/batch (μένει για αργότερα).

**Ροή** (ρητά περιγραμμένη από χρήστη):
1. Χειριστής ζητάει παραστατικά με prompt στο chat της κουρτίνας Courier - ο
   Claude τα βρίσκει με `query_data` (ελεύθερο SQL) και τα εμφανίζει
   ΑΠΕΥΘΕΙΑΣ στο κύριο παράθυρο μέσω `show_courier_documents` (ΙΔΙΟ idiom με
   `show_calendar_entries` του Email curtain - ο Claude υπολογίζει, το tool
   απλά ΜΕΤΑΦΕΡΕΙ το αποτέλεσμα, καμία δεύτερη επεξεργασία στο backend).
2. Ανά γραμμή, 2 κουμπιά - **deterministic**, καμία εμπλοκή LLM:
   - "Εμφάνιση εγγραφής" → ξαναχρησιμοποιεί αυτούσιο το `JarvisTools.
     ExecuteOpenDocument` (ίδιο μηχανισμό με το `open_document` tool).
   - "Δημιουργία Voucher" → ανοίγει modal, port 1-προς-1 του
     `CourierControl.xaml` (πεδία, capability-driven enable/disable,
     validation - βλ. παρακάτω).
3. Στο modal: "Έκδοση Voucher" (`JarvisCourier.CreateVoucherAsync`, port
   `btnCreate_Click`) → γράφει πίσω `FINDOC.VARCHAR01/VARCHAR02/
   CCCCOURJOBID`, δείχνει το PDF σε 2ο tab (`GetVoucherPdfAsync`, port
   `btnPrintVoucher_Click`, base64 αντί για temp file).
4. "Ακύρωση Voucher" (εμφανίζεται μόνο όταν υπάρχει ήδη ενεργή αποστολή) -
   `JarvisCourier.CancelVoucherAsync`, port `btnCancelShipment_Click`,
   καθαρίζει τα ίδια 3 πεδία σε `NULL`.

**Capability-driven UI, ΧΩΡΙΣ δεύτερη αντιγραφή**: `LoadActiveProviders`
διαβάζει τον **πραγματικό** πίνακα `CCCCRPROV` (ΟΧΙ το νεκρό
`S1Courier.Data.CourierRepository`, που δουλεύει πάνω σε λάθος/legacy πίνακα
`CCCCOURIER_PROVIDERS`) και ΕΠΙΠΛΕΟΝ instantiate-άρει κάθε provider μέσω
`CourierProviderFactory.Create` για να διαβάσει τα ΠΡΑΓΜΑΤΙΚΑ capability
flags (`SupportsCodChequeDate`/`SupportsDeliveryTimeWindow`/
`SupportsDeliveryTimeRange`/`SupportsSaturdayDelivery`/`SupportsDeliveryDate`).
Το JS ενεργοποιεί/απενεργοποιεί πεδία βάσει αυτών των flags - επιβεβαιώθηκε
ζωντανά ότι ταιριάζει ΑΚΡΙΒΩΣ με το πραγματικό WPF (π.χ. ACS δεν δείχνει ποτέ
ημ/νία επιταγής ακόμα κι όταν είναι επιλεγμένη αντικαταβολή+Επιταγή, ΕΛΤΑ ναι).

**⚠️ VARCHAR02 = ProviderNAME, όχι ProviderCode**: το πραγματικό
`CourierControl.btnCreate_Click` γράφει `_activeProvider.ProviderName` (όχι
code) στο `FINDOC.VARCHAR02` - το αντιγράψαμε πιστά. Το πρόβλημα: το
πραγματικό S1Courier δεν χρειάζεται ποτέ να το ξαναδιαβάσει (το Cancel εκεί
βασίζεται στο `_activeProvider` που μένει στη μνήμη μέσα στην ίδια session
UI), αλλά ο Jarvis ΔΕΝ έχει τέτοιο in-memory state ανά παραστατικό - πρέπει
να ξαναβρει τον provider από τη βάση σε κάθε cancel. Λύση: νέο
`JarvisCourier.GetProviderConfigByName` (lookup με `PROVNAME`, ΟΧΙ
`PROVCODE`) ειδικά για το Cancel path.

**Inline confirm, ΟΧΙ native `confirm()`** (ρητή διόρθωση χρήστη 18/08 - το
native browser dialog πετάγεται οπτικά εκτός modal): κλικ στο "Ακύρωση
Voucher" δείχνει inline bar στο footer (μήνυμα + κουμπιά Ναι/Όχι, αριστερά
του κουμπιού) αντί για `window.confirm()`.

**Bug βρέθηκε/διορθώθηκε στο build**: `MTRDOC` table ΔΕΝ έχει στήλη
`MTRDOC` (το PK του είναι `FINDOC`, εξαίρεση στη συνήθη σύμβαση "PK = όνομα
πίνακα" - βλ. [[soft1-pk-naming-convention]]) - αφαιρέθηκε από το SELECT στο
`BuildRequestFromFindoc` (ήταν κι αχρησιμοποίητη). Επίσης `CancelShipmentAsync`
επιστρέφει `CancelResult` (ΟΧΙ `ShipmentResult`, διαφορετικός τύπος, ίδια
πεδία `Success`/`ErrorMessage`).

**Αρχεία**: νέο `Core/JarvisCourier.cs`, `AccessConfig.CourierToolName`,
`courierMode` στο `JarvisAgentClient` (tools: `query_data`/`open_document`/
`show_courier_documents` ΜΟΝΟ), 7 handlers στο `JarvisShell.xaml.cs`
(`HandleCourierStartAsync`/`HandleCourierMessageAsync`/
`HandleCourierOpenDocument`/`HandleCourierLoadVoucherFormAsync`/
`HandleCourierCreateVoucherAsync`/`HandleCourierGetVoucherPdfAsync`/
`HandleCourierCancelVoucherAsync`), `.courier-curtain`/voucher-modal CSS/HTML/JS
στο `index.html`.

**Εκτός v1** (ρητά ή εκ των πραγμάτων):
- Μαζική έκδοση/εκτύπωση voucher (ρητά εκτός scope).
- Εντολή που ανοίγει το voucher modal ΑΠΕΥΘΕΙΑΣ από το ΚΥΡΙΟ chat (ΟΧΙ μόνο
  μέσα από τη λίστα της κουρτίνας) - ο χρήστης το ανέφερε ως επόμενο βήμα
  ΜΕΤΑ την κουρτίνα ("Παμε πρωτα να φτιάξουμε την κουρτίνα..."), δεν έχει
  ξεκινήσει ακόμα.

**Tracking αποστολής**: ΔΕΝ χτίζεται σαν custom tracking UI/API integration
(σε αντίθεση με το `btnTrack_Click` του πραγματικού S1Courier) - ρητή
απόφαση χρήστη 18/08: καλύπτεται από το υπάρχον **Browser mode**. Λύθηκε
ΚΑΙ το "πρέπει να ψάξει χειροκίνητα ο χειριστής" κομμάτι: νέο custom field
`CCCCRPROV.CCCTRACKINGURL` (VarChar(500), Designer, caption "URL
Αναζήτησης Voucher") - URL template ανά provider με placeholder `{NUMBER}`
(π.χ. `https://www.acscourier.net/track/{NUMBER}`). Το
`JarvisCourier.LoadActiveProviders` το διαβάζει ΑΠΕΥΘΕΙΑΣ από το `DataRow`
(ΟΧΙ μέσω `CourierProviderConfig` - τύπος από το `S1Courier.dll`, δεν
μπορούμε να του προσθέσουμε πεδίο) και το εκθέτει σαν
`trackingUrlTemplate` στο JSON των providers. Κουμπί "🔍 Tracking" στο
voucher modal (ορατό ΜΟΝΟ όταν υπάρχει ενεργή αποστολή ΚΑΙ ο provider που
την εξέδωσε έχει συμπληρωμένο template - matching by ProviderNAME, ΟΧΙ
code, ίδιο idiom με το Cancel) → αντικαθιστά το `{NUMBER}`, κλείνει το
modal, `postCommand({type:'browser_open', url})`. Το `OpenBrowserPane`
πήρε προαιρετικό `url` param (ίδια μέθοδο navigate με address
bar/`open_url` tool - μία πηγή αλήθειας) ώστε να ανοίγει ΚΑΤΕΥΘΕΙΑΝ στη
σελίδα tracking αντί για άδειο/προηγούμενο URL.

**`HelpCommands`**: ενημερώθηκε με τις εντολές `Email` και `Courier` (έλειπαν).

### ✅ Voucher μέσω chat (18/08) - χωρίς modal, με υποχρεωτική επιβεβαίωση

Ρητό αίτημα χρήστη: "θα πρέπει να μπορεί να το κάνει και από chat... αν θέλω " +
"να κάνω μια ενέργεια που έχει modal να μπορεί να γίνεται από τον agent " +
"χωρίς το modal, μόνο με οδηγίες" - γενική αρχή, εφαρμόστηκε πρώτα σε
Cancel + Create (Tracking/mass μένουν deterministic-only προς το παρόν).

- **`cancel_courier_voucher`** (LLM tool, courierMode) - ο Claude ΗΔΗ έχει
  βρει τις 4 τιμές (findocId/providerName/shipmentNumber/jobId) μέσω
  `query_data` πριν το καλέσει. Reuse ΑΥΤΟΥΣΙΟ το `CancelVoucherAsync`.
- **`get_courier_voucher_data`** + **`create_courier_voucher`** (LLM tools,
  courierMode) - το πρώτο wraps `BuildRequestFromFindoc`+
  `LoadActiveProviders` (ο Claude βλέπει τι ΕΙΝΑΙ ήδη γνωστό, ρωτάει ΜΟΝΟ
  για ό,τι λείπει/είναι ασαφές - courier, ΑΚ/επιταγή, ώρα παράδοσης), το
  δεύτερο reuse ΑΥΤΟΥΣΙΟ το `CreateVoucherAsync`. **ΜΟΝΟ σε αυτό το
  (chat) path**, το PDF **αποθηκεύεται στο δίσκο** (`Έγγραφα\Jarvis
  Exports\`, ΙΔΙΟ idiom με τα Phase 3 exports - "Clickable export path",
  `[shipmentNumber.pdf](path)` → click → `Process.Start`, ήδη ασφαλές/
  επιβεβαιωμένο μηχανισμό, καμία νέα πλομβαρία frontend χρειάστηκε - το
  mini-markdown parser/click handler είναι ήδη γενικό, δουλεύει
  αυτόματα σε ΟΠΟΙΟΔΗΠΟΤΕ transcript). Στο modal path (κουμπί) το PDF
  παραμένει base64/iframe, όπως πριν - δύο ΞΕΧΩΡΙΣΤΑ entry points στο
  ΙΔΙΟ `CreateVoucherAsync`/`GetVoucherPdfAsync`, καμία διπλή λογική.
- **Υποχρεωτική επιβεβαίωση σε ΞΕΧΩΡΙΣΤΟ turn** (system prompt, ΟΧΙ
  τεχνικός περιορισμός στο tool schema): ο Claude ΔΕΝ επιτρέπεται να
  καλέσει `cancel_courier_voucher`/`create_courier_voucher` στο ΙΔΙΟ turn
  που βρήκε/έδειξε το παραστατικό ή τα δεδομένα - πρέπει ΠΡΩΤΑ να ρωτήσει
  (❓/> quick-reply, ίδια σύμβαση με όλο το app) και να περιμένει ρητή
  απάντηση σε επόμενο μήνυμα. Και οι δύο ενέργειες είναι ανεπίστρεπτες
  (πραγματική ακύρωση/έκδοση αποστολής courier).
- Μετά από επιτυχή δημιουργία, ο Claude ξανακαλεί `show_courier_documents`
  ώστε να ενημερωθεί η λίστα στο κύριο παράθυρο - καμία νέα C#→JS
  ειδοποίηση χρειάστηκε, reuse του ήδη υπάρχοντος tool.

**Standalone εντολή `CREATEVOUCHER <findocId>`** (index.html `send()`,
ίδιο idiom με `CREATEAADEAFM`) - λύνει το παλιό pending item "Modal στο
κεντρικό chat", ΑΛΛΑ μέσω συζήτησης αντί για raw modal. **Σκόπιμη
αρχιτεκτονική επιλογή**: ΔΕΝ χτίστηκε ξεχωριστό
entitlement-check/pipeline - ανοίγει την ήδη σωστά-φυλαγμένη κουρτίνα
Courier (`showCourierBar()`+`openCourier()`) και στέλνει το αίτημα
ΑΥΤΟΜΑΤΑ μόλις περάσει το ήδη υπάρχον `courier_start` entitlement check
(`pendingVoucherFindocId`, καταναλώνεται στο `courier_access_result`) - ή
απευθείας αν η κουρτίνα ήταν ήδη ανοιχτή/allowed. Ο λόγος: κατά το
σχεδιασμό εντοπίστηκε ότι το προϋπάρχον standalone `CREATEAADEAFM`
(`HandleDrManualLookupAsync`) **ΔΕΝ ελέγχει καθόλου `_drAllowed`** πριν
εκτελεστεί - ξεχωριστό, προϋπάρχον bug, καταγράφηκε ως spawned task για
διόρθωση εκεί, ΑΛΛΑ δεν αντιγράφηκε τυφλά το ίδιο idiom εδώ· το reuse του
ήδη ελεγμένου `courier_start`/`_courierAllowed` μονοπατιού είναι ΚΑΙ
απλούστερο ΚΑΙ ασφαλέστερο από ένα δεύτερο, παράλληλο pipeline.

---

## ✅ Ομοιόμορφο "Stop" κουμπί σε όλα τα composers (18/08)

Ρητό αίτημα χρήστη: το κουμπί αποστολής του κύριου chat γίνεται κόκκινο
"Stop" (μετά από `STOP_DELAY_MS`=4s) όσο ο Jarvis σκέφτεται - "θέλω σε όλα
τα κουμπιά που στέλνουν εντολές να είναι παντού ομοιόμορφο". Ίσχυε ΜΟΝΟ
στο κύριο `#sendBtn` (`beginThinking`/`stopThinking`) - Help/Browser/
Email/Courier composers είχαν μόνο spin animation, όχι clickable stop.

Νέο γενικό `makeStoppableComposerButton(btn)` (index.html) - επιστρέφει
`{begin, end}`, καλείται από κάθε `setXThinking(v)` αντί για το απλό
`classList.toggle('thinking', v)`. ΙΔΙΟ sentinel (`__JARVIS_STOP__`)
παντού - το backend έχει ΕΝΑ κοινό in-flight `_cts`
(`JarvisAgentClient.CancelCurrent`), οπότε η ακύρωση από ΟΠΟΙΟΔΗΠΟΤΕ
κουμπί σταματάει ό,τι πραγματικά τρέχει τη στιγμή εκείνη - καμία αλλαγή
χρειάστηκε στο backend.

**Τεχνική λεπτομέρεια (event listener ordering)**: το νέο listener
προστίθεται με `capture:true` + `stopImmediatePropagation()` ΠΡΙΝ (πιο
πάνω στο αρχείο) από το ήδη υπάρχον `Xbtn.addEventListener('click',
sendXMessage)` - σκόπιμα, γιατί σε listeners πάνω στο ΙΔΙΟ target element
ο browser τα τρέχει με σειρά ΚΑΤΑΧΩΡΗΣΗΣ (όχι capture-πρώτα), άρα η σειρά
στο αρχείο έχει σημασία. Επιβεβαιώθηκε headless: κλικ ενώ `.stoppable`
ΔΕΝ πυροδοτεί το `sendXMessage` (κανένα `courier_message`/κ.λπ. postCommand),
ΜΟΝΟ το `__JARVIS_STOP__`.

---

## ✅ Bugfix: bar chart (📊) δεν αποδιδόταν (18/08)

Ρητή αναφορά χρήστη: ζήτησε διάγραμμα, ο Jarvis "νόμιζε ότι το κατάφερε"
αλλά έβγαζε συνέχεια απλούς πίνακες. Ο Claude ΟΝΤΩΣ χρησιμοποιούσε σωστά
τη σύμβαση `📊 τίτλος` + 2-στηλο table (βλ. system prompt πιο κάτω) - το
πρόβλημα ήταν στο `CHART_RE` block του `index.html` `parseAssistant()`:
απαιτούσε τον πίνακα ΑΚΡΙΒΩΣ στην επόμενη γραμμή (`lines[i+1]`), ΧΩΡΙΣ
ανοχή για κενή γραμμή ανάμεσα - κάτι που ο Claude ΣΧΕΔΟΝ ΠΑΝΤΑ βάζει
(φυσιολογική markdown μορφοποίηση, το system prompt δεν το απαγόρευε
ρητά). Αποτέλεσμα: σιωπηλή αποτυχία σε plain `heading`+`table` (ΧΩΡΙΣ
μπάρες) σε σχεδόν ΚΑΘΕ πραγματική προσπάθεια - ούτε ο χειριστής ήξερε
γιατί, ούτε ο ίδιος ο Claude (βλέπει μόνο το κείμενο που έστειλε, όχι
πώς αποδόθηκε στο UI).

**Fix**: το parser τώρα ανέχεται ΟΠΟΙΕΣΔΗΠΟΤΕ κενές γραμμές ανάμεσα στο
`📊 τίτλος` και τον πίνακα (σαρώνει μπροστά μέχρι την πρώτη μη-κενή
γραμμή πριν ελέγξει αν είναι πίνακας). Επιβεβαιώθηκε headless: 0/1/2
κενές γραμμές → σωστό `chart` block και στις τρεις περιπτώσεις, τίτλος
χωρίς πίνακα καθόλου → σωστά παραμένει `heading` (καμία παλινδρόμηση).
Rendered DOM επιβεβαιωμένο επίσης (`.chart-bar` elements με σωστά πλάτη).

---

## ✅ Chart.js: πολυδιάστατα γραφήματα (18/08)

Ρητό αίτημα χρήστη - το 📊+table bar chart είναι σκόπιμα μονοδιάστατο
(ετικέτα → 1 αριθμός), δεν μπορεί να κάνει grouped/multi-series (π.χ.
"μήνας × πελάτης"). "Υπάρχει κάτι καλύτερο;" → ναι, **Chart.js**.

- **Chart.js v4.4.4, ΤΟΠΙΚΟ αντίγραφο** (`UI\web\vendor\chart.umd.min.js`,
  ~200KB, MIT) - ΟΧΙ CDN (το WebView2 μπορεί να μην έχει internet, ίδιο
  σκεπτικό με τα υπόλοιπα vendored dlls). `<script src="vendor/
  chart.umd.min.js">` στο `index.html`, `<Content>` entry στο `.csproj`
  (ίδιο pattern με το `index.html` - CopyToOutputDirectory + flatten Link).
- **Νέα σύμβαση, ΠΟΛΥ πιο robust από το 📊+table**: fenced code block
  ` ```chart ` με ΕΝΑ πλήρες JSON μέσα
  (`{type,title,labels,datasets:[{label,data}]}`) - `type`: bar/line/pie/
  donut. Ο parser (`CHARTJS_FENCE_RE`) απλά διαβάζει μέχρι το κλείσιμο
  ` ``` `, καμία ευαισθησία σε adjacency/κενές γραμμές (σε αντίθεση με το
  📊 - βλ. bugfix section πιο πάνω). Άκυρο JSON → δείχνει το raw κείμενο
  ΑΝΤΙ να χαθεί σιωπηλά.
- `buildChartJsConfig(spec)` - μεταφράζει το ΔΙΚΟ ΜΑΣ απλό schema σε
  πλήρες Chart.js config, διαβάζει τα ΙΔΙΑ CSS custom properties
  (`--text`/`--text-dim`) με το υπόλοιπο UI ώστε να ταιριάζει οπτικά.
- `mountPendingCharts(pendingCharts)` - τα `<canvas>` elements ΔΕΝ
  μπορούν να γίνουν πραγματικά Chart.js instances μέσα στο `blocksToHtml`
  (ακόμα string, όχι live DOM) - ο κάθε caller παίρνει πίσω το
  `pendingCharts` array και καλεί το `mountPendingCharts` ΑΦΟΥ εισάγει το
  html στο πραγματικό DOM. Fail-soft αν λείπει το `window.Chart` (δείχνει
  μήνυμα, δεν σκάει η σελίδα) - επιβεβαιωμένο headless (ΚΑΙ το happy path
  με mock `window.Chart`, ΚΑΙ το fail-soft όταν όντως έλειπε).
- **Κουμπωμένο σε ΟΛΑ τα render call sites** (ρητό αίτημα χρήστη - "να
  έχεις κατά νου και το Dashboard"): κύριο chat (`addMessage`), Email,
  Courier, Help, Browser composers, ΚΑΙ το Dashboard
  (`renderDashboardResult`/`upgradeTablesToCharts` - το ΗΔΗ υπάρχον
  Dashboard-only heuristic που αναβαθμίζει 2-στηλους πίνακες σε charts
  παραμένει, τώρα απλά ΚΑΙ αυτό μπορεί να επωφεληθεί από το `pendingCharts`
  wiring). ΕΚΤΟΣ σκοπίμα: PDF export path (`printArea`) και το task-modal
  "next action" summary - δεν έχει νόημα εκεί (print pipeline/απλό
  confirmation text αντίστοιχα).
- System prompt (`JarvisAgentClient.cs`) ενημερώθηκε: δύο επιλογές -
  (α) 📊+table για μία σειρά, (β) ` ```chart ` JSON για πολλαπλές σειρές/
  ομαδοποιημένα δεδομένα, με ρητό παράδειγμα σχήματος και προειδοποίηση
  να ΜΗΝ κάνει "Ιαν-FINIX"/"Ιαν-ΧΛΙΑΠΑΣ" ξεχωριστές ετικέτες (λάθος σχήμα)
  αλλά `labels`=μήνες + ξεχωριστό `dataset` ανά πελάτη.

---

## ✅ Ενοποίηση: μόνο Chart.js παντού, καμία παλιά βιβλιοθήκη (18/08)

Ρητό αίτημα χρήστη - "ένα graph object παντού", καθόλου χρήση της παλιάς
CSS-πλάτος βιβλιοθήκης πουθενά, ούτε στο Dashboard.

- **System prompts** (`JarvisAgentClient.cs` γενικό + `JarvisShell.xaml.cs`
  `BuildDashboardPrompt`): αφαιρέθηκε ΕΝΤΕΛΩΣ η οδηγία "📊 τίτλος" - ο
  Claude τώρα καθοδηγείται να χρησιμοποιεί ΑΠΟΚΛΕΙΣΤΙΚΑ ` ```chart ` JSON,
  ΑΚΟΜΑ ΚΑΙ για μονοδιάστατα δεδομένα (π.χ. τα 4 top-10 reports του
  Dashboard - ένα dataset αρκεί).
- **`upgradeTablesToCharts`** (Dashboard-only heuristic που αναβαθμίζει
  2-στηλους αριθμητικούς πίνακες σε γράφημα, ΑΝΕΞΑΡΤΗΤΑ από το τι διάλεξε
  ο Claude) - ενημερώθηκε να παράγει `{type:'chartjs', spec:{...}}` αντί
  για το παλιό `{type:'chart', header, rows}`.
- **Αφαιρέθηκε εντελώς ο παλιός μηχανισμός** (πλέον dead code, τίποτα δεν
  τον παρήγαγε): `CHART_RE`, το detection block του στο `parseAssistant`,
  η `chartBlockToHtml()`, το `.chart-block`/`.chart-title`/`.chart-row`/
  `.chart-bar-track`/`.chart-bar`/`.chart-value` CSS. Επιβεβαιώθηκε
  headless ότι `typeof chartBlockToHtml === 'undefined'` και
  `typeof CHART_RE === 'undefined'` μετά την αφαίρεση.
- **`blocksToExportRows`** ενημερώθηκε να ξέρει να εξάγει `chartjs`
  blocks σε Excel/CSV (1η στήλη = labels, μία στήλη ανά dataset) - το
  παλιό chart block είχε πάντα ένα ενσωματωμένο `header`/`rows` πίνακα
  ΓΙ' ΑΥΤΟ εξαγόταν "δωρεάν", το νέο `chartjs` block έχει μόνο
  `spec.labels`/`spec.datasets` - χρειάστηκε ρητή μετατροπή.
- **Γνωστή διαφορά συμπεριφοράς** (ρητά αποδεκτή, ρωτήθηκε ο χρήστης):
  το Dashboard ΔΕΝ δείχνει πια μόνιμα ορατό "πίνακα ακριβών τιμών" κάτω
  από το γράφημα (όπως έκανε η παλιά βιβλιοθήκη) - οι ακριβείς τιμές
  είναι διαθέσιμες μέσω Chart.js tooltip (hover) αντί για στατικό πίνακα.
  Πιο απλό/ομοιόμορφο, λιγότερη ενσωματωμένη πληροφορία-χωρίς-hover.

---

## ✅ Dashboard: Tasks tab πρώτο (18/08)

Ρητό αίτημα χρήστη μετά από αναφορά "ανεξήγητη καθυστέρηση" στην εμφάνιση
δεδομένων Dashboard - διερευνήθηκε (η μικρή λεζάντα ενημερωνόταν κανονικά,
η καθυστέρηση προϋπήρχε και με το παλιό chart mechanism, άρα ΔΕΝ ήταν
regression των σημερινών αλλαγών, αλλά προϋπάρχον χαρακτηριστικό του
Commercial tab: χρειάζεται πολλαπλά AI/`query_data` round-trips, αργό εκ
φύσεως, σε αντίθεση με το Tasks tab που είναι ΕΝΑ deterministic SQL
query). Λύση: αλλαγή ΣΕΙΡΑΣ εμφάνισης, όχι της ίδιας της καθυστέρησης.

- `openDashboard()`: καλεί ΠΡΩΤΑ `switchDashboardPage(1)` (Tasks - δείχνει
  ΑΜΕΣΩΣ, ίδιο μηχανισμό με κλικ στο tab, κάνει και το lazy-load αν είναι
  η πρώτη φορά) και ΜΕΤΑ `requestDashboardData()` (Commercial - τρέχει
  από πίσω, έτοιμο όταν ο χειριστής αλλάξει tab).
- Tα tabs (`.dashboard-tabs`, κάθετη λωρίδα - `flex-direction:column`,
  `writing-mode:vertical-rl` - "πάνω/κάτω" όχι "αριστερά/δεξιά") άλλαξαν
  ΣΕΙΡΑ ΣΤΟ DOM (Tasks πρώτο/πάνω, Commercial δεύτερο/κάτω) ώστε να
  ταιριάζει οπτικά με ποιο tab δείχνεται πρώτο - τα `data-dashboard-page`
  ΔΕΝ άλλαξαν (κάθε κουμπί δείχνει πάντα στη ΔΙΚΗ ΤΟΥ σελίδα, ανεξάρτητα
  από τη σειρά εμφάνισης των κουμπιών).
- Επιβεβαιώθηκε headless: DOM order σωστό, active tab = Tasks, correct
  `translateX`, ΚΑΙ τα δύο requests (`dashboard_get_my_tasks` πρώτο,
  `dashboard_query` δεύτερο) φεύγουν με τη σωστή σειρά.

---

## ✅ Άνοιγμα/δημιουργία συναλλασσόμενου μέσω ελεύθερης συζήτησης (18/08)

Ρητό αίτημα χρήστη - το `CREATEAADEAFM` ήταν ΜΟΝΟ deterministic εντολή με
σταθερή σύνταξη. "Θέλω να λειτουργεί και για πελάτες και για προμηθευτές
με ελεύθερη συζήτηση, να μπορεί ο Jarvis να ανοίγει συναλλασσόμενο."

- **3 νέα LLM tools** (`JarvisTools.cs`, διαθέσιμα στο ΓΕΝΙΚΟ chat, ΟΧΙ
  πίσω από courierMode/emailMode): `find_trader_by_afm` (wraps
  `ExecuteFindTraderByAfm`/`ExecuteFindTraderByAfmAndSodType`),
  `get_aade_data` (wraps το ήδη υπάρχον `ExecuteGetAadeData`),
  `create_trader_from_aade` (wraps το ήδη υπάρχον
  `ExecuteCreateTraderFromAade`) - ΚΑΜΙΑ δεύτερη λογική, ίδιες
  deterministic συναρτήσεις με το standalone CREATEAADEAFM/DR flow.
- **Entitlement (JARVISDOCREADER)**: ελέγχεται ΡΗΤΑ στο
  `JarvisAgentClient.ExecuteTool`, ΚΑΘΕ φορά που καλείται ΟΠΟΙΟΔΗΠΟΤΕ από
  τα 3 tools - ΔΕΝ υπάρχει προηγούμενο "start" gate σαν το courier/email
  (το γενικό chat δεν έχει ξεχωριστό "άνοιγμα κουρτίνας" βήμα). Ρητά
  σχεδιασμένο ΝΑ ΜΗΝ επαναλάβει το κενό που βρέθηκε νωρίτερα σήμερα στο
  standalone CREATEAADEAFM (`HandleDrManualLookupAsync` δεν έλεγχε καθόλου
  άδεια) - κανένα flag δεν εμπιστεύεται, έλεγχος ΣΤΟ ΣΗΜΕΙΟ ΧΡΗΣΗΣ.
- **Υποχρεωτική σειρά/επιβεβαίωση** (system prompt, ίδιο σκεπτικό με τα
  voucher tools νωρίτερα σήμερα): find_trader_by_afm πρώτα (αν βρεθεί,
  ΣΤΑΜΑΤΑ - δεν δημιουργεί ξανά) → αν ζητήθηκε δημιουργία και sodType
  άγνωστο, ρωτάει ❓/> Προμηθευτή/Πελάτη → get_aade_data, δείχνει στοιχεία,
  ζητά ΡΗΤΗ τελική επιβεβαίωση → ΜΟΝΟ μετά από "ναι" σε επόμενο μήνυμα,
  create_trader_from_aade.
- **Νέο `trader:OBJECTNAME:trdrId` inline link scheme** (`index.html`,
  `TRADER_LINK_RE`) - ΞΕΧΩΡΙΣΤΟ από το `doc:SOSOURCE:ID` των παραστατικών
  (trader records δεν έχουν SOSOURCE/FINDOC). Reuse ΑΥΤΟΥΣΙΟ το ήδη
  υπάρχον `open_trader` postCommand (φτιαγμένο πριν για το κουμπί
  "Άνοιγμα" του DR flow) - ΝΕΟ entry point (κλικαριστό link σε
  οποιοδήποτε chat κείμενο) στην ΙΔΙΑ, ήδη δουλεμένη λειτουργικότητα.
  Επιβεβαιώθηκε headless: σωστό parsing, doc: links χωρίς regression,
  σωστό `postCommand` στο κλικ.

---

## ✅ Browser mode: scraping δεδομένων (extract_page_tables, 18/08)

Ρητό αίτημα χρήστη - "scraping δεδομένων από ιστοσελίδες", με στόχο (1)
εμφάνιση/πίνακας μέσα στο chat, (2) εξαγωγή Excel/CSV, (3) σύγκριση με
εσωτερικά δεδομένα Soft1.

- **Νέο tool `extract_page_tables`** (μόνο σε browserMode, δίπλα στο
  `open_url`/`read_page_content`) - διαβάζει τα πραγματικά `<table>`
  elements της σελίδας ΜΕΣΑ στο browserView (DOM query, ΟΧΙ regex πάνω σε
  raw κείμενο σαν το `read_page_content` - πολύ πιο αξιόπιστο για
  πραγματικά tabular δεδομένα, δεν χάνεται η στοίχιση στηλών).
- **Δύο βήματα** (ίδιο σχεδιασμό με `get_courier_voucher_data` νωρίτερα
  σήμερα): χωρίς `tableIndex` → ΜΟΝΟ περίληψη όλων των πινάκων
  (index/rowCount/colCount/header), ώστε ο Claude να διαλέξει τον σωστό
  χωρίς να ξοδέψει context σε άσχετους/τεράστιους πίνακες (π.χ.
  navigation tables). Με `tableIndex` → πλήρη δεδομένα (header+rows,
  κομμένα στις πρώτες 200 γραμμές) μόνο για εκείνον.
- **Καμία νέα export/rendering λογική χρειάστηκε** - ο Claude ξαναγράφει
  το αποτέλεσμα σαν κανονικό markdown table στην απάντησή του, και
  παίρνει "δωρεάν" ΟΛΟ το ήδη υπάρχον μηχανισμό (rendering + Excel/CSV/
  PDF export toolbar). Σύγκριση με Soft1: ο Claude απλά καλεί ΚΑΙ
  `query_data` στην ΙΔΙΑ συζήτηση - κανένα ξεχωριστό "compare" tool, απλή
  σύγκριση δύο ήδη γνωστών συνόλων δεδομένων.
- **Fallback για μη-`<table>` σελίδες** (ρητή διόρθωση χρήστη 18/08: "θέλω
  να μπω σε μια σελίδα και να του πω βάλε μου σε πίνακα τα δεδομένα των
  ειδών που βλέπεις" - πολλά sites φτιάχνουν λίστες προϊόντων με `<div>`
  κάρτες, ΟΧΙ πραγματικό `<table>`): αν το `extract_page_tables` δεν
  βρει τίποτα, ο Claude ΔΕΝ σταματάει/αναφέρει απλά τον περιορισμό -
  καλεί ΑΜΕΣΩΣ `read_page_content` και χτίζει ΤΟΝ ΙΔΙΟ τον πίνακα από το
  ορατό κείμενο, αναγνωρίζοντας μόνος του τα επαναλαμβανόμενα "είδη" -
  ΙΔΙΟ τελικό αποτέλεσμα (κανονικό markdown table) με το
  extract_page_tables, ο χειριστής δεν χρειάζεται να ξέρει ποιο μονοπάτι
  χρησιμοποιήθηκε. Καμία νέα λειτουργικότητα χρειάστηκε - μόνο πιο ρητή
  οδηγία στο system prompt (και τα δύο tools ήδη υπήρχαν).
- Επιβεβαιώθηκε headless με fixture 2 πινάκων (ένα navigation table χωρίς
  πραγματικά δεδομένα + ένα πραγματικό price table με κενή γραμμή) - το
  navigation table σωστά αγνοήθηκε, η κενή γραμμή φιλτραρίστηκε, summary/
  detail modes σωστά.

---

## ✅ Export toolbar σε όλα τα composers + μεγαλύτερο read_page_content (18/08)

Ρητή αναφορά χρήστη: "δεν εμφανίζει κάτω τα εικονίδια για εξαγωγή σε
excel/csv" όταν το `extract_page_tables`/scraping έβγαζε πίνακα στην
κουρτίνα Browser. Αιτία: το `attachExportBar` (Excel/CSV/PDF κουμπιά +
"Επιλογή γραμμών") υπήρχε ΜΟΝΟ στο `addMessage` του κύριου chat - ΚΑΝΕΝΑ
από τα άλλα composers (Email/Courier/Help/Browser) δεν το καλούσε ποτέ,
από τότε που φτιάχτηκαν. Διορθώθηκε ΚΑΙ στα 4 (ίδιο θέμα ομοιομορφίας
με το Stop-κουμπί νωρίτερα σήμερα) - επιβεβαιώθηκε headless: export-bar
εμφανίζεται σε assistant μηνύματα με πίνακα, ΟΧΙ σε user μηνύματα, και
στα 4 composers.

Παράλληλα, ζωντανό παράδειγμα scraping (48 προϊόντα σε μια σελίδα)
έδειξε ότι το `read_page_content` έκοβε στο μισό (`MaxPageContentChars`
ήταν hardcoded 8000, ΧΩΡΙΣ δυνατότητα "συνέχειας"). Το όριο ήταν δικό
μας, αυθαίρετο - το πραγματικό context window του Claude είναι 1M
tokens. Έγινε **ParamCode 500025** (default 40000, 5x το παλιό όριο) -
βλ. `PARAMS.md` για πλήρη λίστα όλων των παραμέτρων cccParams.

---

## ✅ Export link "χανόταν" πίσω από κουρτίνες (18/08)

Ρητή αναφορά χρήστη: πατούσε Excel/CSV στο Browser, το αρχείο δημιουργούταν
κανονικά, αλλά ΔΕΝ εμφανιζόταν clickable link πίσω. Αιτία: το
`HandleExportAsync` έστελνε την επιβεβαίωση σαν ΩΜΟ κείμενο (`"✅
Αποθηκεύτηκε: [...](...)"`, ΟΧΙ JSON) - το γενικό fallback στο τέλος
του message listener (`index.html`) δρομολογεί ΚΑΘΕ μη-typed μήνυμα
ΠΑΝΤΑ στο ΚΥΡΙΟ chat (`addMessage`/`transcript`), ΑΝΕΞΑΡΤΗΤΑ από ποια
κουρτίνα (Browser/Email/Courier/Help) πραγματικά ζήτησε το export - το
link "χανόταν" πίσω από την ενεργή κουρτίνα.

**Fix**: το `source` ('main'/'browser'/'email'/'courier'/'help') ταξιδεύει
ΤΩΡΑ από το `attachExportBar`/`exportBlocks` (JS) → `{type:'export',
source,...}` → `HandleExportAsync` (C#) → πίσω σαν JSON `{type:
'export_result', source, text}` (ΟΧΙ πια ωμό string) → το JS `routeMap`
στο message listener καλεί το ΣΩΣΤΟ `add<Source>Message`. Επιβεβαιώθηκε
headless: κλικ στέλνει σωστό `source`, η απάντηση καταλήγει στο σωστό
transcript, το κύριο chat μένει ανέπαφο.

---

## ✅ "Επιλογή γραμμών" δεν έφτανε στον Jarvis σε δευτερεύοντα composers (18/08)

Ρητή αναφορά χρήστη (Browser): "επέλεξα κάποιες εγγραφές αλλά δεν το
κατάλαβε". Ίδια κατηγορία bug με το export-bar νωρίτερα σήμερα - το
`collectSelectedRowsMarkdown()`/`clearAllRowSelections()` (μηχανισμός
που μαζεύει τσεκαρισμένες γραμμές πίνακα και τις βάζει ΜΠΡΟΣΤΑ από το
μήνυμα που πάει στο backend, ώστε ο Claude να βλέπει ΤΙ ακριβώς εννοεί
ο χειριστής με "αυτές"/"τις επιλεγμένες") ήταν hardcoded στο κύριο
`#transcript` ΚΑΙ καλούνταν ΜΟΝΟ από το κύριο `send()` - τα άλλα 4
composers (Email/Courier/Help/Browser) είχαν οπτικά το checkbox UI
(reused CSS/`attachExportBar`), αλλά καμία σύνδεση με το backend.

**Fix**: και οι δύο συναρτήσεις δέχονται τώρα `transcriptEl` param
(default = κύριο `#transcript`, ΜΗΔΕΝ αλλαγή στο κύριο chat). Κάθε
`send<X>MessageText(text)` (Email/Courier/Help/Browser) μαζεύει τώρα τη
δική του επιλογή πριν στείλει, ίδιο idiom με το κύριο chat (`backendText`
≠ `displayText` - ο χειριστής βλέπει καθαρό κείμενο στη φούσκα του, ο
Claude λαμβάνει το prefix με τις επιλεγμένες γραμμές). Χρειάστηκε ΚΑΙ
`el.__jarvisBlocks = blocks` σε όλα τα composers (έλειπε - το
`collectSelectedRowsMarkdown` το χρειάζεται για να ξαναβρεί τα
πραγματικά δεδομένα του πίνακα, όχι μόνο το ορατό κείμενο του κελιού).

Επιβεβαιώθηκε headless στο Browser: επιλεγμένη γραμμή φτάνει σωστά στο
backend text, το αρχικό κείμενο διατηρείται, τα checkboxes καθαρίζουν
μετά την αποστολή.

---

## ✅ Systemic fix: delegated listeners μόνο στο κύριο chat (18/08)

Τρεις ρητές αναφορές χρήστη ζωντανά στο Browser, ΙΔΙΑ ρίζα και στις 3:
"δεν δούλευε το κουμπί Επιλογή όλων", "δεν εμφάνισε τις επιλεγμένες
εγγραφές ως thumbnail", "έφτιαξε link excel αλλά δεν ανοίγει με κλικ".

**Ρίζα**: 5 delegated event listeners (`.file-link` click, `.doc-link`
click - `open_document`/`open_trader`/`open_external_url`, `.rate-star`
click, `.row-select-all` change, `.quick-reply-btn` click) ήταν
hardcoded ΜΟΝΟ στο κύριο `#transcript` - παρόλο που το ΙΔΙΟ rendering
(`parseAssistant`/`blocksToHtml`/`renderInlineLinks`) παράγει τα ΙΔΙΑ
clickable elements ΚΑΙ στα 4 άλλα composers (Email/Courier/Help/
Browser). Κανείς δεν τα άκουγε εκεί - τα στοιχεία υπήρχαν οπτικά,
απλά "δεν έκαναν τίποτα" όταν πατιόντουσαν.

**Fix**: `ALL_TRANSCRIPTS` array, όλοι οι listeners bind σε ΟΛΑ πλέον.
Η **μία εξαίρεση** που χρειάστηκε ιδιαίτερη προσοχή: το
`.quick-reply-btn` καλούσε το `sendMessage` (κύριο chat) - αν το είχα
απλά bind-άρει "ίδιο handler σε όλα" όπως τα άλλα 4, ένα quick-reply
κουμπί μέσα στο Courier chat θα έστελνε τη λάθος συζήτηση/backend
conversation. Λύθηκε με πίνακα `[transcriptEl, sendFn]` ζευγαριών - το
ΣΩΣΤΟ `send<X>MessageText` ανά composer.

Παράλληλα, ΝΕΟ `appendSelectionChip`/`attachmentName` param στα 4
composer `add<X>Message` - το ίδιο 📎 chip ("N επιλεγμένες γραμμές") που
έδειχνε ήδη το κύριο chat όταν στέλνεις μήνυμα με τσεκαρισμένες γραμμές.

Επιβεβαιώθηκε headless: select-all λειτουργεί, file-link click σωστό,
selection chip εμφανίζεται, quick-reply στέλνει στη ΣΩΣΤΗ συζήτηση
(Browser, όχι κύριο chat) - ΚΑΙ regression check ότι το κύριο chat
παραμένει ανεπηρέαστο.

---

## ✅ Email: αποστολή/απάντηση (18/08) — ΕΚΚΡΕΜΕΙ ζωντανό test

Ρητό αίτημα χρήστη ("θα πρέπει να το βάλουμε να στέλνει email"). Ίδιο
mailbox με την ανάγνωση (`GetCurrentUserEmail` - ρητή απόφαση χρήστη
"ίδιο με αυτό που διαβάζει το inbox"), δύο ανεξάρτητα entry points στο
ΙΔΙΟ deterministic backend:

- **Backend** (`JarvisEmailAccess.cs`): `SendEmailAsync` (Graph
  `POST /users/{mailbox}/sendMail`) και `ReplyEmailAsync` (Graph
  `POST /users/{mailbox}/messages/{id}/reply` - πραγματικό reply με σωστό
  threading, ΟΧΙ νέο email με χειροκίνητο "RE:" prefix).
- **Chat tools** (`send_email`/`reply_email`, `JarvisAgentClient.cs`):
  διαθέσιμα στο γενικό chat, browserMode και emailMode. ΑΝΕΠΙΣΤΡΕΠΤΕΣ
  ενέργειες - υποχρεωτική ΡΗΤΗ επιβεβαίωση σε ΕΠΟΜΕΝΟ turn πριν την
  κλήση (ίδιο idiom με `create_trader_from_aade`/`create_courier_voucher`).
  Το `reply_email` δουλεύει και από ελεύθερη φράση όπως "απάντησε στον Χ
  ότι..." (ρητό παράδειγμα χρήστη) πάνω σε ήδη γνωστό email (`read_email`
  πρώτα αν χρειάζεται το `messageId`).
- **Βοήθεια σύνταξης** (ρητό αίτημα χρήστη, ΞΕΧΩΡΙΣΤΟ από την αποστολή):
  το system prompt διδάσκει ρητά ότι σύνταξη/βελτίωση διατύπωσης/τόνου/
  διόρθωση ορθογραφικών σε email είναι ΑΠΛΗ συζήτηση (ΚΑΝΕΝΑ tool) -
  πάντα διαθέσιμη, ανεξάρτητα από το αν το κείμενο τελικά θα σταλεί.
- **Κουμπιά UI (deterministic, ΧΩΡΙΣ Claude)** - ρητό αίτημα χρήστη ("τα
  tools θα πρέπει να δουλεύουν και μέσα από την κουρτίνα με κουμπιά...
  ακόμα και χωρίς εντολή"): "✎ Νέο email" στο toolbar της λίστας Email
  (compose modal: Προς/Θέμα/Κείμενο) και "↩ Απάντηση" στο πάνω δεξιά του
  detail modal (προσυμπληρώνει Προς/Θέμα από το ήδη ανοιχτό email, κλείνει
  πρώτα το detail modal). Η ίδια η φόρμα (συμπλήρωση + κλικ "Αποστολή")
  ΕΙΝΑΙ η επιβεβαίωση εδώ - καμία επιπλέον, σε αντίθεση με το chat tool
  path. Νέα `postCommand` types: `email_compose_send`/`email_reply_send`
  (JS→C#), `email_compose_result`/`email_reply_result` (C#→JS) - ΔΕΝ
  μπερδεύονται με το προϋπάρχον `email_reply` (αυτό είναι το ΓΕΝΙΚΟ
  "assistant text reply" type του email chat, άσχετο όνομα/συμπτωματικό).

**Χρειάζεται ακόμα (δεν μπορεί να γίνει από κώδικα):** το Azure AD App
Registration έχει σήμερα ΜΟΝΟ `Mail.Read` (Application permission). Για
να δουλέψει το send/reply χρειάζεται να προστεθεί ΚΑΙ `Mail.Send`
(Application permission) με admin consent - χρήστης το κάνει στο Azure
Portal.

**Verification**: μόνο headless (JS wiring/λογική compose-reply modal,
brace-balance check στα C# - `dotnet build` σε αυτό το sandbox σκάει σε
6 προϋπάρχοντα σφάλματα resolution WebView2/MSAL, ΑΣΧΕΤΑ με τις αλλαγές
εδώ). ΚΑΜΙΑ πραγματική κλήση Graph API δεν έχει δοκιμαστεί ζωντανά -
χρειάζεται πραγματικό Soft1 host + το νέο δικαίωμα.

**Εύρεση παραλήπτη από όνομα (18/08)**: αν ο χειριστής δώσει ΜΟΝΟ όνομα
στο `send_email`/`reply_email` (π.χ. "στείλε ένα μήνυμα στον Χ"), ο Jarvis
ψάχνει πρώτα `query_data` στο **PRSN** (βλ. [[soft1-prsn-contacts-table]]
memory - επιβεβαιωμένο ζωντανά από τον χρήστη: `PRSN.EMAIL`/`EMAIL1`,
ΧΩΡΙΣ έλεγχο εντός/εκτός εταιρίας, μόνο ότι το EMAIL είναι συμπληρωμένο)
πριν φτιάξει το draft - 1 match συνεχίζει, >1 ρωτάει ποιον εννοεί, 0 το
λέει καθαρά. Καμία νέα υποδομή - reuse του ήδη υπάρχοντος `query_data`.

---

## ✅ Contact finder: PRSN + Outlook → modal ("FNDPRSN", 18/08)

Ρητό αίτημα χρήστη - ξεχωριστό από το σιωπηλό name-lookup πιο πάνω: όταν
ο χειριστής **ρητά** ζητήσει να δει/βρει στοιχεία μιας επαφής (π.χ. "βρες
μου τα στοιχεία του Γιώργου Παπαδόπουλου"), ο Jarvis δείχνει τα ευρήματα
σε **modal** αντί για draft email. Χρήστης το ονόμασε "FNDPRSN" σαν idea
(δεν υπάρχει τέτοιο πραγματικό command στο Soft1 - απλά η αρχική σκέψη).

- **`show_contact_results`** (JarvisTools.cs) - ίδιο idiom "Claude
  υπολογίζει, το tool μεταφέρει" με `show_courier_documents`/
  `show_calendar_entries`: ο Claude ψάχνει PRSN μόνος του (`query_data`)
  και καλεί αυτό με τα ευρήματα - το tool ΔΕΝ ψάχνει τίποτα, μόνο
  πυροδοτεί το modal.
- **`search_outlook_contacts`** (JarvisEmailAccess.cs) - Graph
  `GET /users/{mailbox}/contacts` (`$search` + header `ConsistencyLevel:
  eventual`, όπως απαιτεί η τεκμηρίωση Graph για /contacts) -
  ΣΥΜΠΛΗΡΩΜΑΤΙΚΟ στο PRSN, ΟΧΙ αντικαταστάτης. Fail-graceful αν λείπει
  δικαίωμα - συνεχίζει με ό,τι βρέθηκε στο PRSN.
- Διαθέσιμα σε general/browserMode/emailMode (ίδιες branches με
  send_email/reply_email).
- **UI**: κοινό modal (`#contactResultsModalOverlay`, ΑΝΕΞΑΡΤΗΤΟ από ποιος
  composer το πυροδότησε) με κάρτα ανά επαφή (όνομα, badge Soft1/Outlook,
  email/τηλέφωνο/κινητό/διεύθυνση/θέση) + κουμπί **"✎ Νέο email"** ανά
  κάρτα που κλείνει το contact modal και ανοίγει το compose modal ήδη
  προσυμπληρωμένο (`openEmailComposeModal(prefillTo)` - επεκτάθηκε με
  προαιρετικό όρισμα, backward-compatible) - άμεση γέφυρα από το εύρημα
  στην αποστολή, χωρίς αντιγραφή/επικόλληση email.

**Χρειάζεται ακόμα**: το Outlook-σκέλος χρειάζεται `Contacts.Read`
(Application permission) στο ίδιο Azure AD App Registration - ΔΕΝ
υπάρχει ακόμα (ίδιο μπλοκάρισμα με το `Mail.Send`). Το PRSN-σκέλος
δουλεύει ανεξάρτητα, καμία εξάρτηση.

**Verification**: headless (render/open/close modal, per-card "✎ Νέο
email" action + prefill, empty-state) + brace-balance/compile check.
Καμία πραγματική κλήση Graph API δεν έχει δοκιμαστεί ζωντανά.

---

## ✅ Υπενθυμίσεις: Soft1 εργασία ή Outlook Calendar (18/08)

Ρητό αίτημα χρήστη: "θέλω να μπορώ ως χειριστής να βάζω υπενθυμίσεις.
Είτε στο Soft1 ως εργασίες, είτε στο Outlook Calendar."

- **Soft1 εργασία**: ΗΔΗ υπήρχε - `create_crm_task` (`JarvisTools.cs`)
  δέχεται `reminderDate`, γράφει `VACTRMND.RMDATE`. Καμία αλλαγή
  χρειάστηκε, απλά επιβεβαιώθηκε ότι καλύπτει το αίτημα.
- **Outlook Calendar** (ΝΕΟ): `create_outlook_event`
  (`JarvisEmailAccess.cs`) - Graph `POST /users/{mailbox}/events`, ίδιο
  `"GTB Standard Time"` idiom με το `GetCalendarEventsAsync`. **Πλήρες
  event** (ρητή επιλογή χρήστη) - διάρκεια, τοποθεσία, καλεσμένοι,
  υπενθύμιση λεπτών πριν. Χρησιμοποιεί το `Calendars.ReadWrite`
  permission που ο χρήστης είχε ΗΔΗ προσθέσει - καμία νέα Azure ρύθμιση.
- Διαθέσιμο σε general/browserMode/emailMode (ίδιες branches με
  send_email/reply_email/show_contact_results).
- **Confirmation gate**: ΑΝ έχει `attendees` (καλεσμένοι) -
  ΑΝΕΠΙΣΤΡΕΠΤΗ ενέργεια (πραγματικές προσκλήσεις email) - ΙΔΙΟΣ κανόνας
  με send_email (δείξε draft, περίμενε ρητή επιβεβαίωση σε επόμενο turn).
  ΧΩΡΙΣ attendees (προσωπική υπενθύμιση) - καλείται ΑΠΕΥΘΕΙΑΣ, ίδιο με
  το create_crm_task.
- Name-to-email resolution για καλεσμένους ξαναχρησιμοποιεί την ΙΔΙΑ
  λογική (PRSN/search_outlook_contacts) με το send_email.
- System prompt: αν ο χειριστής δεν διευκρινίσει Soft1 vs Outlook, ο
  Jarvis ΡΩΤΑΕΙ - δεν μαντεύει.

**Verification**: brace-balance/compile check μόνο (καθαρό chat tool,
καμία αλλαγή UI - όπως το create_crm_task, δεν χρειάζεται δικό του
κουμπί). Καμία πραγματική κλήση Graph API δεν έχει δοκιμαστεί ζωντανά.

---

## ✅ Δημιουργία είδους - v1 "απλό" tier (18/08)

Ρητό αίτημα χρήστη ("άνοιγμα ειδών ... εδώ θέλει ανάλυση") - νέο αρχείο
`Core/JarvisItems.cs`. Ο χρήστης περιέγραψε ΔΥΟ tiers:
- **(α) Απλό** - ΜΟΝΟ τα απαραίτητα πεδία, όλα τα υπόλοιπα αντιγράφονται
  από ΠΑΡΟΜΟΙΟ υπάρχον είδος στο μητρώο. **ΑΥΤΟ χτίστηκε τώρα (v1).**
- **(β) Πολύπλοκο** - όσο το δυνατόν περισσότερα πεδία, με web-search
  συμπλήρωση όταν ο χειριστής δεν ξέρει κάτι, και δυναμικό mapping σε
  υποπίνακες για "πινακοποιημένα" πεδία. **ΕΚΤΟΣ σκοπείου αυτής της
  φάσης** - ρητά αναφέρθηκε στο chat ότι δεν έχει χτιστεί ακόμα.

**Ρητές αποφάσεις χρήστη (18/08):**
- "Μερίδα είδους" = απλά μία νέα εγγραφή `MTRL`, κανένα ξεχωριστό
  concept/πίνακας.
- **ΑΓΝΟΟΥΜΕ τα `ccc*` custom πεδία** (Jetoil-specific) - στάνταρ Soft1
  MTRL πεδία μόνο, γενικός σχεδιασμός (ρητή υπενθύμιση χρήστη "μην
  στέκεσαι στην Jetoil μόνο").
- **Designer Object: `"ITEM"`, SODTYPE=51** (ρητή πληροφορία χρήστη) -
  ΙΔΙΟ idiom με το `TRDR`/`TraderObjectsBySodType` - το SODTYPE ΔΕΝ
  μπαίνει χειροκίνητα σε `Current[...]`, είναι inherent στο ίδιο το
  `CreateModule("ITEM")`.
- **CODE**: προτείνεται μόνο του, ΙΔΙΑ λογική με
  `JarvisTools.SuggestNextTraderCode` (MAX+1, zero-padding heuristic) -
  επεξεργάσιμο, ΟΧΙ σιωπηλή ανάθεση.
- Πάντα ρωτάει τον χειριστή: `CODE`(προτεινόμενο)/`NAME`/`MTRUNIT1`(ΜΜ)/
  `VAT`(ΦΠΑ)/`MTRACN`(Λογαριασμός). `MTRUNIT3`(ΜΜ αγορών)/`MTRUNIT4`(ΜΜ
  πώλησης) = default ίδιο με `MTRUNIT1`, αλλάζουν ΜΟΝΟ αν ζητηθεί ρητά.
  Προαιρετικά `PRICER`/`PRICEW`.
- **ΠΑΝΤΑ** ρωτάει (εκτός αν ήδη απαντήθηκε στο αρχικό prompt):
  `MTRLOTUSE` (Παρτίδα) / `MTRSNUSE` (Serial Number).
- Πρότυπο είδος: δοσμένο → ρωτάει ΜΟΝΟ τη νέα περιγραφή· όχι δοσμένο →
  ρωτάει αν υπάρχει· ούτε τότε δοθεί → ΜΙΑ ενιαία ερώτηση με όλα τα
  απαραίτητα πεδία μαζί.

**Δύο tools:**
- **`get_item_template`** (ΜΟΝΟ ανάγνωση) - διαβάζει τα whitelist πεδία
  ενός ήδη-εντοπισμένου (μέσω `query_data`) πρότυπου είδους + προτείνει
  επόμενο κωδικό. Το whitelist είναι το **ParamCode 500026** (comma-
  delimited στήλες `MTRL`, ΙΔΙΟ idiom με το `CarryOverFieldsByPhysicalTable`
  του DR feature - επέκταση = 1 αλλαγή στην παράμετρο, ΟΧΙ στον κώδικα).
- **`create_item`** (write, **ΥΠΟΧΡΕΩΤΙΚΗ επιβεβαίωση** πριν την κλήση -
  μόνιμη εγγραφή, ίδιος κανόνας με `create_trader_from_aade`/`send_email`)
  - `copiedFields` **επικυρώνεται server-side** έναντι του ΙΔΙΟΥ
    whitelist (500026) - ρητό fail (ΟΧΙ σιωπηλή παράλειψη) αν ο Jarvis
    προσπαθήσει να γράψει σε στήλη εκτός λίστας.
  - Duplicate-code check πριν το insert (`SELECT COUNT(*) FROM MTRL
    WHERE COMPANY=... AND CODE=...`) - ίδιο idiom με το TRDR
    duplicate-check.
  - Backend idiom **ίδιο ΑΚΡΙΒΩΣ** με `ExecuteCreateTraderFromAade`:
    `CreateModule("ITEM")` → `GetTable("MTRL")` → `InsertData()` → set
    `Current[...]` ανά πεδίο → `PostData()`.

Διαθέσιμο ΜΟΝΟ στο γενικό (default) tools branch - όχι
browserMode/emailMode/courierMode, ίδιο scope με τα trader-creation
tools.

**Verification**: brace-balance/compile check μόνο (νέο αρχείο, καμία
αλλαγή UI). Καμία πραγματική δημιουργία είδους δεν έχει δοκιμαστεί
ζωντανά.

**ΠΡΩΤΟ ζωντανό test 18/08 - δούλεψε, ΕΝΑ πραγματικό bug βρέθηκε+διορθώθηκε:**
ο κωδικός που ανέφερε ο Jarvis στον χειριστή ΔΕΝ ήταν αυτός που τελικά
αποθηκεύτηκε στο είδος - το `ITEM` object πιθανόν έχει auto-numbering
ενεργό στο Designer, το Soft1 αγνοεί/αντικαθιστά το `CODE` που στέλνουμε
κατά το `PostData()` με δικό του (ο χρήστης το περιέγραψε ως "τυχαίο
7ψήφιο νούμερο"). **Fix**: `ExecuteCreateItem` πλέον διαβάζει ΠΙΣΩ το
πραγματικό `CODE` από τη βάση (`SELECT CODE FROM MTRL WHERE MTRL=@id`)
ΜΕΤΑ το insert, ΑΥΤΟ επιστρέφεται (`code` field) - ΟΧΙ το αρχικό αίτημα
(τώρα `requestedCode`, ξεχωριστό πεδίο) - `codeChanged` flag αν διαφέρουν.
System prompt ενημερώθηκε να αναφέρει ΠΑΝΤΑ το `code` του αποτελέσματος,
ΠΟΤΕ το αρχικό draft, και να το λέει ΚΑΘΑΡΑ στον χειριστή αν άλλαξε.
**ΔΙΕΥΚΡΙΝΙΣΗ χρήστη μετά το fix**: η ΠΡΑΓΜΑΤΙΚΗ αιτία ΔΕΝ ήταν
auto-numbering στο Soft1 - ήταν ότι ο Jarvis, κατά το bulk import, χρησι-
μοποίησε ΣΙΩΠΗΛΑ τον κωδικό που διάβασε από τη στήλη "κωδικός" του ίδιου
του Excel αντί για τον προτεινόμενο διαδοχικό κωδικό Soft1
(`suggestedCode`) - χωρίς να ρωτήσει ποιο θέλει ο χειριστής. **Νέα
οδηγία στο system prompt** (bulk import flow, νέο βήμα 1): αν το αρχείο
έχει δική του στήλη κωδικού, ο Jarvis ΡΩΤΑΕΙ ΡΗΤΑ ΜΙΑ ΦΟΡΑ για ΟΛΟ το
batch ("κωδικοί αρχείου ή Soft1 αρίθμηση;") - ΠΟΤΕ δεν υποθέτει μόνος
του. Το DB read-back fix (πιο πάνω) παραμένει ΣΩΣΤΟ/χρήσιμο ανεξάρτητα -
είναι ΓΕΝΙΚΗ άμυνα ("ό,τι ζητήθηκε ≠ ό,τι αποθηκεύτηκε ΠΑΝΤΑ αναφέρεται
σωστά"), απλά δεν ήταν η ΡΙΖΙΚΗ αιτία εδώ.

**ΔΕΥΤΕΡΟ ζωντανό test 18/08 (Browser scrape -> bulk import, 5 είδη
"Πλαισίου")** - πέτυχε στο τέλος (οι κωδικοί πέρασαν ΑΚΡΙΒΩΣ όπως του
Πλαισίου, καμία αυτόματη αλλαγή - επιβεβαιώνει ΚΑΙ το προηγούμενο βήμα 1
δουλεύει σωστά), αλλά "Specified cast is not valid" σε κάθε προσπάθεια
που περιλάμβανε τα copied fields (MTRGROUP/MTRTYPE/MTRTYPE1/SOCURRENCY)
ή τιμές (PRICER/PRICEW) - ο Claude αντιδραστικά τα παρέλειπε και
ξαναδοκίμαζε, οπότε τα είδη δημιουργήθηκαν ΧΩΡΙΣ ομάδα/τύπο/νόμισμα/
τιμές. **Ρίζα**: classic .NET boxing/unboxing mismatch - `JToken.
ToObject<object>()` δίνει ΠΑΝΤΑ `long` για ακέραιους (Json.NET default)
ΑΣΧΕΤΑ με την πραγματική SQL στήλη (smallint/int), και το input parsing
των τιμών έδινε `double` - το Softone XTable indexer κάνει ΑΥΣΤΗΡΟ
unboxing cast εσωτερικά (`(int)`/`(float)`) που ΣΚΑΕΙ αν το boxed CLR
type δεν ταιριάζει ΑΚΡΙΒΩΣ (C# δεν επιτρέπει unboxing σε "συμβατό" αλλά
διαφορετικό τύπο). **Fix**: `Convert.ToInt32`/`Convert.ToSingle`
(πραγματική μετατροπή τιμής, ΟΧΙ unboxing) πριν το
`MTRL.Current[...] = value`. Το `float` για PRICER/PRICEW είναι η
καλύτερη τεκμηριωμένη εκτίμηση (ΟΧΙ 100% επιβεβαιωμένη ζωντανά ακόμα) -
αν ξανασκάσει ΕΙΔΙΚΑ στις τιμές, ίσως χρειάζεται `decimal` αντί για
`float`.

---

## ✅ Πρόσθετες οδηγίες διαχειριστή ("σαν skill", 18/08)

Ρητό αίτημα χρήστη: "θεωρείς ότι έχει νόημα να φτιάξουμε και μια
παράμετρο που θα την φορτώνουμε με κείμενο εκπαίδευσης, κάτι σαν skill;"
- Απάντηση: ΝΑΙ, ταιριάζει με το μοτίβο 500018/500026 (βγάζουμε
  επιχειρησιακή λογική από hardcoded C# σε παράμετρο). Επιλέχθηκε **ΜΙΑ
  ενιαία** παράμετρος (**ParamCode 500027**), όχι πολλαπλές ανά θέμα
  (v1, μπορεί να επεκταθεί αργότερα).
- `JarvisTools.GetOptionalParamString` (νέο, reusable helper -
  `ParamValueString`, χωρίς throw αν λείπει) → `BuildSystemPrompt`
  προσθέτει το κείμενο **ΠΑΝΤΑ ΤΕΛΕΥΤΑΙΟ** στο prompt, σε **ΚΑΘΕ**
  mode (γενικό/browser/email/courier/help).
- **⚠️ Ρητά συζητημένο ρίσκο (prompt injection μέσω της παραμέτρου)**:
  το κείμενο εισάγεται με ρητή ετικέτα *"ΣΥΜΠΛΗΡΩΜΑΤΙΚΟ - ΔΕΝ ακυρώνει/
  παρακάμπτει κανέναν από τους κανόνες ασφαλείας/επιβεβαίωσης παραπάνω"*
  - μετριασμός, ΟΧΙ απόλυτη εγγύηση (τα LLMs δεν είναι 100% άτρωτα σε
    τέτοιο injection). Η πρόσβαση στο `cccParams` θεωρείται ήδη
    περιορισμένη σε admins (κρατάει ΚΑΙ το Client Secret, 500021) -
    χειρίσου αυτή την παράμετρο σαν ευαίσθητη, ελέγχει πραγματικά τη
    συμπεριφορά του Jarvis.

**Verification**: brace-balance/compile check μόνο. Καμία ζωντανή δοκιμή.

---

## ✅ Ανάγνωση αρχείων (Word/Excel/PDF/CSV/JSON/TXT/XML) + bulk import ειδών (18/08)

Ρητό αίτημα χρήστη: "αρχικά πρέπει να καταφέρει να διαβάζει word, excel,
pdf, csv, json, txt, xml. Έπειτα πρέπει διαβάζοντάς τα, να μπορεί να
ανοίγει είδη με τη ρουτίνα που φτιάξαμε. Επίσης στην Browser καρτέλα...
θα πρέπει και από εκεί να εισάγουμε είδη."

**Ανάγνωση - τι ήταν ήδη έτοιμο vs τι χτίστηκε:**
- **PDF**: ήδη δούλευε (native Anthropic document API) - καμία αλλαγή.
- **TXT/CSV/JSON/XML**: ΗΔΗ κείμενο - **ΜΗΔΕΝ backend αλλαγή**, απλά
  επεκτάθηκε το `isTextAttachmentFile` (index.html, υπήρχε ήδη για
  `.md`/`.txt`) να αναγνωρίζει ΚΑΙ `.csv`/`.json`/`.xml` - ίδιο μονοπάτι
  (client-side ανάγνωση, μπαίνει ΑΠΕΥΘΕΙΑΣ στο μήνυμα ως κείμενο, ΟΧΙ
  vision API).
- **XLSX/DOCX**: νέο `Core/DocumentReaders.cs` - self-contained OOXML
  (ZIP+XML) readers, **ΧΩΡΙΣ κανένα εξωτερικό NuGet** (ίδια φιλοσοφία με
  το `XlsxWriter.cs`, αντίστροφα - READ). `ReadXlsxAsText` (sharedStrings
  + workbook rels + ανά sheet, "Φύλλο: Χ" markdown-style output),
  `ReadDocxAsText` (παράγραφοι + πίνακες). Γνωστοί περιορισμοί
  τεκμηριωμένοι στον κώδικα: formulas ΜΟΝΟ cached τιμή (ΟΧΙ re-eval),
  ημερομηνίες ΩΣ Excel serial numbers (όχι formatted), DOCX ΧΩΡΙΣ
  headers/footers/εικόνες.
- **Legacy .xls/.doc** (pre-2007 binary): **ΡΗΤΑ ΕΚΤΟΣ σκοπείου** - ρητό
  φιλικό μήνυμα ("τελείως διαφορετική δυαδική μορφή"), ΟΧΙ σιωπηλή
  αποτυχία/crash.
- **Ροή**: JS διαβάζει το .xlsx/.docx ως base64 (ίδιο idiom με vision
  attachments), νέο `postCommand` type `read_office_document` -> C#
  (`JarvisShell.HandleReadOfficeDocument`, sync/deterministic) αποκωδικο-
  ποιεί + τρέχει το parser + επιστρέφει ΚΑΘΑΡΟ κείμενο -> JS το βάζει σε
  `pendingAttachment` **ΑΚΡΙΒΩΣ όπως ένα text attachment** (ίδιο
  `isText:true` σχήμα) - μηδενική διπλή λογική στο send()/backendText.

**Bulk import ειδών** (ρητή απόφαση χρήστη - live progress, ΧΩΡΙΣ preview
οθόνη, report στο τέλος):
- **ΜΙΑ ενιαία επιβεβαίωση για ΟΛΟ το batch** (όχι ανά είδος, ρητή
  απόφαση χρήστη - "όταν είναι bulk import εννοείται ότι η αρχική
  επιβεβαίωση αφορά όλα τα είδη του batch").
- **Ζωντανή εξέλιξη**: ήδη υπήρχε ο μηχανισμός (progress-caption ανά tool
  call, thinking-caption ανά reasoning βήμα) - απλά διδάχτηκε στο system
  prompt να το εκμεταλλεύεται (σύντομη πρόταση πριν κάθε
  `get_item_template`/`create_item`).
- **Συνέχεια παρά τα λάθη**: ήδη υπήρχε (`catch`/`isError` γύρω από κάθε
  tool call στο `AskAsync` loop) - ένα αποτυχημένο `create_item` (π.χ.
  διπλός κωδικός) ΔΕΝ σταματάει το batch.
- **Τελικό report**: νέα ρητή οδηγία system prompt.
- **`get_item_template`/`create_item`** προστέθηκαν ΚΑΙ στο `browserMode`
  tools array (πριν ήταν ΜΟΝΟ στο γενικό chat) - για το Browser-tab
  σκέλος (scrape σελίδας -> εισαγωγή ειδών).
- **`MaxIterations=14` ήταν ανεπαρκές** για πολλά είδη σε ΕΝΑ μήνυμα (π.χ.
  50 γραμμές = πιθανόν 50-100 tool calls) - νέο προαιρετικό `maxIterations`
  όρισμα στο `AskAsync` (default = ίδιο ΑΚΡΙΒΩΣ με πριν, ΚΑΜΙΑ αλλαγή σε
  κανένα υπάρχον call site εκτός από τα 2 παρακάτω), τροφοδοτημένο από
  **νέο ParamCode 500028** (default 40) - εφαρμόζεται ΜΟΝΟ στο γενικό chat
  και στο Browser mode (τα 2 σενάρια bulk import). ΧΩΡΙΣ κόστος για
  κανονικές συζητήσεις (το όριο είναι οροφή ασφαλείας, όχι κάτι που
  "καταναλώνεται" σε κανονική χρήση - το loop ήδη σταματάει νωρίς όταν
  τελειώσει η δουλειά).

**Verification**: headless (extension-classification, `loadOfficeAttachment`
postCommand shape, simulated result-handling incl. stale-result guard) +
brace-balance/compile check. Καμία πραγματική ανάγνωση .xlsx/.docx ή bulk
δημιουργία ειδών δεν έχει δοκιμαστεί ζωντανά.

---

## ✅ Preview επισυναπτόμενου αρχείου - πίνακας/περίληψη (18/08)

Ρητό αίτημα χρήστη: "θέλουμε ένα Preview μικρό από αυτό που ανεβάζει ο
χειριστής, ας πούμε 30 γραμμές σε έναν πίνακα" - με επέκταση μετά από
συζήτηση: "όπου μπορεί [να βγάλει πίνακα] να το κάνει, όπου δεν μπορεί
να εξηγεί τον λόγο... και να αναμένει οδηγίες" (Jarvis-side, ΟΧΙ μόνο UI).

**Δύο επίπεδα:**
1. **Client-side, instant preview** (`index.html`, νέο `.attachment-preview`
   κάτω από το `.attachment-chip`) - `detectTabularPreview(text)`: heuristic
   delimiter detection (" | " από το δικό μας XLSX/DOCX-table output, ή ","
   για CSV) πάνω στις πρώτες 30 γραμμές - ΑΓΝΟΕΙ τα "### Φύλλο: Χ" section
   markers (δείχνει preview ΜΟΝΟ του πρώτου φύλλου/section). Χρειάζεται
   ΣΤΑΘΕΡΟΣ αριθμός στηλών στο ≥60% των γραμμών, αλλιώς ΔΕΝ το θεωρεί
   πίνακα (αποφεύγει false positives σε ελεύθερο κείμενο με τυχαία κόμματα).
   Αν αναγνωριστεί: πραγματικό `<table>` (header + έως 29 data rows). Αν
   όχι: σύντομο snippet (6 πρώτες γραμμές) + σημείωση ότι ο Jarvis θα
   περιγράψει τι διάβασε. ΚΑΜΙΑ κλήση Claude - καθαρή client-side λογική,
   instant, μηδενικό κόστος. Εφαρμόζεται ΜΟΝΟ σε text/office attachments
   (`isText:true`) - vision attachments (εικόνα/PDF) έχουν ήδη δικιά τους
   thumbnail προεπισκόπηση, ανεπηρέαστη.
2. **Jarvis-side, στην πρώτη απάντηση** (system prompt, `BuildSystemPrompt`)
   - όταν το μήνυμα ξεκινάει με το γνωστό attachment-prefix ("[Ο χειριστής
   επισύναψε το αρχείο...]"), ο Jarvis ΠΡΩΤΑ περιγράφει σύντομα τι διάβασε
   (πόσες γραμμές/είδη αν μοιάζει με πίνακα, ή γιατί ΔΕΝ μοιάζει + σύντομη
   περίληψη αν όχι) και ΠΕΡΙΜΕΝΕΙ οδηγίες - ΕΚΤΟΣ αν ο χειριστής ΗΔΗ έδωσε
   σαφή οδηγία ΣΤΟ ΙΔΙΟ μήνυμα (τότε προχωράει κατευθείαν, π.χ. στο bulk
   import flow πιο πάνω).

**Verification**: πλήρες headless (μέσω τοπικού HTTP server ως workaround -
το `file://` preview είχε πρόβλημα εργαλείου εκείνη τη στιγμή) -
`detectTabularPreview` σε CSV/XLSX-δύο-φύλλα/ελεύθερο-κείμενο/μονή-γραμμή/
κενό/ασυνεπείς-στήλες (όλα σωστά), πλήρες `showAttachmentChip`/
`clearAttachment` integration (πίνακας για CSV, σημείωση για ελεύθερο
κείμενο, καθαρισμός σε clear, ΚΑΜΙΑ εμφάνιση σε vision attachment) +
brace-balance/compile check. Το system-prompt σκέλος (Jarvis-side
περιγραφή) δεν έχει δοκιμαστεί ζωντανά.

---

## ✅ Performance review + agent-clustering restructuring (19/08)

Ρητό αίτημα χρήστη ("θέλω να είσαι αυστηρός... παρατηρώ σημαντική
καθυστέρηση στην επεξεργασία request προς την εκτέλεση... Θέλω να το
δούμε ολιστικά και αν χρειάζεται να κάνουμε restructure"), με ρητή
απόρριψη πυροσβεστικής (band-aid) λύσης.

**Ευρήματα του review:**
- `BuildSystemPrompt` είχε φτάσει ~95KB/~930 γραμμές, ΟΛΟΚΛΗΡΟ
  unconditional (ΕΝΑ statement, ΧΩΡΙΣ κανένα mode-check) - ΚΑΘΕ request,
  ΑΚΟΜΑ και Courier/Browser/Help (πριν καν ελεγχθεί ποιο mode είναι
  ενεργό), πλήρωνε το ΠΛΗΡΕΣ κόστος επεξεργασίας του, ασχέτως αν το
  request χρειαζόταν έστω και ένα κομμάτι του.
- ΚΑΝΕΝΑ prompt caching (`grep -c cache_control` = 0) - κάθε iteration
  (έως 40 σε bulk import) ξαναεπεξεργαζόταν το ΙΔΙΟ system prompt +
  tools από την αρχή.
- Self-inflicted regression: η ανάγνωση της παραμέτρου 500027 (extra
  instructions) γινόταν ΜΕΣΑ στο `BuildSystemPrompt` - δηλαδή ΕΝΑ SQL
  round-trip ΑΝΑ iteration αντί για μία φορά πριν το loop (σε αντίθεση
  με το ΗΔΗ σωστό μοτίβο του `reportDecimalPlaces`).

**Αρχιτεκτονική απόφαση** (μετά από συζήτηση - βλ. git log): "agent
clustering" - split του μονολιθικού unconditional prompt σε μικρό πάντα-
ενεργό πυρήνα ("Atlas") + domain-specific blocks πίσω από νέα
`itemMode`/`traderMode` flags (ΙΔΙΟ μοτίβο με το ήδη υπάρχον
`emailMode`/`courierMode`), με lightweight keyword router (`RouteMainChatAgent`,
ΧΩΡΙΣ επιπλέον LLM call) για το ελεύθερο κύριο chat + "sticky" tracking
(`_lastMainChatMode` στο JarvisShell) ώστε ασαφή follow-up μηνύματα να
ΜΗΝ χάνουν το ενεργό domain. Εσωτερικά μόνο codenames (ΠΟΤΕ ορατά στον
χειριστή - ο Jarvis παραμένει το ΜΟΝΟ όνομα/persona που βλέπει κανείς):
**Atlas** = general/core, **Forge** = item (`itemMode`), **Compass** =
trader (`traderMode`), **Echo** = email (υπάρχον `emailMode`, τώρα και
routable από το κύριο chat), **Sprint** = courier (υπάρχον, αμετάβλητο),
**Scout** = browser (υπάρχον, αμετάβλητο), **Sage** = help (υπάρχον,
αμετάβλητο), **Codex** = DR/Document Reader (αρχιτεκτονικά ΗΔΗ ξεχωριστό
- δικές του standalone vision-extraction κλήσεις, ΔΕΝ περνάει καθόλου
από `AskAsync`/`BuildSystemPrompt`, άρα δεν χρειάζεται routing).

**Τι έγινε:**
1. SQL-call hoist: το `extraInstructions` διαβάζεται ΜΙΑ φορά πριν το
   loop (ίδιο μοτίβο με `reportDecimalPlaces`), περνάει ως παράμετρος
   στο `BuildSystemPrompt`.
2. Prompt caching: `system` πλέον array με `cache_control: {type:
   "ephemeral"}` breakpoint στο τέλος του κειμένου, ΚΑΙ breakpoint στο
   ΤΕΛΕΥΤΑΙΟ tool definition (`ToolsWithCacheBreakpoint`) - cache-άρει
   όλο το prefix (system + tools) που είναι ΙΔΙΟ σε διαδοχικά iterations
   του ΙΔΙΟΥ turn.
3. `BuildSystemPrompt`: το πρώην ΕΝΑ giant unconditional statement
   σπάει σε: πυρήνα (persona/schema/chart/γενικοί κανόνες + λογαριασμός/
   ledger formatting + καταχώρηση παραγγελίας) ΠΑΝΤΑ ενεργό, `if
   (traderMode)` (άνοιγμα συναλλασσόμενου με ΑΦΜ), `if (itemMode)`
   (άνοιγμα/bulk import ειδών), `if (emailMode)` (email + εύρεση επαφής
   + υπενθυμίσεις Outlook - ΝΕΟ δεύτερο block, μαζί με το ΗΔΗ υπάρχον
   emailMode block πιο κάτω). Το `"Τρέχον context: Company=...
   Branch=..."` μετακινήθηκε ώστε να παραμένει ΠΑΝΤΑ τελευταίο/
   unconditional, ανεξάρτητα ποια blocks ενεργοποιήθηκαν.
4. Tools ternary: νέα `itemMode`/`traderMode` branches (ΜΟΝΟ τα σχετικά
   tools το καθένα) - το παλιό "default" (Atlas) bucket, που είχε ΟΛΑ
   τα email/contact/outlook/trader/item tools μαζί, μαζεύτηκε σε ΜΟΝΟ
   `query_data`/`export_query_to_file`/`open_document`/
   `get_conversion_targets`/`create_crm_task`/`create_order`.
   Courier/Browser/Help/emailMode's ΗΔΗ σωστά scoped tools ΔΕΝ
   πειράχτηκαν.
5. `RouteMainChatAgent` (static, `JarvisAgentClient.cs`): v1 keyword
   heuristic, ΣΚΟΠΙΜΑ compound φράσεις (π.χ. "άνοιξε είδος", "άνοιξε
   πελάτη", "στείλε email") αντί μεμονωμένων λέξεων όπως "πελάτης"/
   "είδος" που εμφανίζονται συχνά ΚΑΙ σε απλά reporting ερωτήματα (π.χ.
   "δείξε μου τα είδη με χαμηλό απόθεμα" δεν πρέπει να ενεργοποιεί
   `itemMode`). Ασαφές/κανένα σήμα → sticky στο προηγούμενο mode
   (`routingHint`) → αλλιώς "general" (Atlas).
6. `AskAsync`: νέες προαιρετικές παράμετροι `routingHint`/`onModeChosen`
   - καλούνται ΜΟΝΟ όταν ΚΑΝΕΝΑ από τα helpMode/browserMode/emailMode/
   courierMode δεν είναι ενεργό (δηλ. ΜΟΝΟ το ελεύθερο κύριο chat -
   καμία κουρτίνα δεν χρειάζεται routing, ΗΔΗ ξέρει το mode της ρητά).
   `JarvisShell.xaml.cs`: νέο `_lastMainChatMode` field, περνιέται ως
   `routingHint`/αποθηκεύεται μέσω `onModeChosen` στο κύριο chat call
   site.

**Deferred (ρητά, καταγεγραμμένα ως tasks):** multi-provider AI (#65,
"έχουμε καιρό να φτάσουμε στο 4, τα 1,2,3 πρώτα"), "Architect mode" -
προνομιακός ρόλος με κωδικό επιβεβαίωσης σε cccParams, παρακάμπτει
όλες τις υποχρεωτικές επιβεβαιώσεις (#66, εξαρτάται από την ύπαρξη
αυτής της αρχιτεκτονικής πρώτα).

**Verification**: brace-balance (164/164) + `dotnet build` καθαρό (ΜΟΝΟ
τα ΙΔΙΑ 6 προϋπάρχοντα WebView2/MSAL reference errors, άσχετα με τη
σημερινή αλλαγή - καμία νέα αποτυχία). ΚΑΝΕΝΑ ζωντανό test ακόμα - το
routing/latency effect χρειάζεται πραγματική εκτέλεση μέσα στο Soft1 για
να επιβεβαιωθεί (headless δεν μπορεί να μετρήσει πραγματικό response
time ούτε να δοκιμάσει τον router σε πραγματικές ερωτήσεις χρήστη).

### Μοντέλο AI ανά agent (συνέχεια, ίδια μέρα)

Ο χρήστης επεσήμανε ότι έλειπε το σημείο 3 από το αρχικό "1,2,3 τώρα, 4
deferred" (ποια μοντέλα ανά agent domain + μηχανισμός αλλαγής). Προτάθηκε
οικονομικός mapping (Atlas/Codex-deep=Opus, τα υπόλοιπα=Sonnet,
Sage/Codex-light=Haiku) - ο χρήστης διάλεξε ρητά το **συντηρητικό**:
**ΟΛΑ τα agents (Atlas/Forge/Compass/Echo/Sprint/Scout/Sage) default σε
`claude-opus-5`** (ΚΑΜΙΑ αλλαγή συμπεριφοράς σήμερα), tuning ανά domain
ΑΡΓΟΤΕΡΑ μόνο μέσω params, μετά από ζωντανή δοκιμή ποιότητας.

**Μηχανισμός**: `ResolveAgentModel(xSupport, agentName)` στο
`JarvisAgentClient.cs` - ένα ξεχωριστό `cccParams` ParamValueString ανά
agent (500029-500035, βλ. `PARAMS.md`), κενό/άγνωστο → fallback στο
σταθερό `Model` const. Υπολογίζεται ΜΙΑ φορά πριν το loop (ίδιο
σκεπτικό με `reportDecimalPlaces`/`extraInstructions` - ΟΧΙ SQL ανά
iteration). Το `activeAgentName` προκύπτει από τα ΙΔΙΑ mode flags που
ήδη καθορίζουν prompt/tools.

**Codex (DR) ΔΕΝ συμπεριλήφθηκε** - παραμένει hardcoded (Haiku για
`DetectDocumentIssuerAsync`, Opus για `ExtractDocumentLinesAsync`, ήδη
σωστά tuned εδώ και καιρό) γιατί αυτές οι δύο standalone μέθοδοι δεν
παίρνουν `xSupport` σήμερα - θα χρειαστεί μικρή signature αλλαγή αν
θελήσουμε να τις κάνουμε κι αυτές parametrizable (χαμηλή προτεραιότητα,
δεν είναι μέρος του latency-bloat προβλήματος).

**⚠️ Ανοιχτό σημείο, ρητά καταγεγραμμένο από τον χρήστη**: το `cccParams`
είναι per-company, ΟΧΙ per-Soft1-user - "είναι λίγο tricky γιατί οι
αλλαγές πρέπει να γίνονται per user". Αν χρειαστεί ποτέ tuning ΑΝΑ
χειριστή (όχι μόνο ανά εταιρία), θα χρειαστεί ΔΙΑΦΟΡΕΤΙΚΟΣ μηχανισμός
(π.χ. πίνακας προτιμήσεων ανά USERS, ή UI). Ρητά αφημένο ως έχει προς το
παρόν - ο χρήστης το γνωρίζει και θα το ξανακοιτάξουμε.

### Δύο ζωντανά bugs μετά το restructuring (ίδια μέρα)

1. **400 error** - `ToolsWithCacheBreakpoint` τύλιγε ΟΛΟ το `tools`
   array σαν ΕΝΑ στοιχείο ΝΕΟΥ array ("tools": [[...]] αντί για
   [...]) - το Anthropic API το απέρριπτε. Fix: return type `object`
   (ΟΧΙ `object[]`), επιστρέφει το `JArray` απευθείας.
2. **Router false negative** - "άνοιξέ μου έναν *νεο* είδος με πρότυπο
   το 1002" ΔΕΝ ενεργοποίησε το `itemMode` (ο Jarvis απάντησε λάθος ότι
   δεν υπάρχει "νέα εγγραφή με πρότυπο" λειτουργία - hallucination,
   γιατί δεν είχε το `create_item`/`get_item_template` tool). Ρίζα: η
   πρώτη έκδοση του `RouteMainChatAgent` έψαχνε ΑΚΡΙΒΕΙΣ, ΤΟΝΙΣΜΕΝΕΣ,
   ΓΕΙΤΟΝΙΚΕΣ φράσεις (π.χ. "άνοιξε είδος") - ο χρήστης έγραψε "νεο"
   ΧΩΡΙΣ τόνο ΚΑΙ οι λέξεις δεν ήταν γειτονικές ("άνοιξε **μου έναν**
   νεο είδος"). Fix: `StripGreekAccents` (αφαιρεί τόνους πριν τη
   σύγκριση, ΚΑΙ στο κείμενο ΚΑΙ στα keywords) + αντιστοίχιση σε
   επίπεδο ΛΕΞΗΣ/stem οπουδήποτε στη φράση (ρήμα-δημιουργίας +
   domain-ουσιαστικό, ΟΧΙ γειτονικά/exact phrase) αντί για άκαμπτες
   ολόκληρες φράσεις. Παραμένει συντηρητικό στα false positives
   (item/trader ΑΠΑΙΤΟΥΝ ρήμα+ουσιαστικό συνδυασμό, ΟΧΙ standalone
   "πελάτης"/"είδος").
   - **Παράπλευρο εύρημα κατά τη διόρθωση**: το routed "Echo" (γενικό
     chat → emailMode) ΘΑ έδινε στον Claude και τα
     `filter_email_inbox`/`filter_calendar`/`show_calendar_entries`
     tools - αυτά καλούν callbacks (`onFilterEmailInbox` κλπ) που είναι
     `null` ΕΚΤΟΣ της πραγματικής Email κουρτίνας. Δεν θα έκανε crash
     (τα callbacks είναι `?.Invoke`, no-op) αλλά ο Claude θα ανέφερε
     ψευδώς επιτυχία ("ενημέρωσα το ημερολόγιο") χωρίς να αλλάξει τίποτα
     ορατά. Fix: νέο `isEmailCurtain` flag (captured πριν το routing)
     διαχωρίζει πλήρες tools set (πραγματική κουρτίνα) από trimmed set
     (routed Echo από γενικό chat, ΧΩΡΙΣ τα 3 callback-εξαρτημένα tools -
     το `read_calendar` μένει, δεν χρειάζεται callback).
3. **Router false negative #2 (ομόηχα)** - "**στήλε** ένα email στον
   Κωνσταντίνο Μυλωνά" ΔΕΝ ενεργοποίησε το `emailMode` (ο Jarvis
   απάντησε λάθος ότι δεν έχει διαθέσιμο εργαλείο αποστολής). Ρίζα:
   ο χρήστης έγραψε "στήλε" (με η) αντί για το ορθό "στείλε" (με ει) -
   στα Νέα Ελληνικά τα **η/ι/υ/ει/οι/υι προφέρονται όλα ίδια** ("ι"),
   πολύ συνηθισμένο typo/παραλλαγή γραφής. Η απλή αφαίρεση τόνων
   (`StripGreekAccents`) ΔΕΝ αρκούσε - χρειαζόταν ΚΑΙ φωνητική εξίσωση.
   Fix: `StripGreekAccents` αντικαταστάθηκε από `NormalizeGreek` -
   ToLowerInvariant + αφαίρεση τόνων + "phonetic fold" (αι→ε, ει/οι/
   υι/η/υ→ι, ω→ο, με το "ου" προστατευμένο - δικός του ήχος, ΔΕΝ
   ταυτίζεται με "ο"/"ι"). Εφαρμόζεται ΚΑΙ στο κείμενο χρήστη ΚΑΙ σε
   κάθε keyword τη στιγμή της σύγκρισης (`ContainsAny`) - keyword
   literals παραμένουν κανονικά γραμμένα στον κώδικα (ευανάγνωστα),
   το normalize τα εξισώνει ούτως ή άλλως.
4. **Αρχιτεκτονικό κενό, ΟΧΙ απλό keyword bug** - ο χρήστης έστειλε
   ΕΠΙΤΗΔΕΣ "στείλε ένα **μήνυμα** στον Χ, πες του να με πάρει
   τηλέφωνο" - διφορούμενο ΕΠΙΤΗΔΕΣ ανάμεσα σε email ΚΑΙ CRM task
   (ανάθεση εργασίας/υπενθύμιση). Ο router (σωστά) δεν αναγνώρισε
   "email" (η λέξη "μήνυμα" δεν είναι "email"/"mail"), οπότε έδωσε το
   turn στο Atlas - ΑΛΛΑ το Atlas tools-bucket (χθεσινό restructuring)
   ΔΕΝ είχε καθόλου `send_email` πια, οπότε ο Jarvis ΔΕΝ μπορούσε να
   προτείνει τον δρόμο του email ΑΚΟΜΑ κι αν ήθελε - πρότεινε ΜΟΝΟ CRM
   task, σιωπηλά, χωρίς να υπάρχει καν επιλογή. Ο χρήστης το χαρακτήρισε
   σωστά: **δεν έπρεπε να διαλέξει σιωπηλά ΕΝΑΝ δρόμο - έπρεπε να
   ρωτήσει ποιον εννοεί.**
   - Fix (δύο μέρη): (α) `send_email`/`reply_email`/
     `show_contact_results`/`search_outlook_contacts` ΞΑΝΑΜΠΗΚΑΝ στο
     Atlas tools bucket - φθηνά σε tokens (μικρά schemas, ΚΑΙ cached),
     το πραγματικό κόστος που κόπηκε χθες ήταν το ΒΑΡΥ prompt ΚΕΙΜΕΝΟ
     (emailMode block), ΟΧΙ τα ίδια τα tool ορίσματα. (β) νέος
     unconditional κανόνας στο core prompt ("ΔΙΦΟΡΟΥΜΕΝΟ ΑΙΤΗΜΑ
     ΕΠΙΚΟΙΝΩΝΙΑΣ") - λέξεις όπως "μήνυμα"/"ενημέρωσέ τον"/"πες του"
     είναι εγγενώς διφορούμενες, ο Jarvis ΠΡΕΠΕΙ να ρωτήσει ΠΟΙΟΝ από
     τους δύο δρόμους (email ή CRM task) εννοεί, ΠΡΙΝ προχωρήσει σε
     οποιοδήποτε από τα δύο tools.
   - **Μάθημα για το routing μοντέλο γενικότερα**: όταν ο router δεν
     είναι σίγουρος (χαμηλή εμπιστοσύνη/ασαφές σήμα), το σωστό ΔΕΝ
     είναι να περιορίσει σιωπηλά τα tools στο πιο "ασφαλές" subset -
     είναι να αφήσει αρκετή επιφάνεια (tools) ώστε ο Jarvis να μπορεί
     να ΠΑΡΟΥΣΙΑΣΕΙ την αμφισημία στον χειριστή αντί να την κρύψει.

### Router: ένωση (union) tools για γνήσια compound requests (ίδια μέρα)

Ο χρήστης γενίκευσε το παραπάνω σε αρχιτεκτονική αρχή: "πρέπει να έχει
μια λίστα ανά agent με τα skill set... όταν δεν μπορεί να αποφασίσει ο
ίδιος να δίνει όλα τα παρεμφερή skills των agents ως επιλογές". Αρχικά
γενίκευσα ΥΠΕΡΒΟΛΙΚΑ (ένωση ΚΑΘΕ φορά που 2+ σήματα χτυπούσαν, ό,τι κι
αν ήταν) - ο χρήστης σωστά αντέδρασε: "τι σχέση μπορεί να έχει η
αποστολή μηνύματος με δημιουργία είδους και πελάτη" - item/trader ΔΕΝ
έχουν καμία εννοιολογική σχέση με "στείλε μήνυμα", δεν πρέπει να
ενώνονται επειδή "δεν ξέρουμε".

**Διευκρινισμένο σκεπτικό**: η ένωση ΔΕΝ είναι για να λύσει ασάφεια
ΝΟΗΜΑΤΟΣ (αυτό λύθηκε ήδη παραπάνω - Atlas έχει πάντα send_email+
create_crm_task μαζί) - είναι για ΓΝΗΣΙΑ compound requests, όπου το ΙΔΙΟ
κείμενο ζητάει ρητά ΠΕΡΙΣΣΟΤΕΡΑ ΑΠΟ ΕΝΑ πράγματα (π.χ. "άνοιξε πελάτη Χ
ΚΑΙ ένα νέο είδος Υ" - δύο ξεχωριστά, συγκεκριμένα αιτήματα). Χωρίς
ζωντανό bug σε αυτό το σενάριο ακόμα (προληπτικό, ο χρήστης το κράτησε
ρητά "θα το δούμε στη δοκιμή" με σενάριο: "άνοιξέ μου ένα είδος και
στείλε μου ένα μήνυμα στον Μυλωνά να τον ενημερώσεις ότι το έκανες").

**Υλοποίηση**:
- `RouteMainChatAgent` επιστρέφει πλέον `RoutingDecision` (struct με
  `Item`/`Trader`/`Email` bool flags + `StickyLabel` string) αντί για
  ΕΝΑ string - μπορούν να είναι ΠΕΡΙΣΣΟΤΕΡΑ ΑΠΟ ΕΝΑ true ταυτόχρονα.
  hitCount==1 -> καθαρή σίγουρη επιλογή (StickyLabel = αυτό). hitCount
  ≥2 -> όλα τα flags που χτύπησαν true μαζί (StickyLabel="general" -
  απλό, ασφαλές fallback για το ΕΠΟΜΕΝΟ turn). hitCount==0 -> sticky
  στο routingHint ή "general".
- `BuildRoutedTools(itemMode, traderMode, emailMode)` - ΝΕΑ μέθοδος,
  αντικατέστησε το παλιό αποκλειστικό `itemMode ? ... : traderMode ?
  ... : default` ternary. Είναι ΠΡΟΣΘΕΤΙΚΗ (additive) - Atlas base
  ΠΑΝΤΑ παρόν (ΠΕΡΙΛΑΜΒΑΝΕΙ πια μόνιμα send_email/reply_email/contact-
  lookup, βλ. προηγούμενο fix), + item tools αν itemMode, + trader
  tools αν traderMode, + read_email/download_attachment/read_calendar/
  create_outlook_event αν emailMode (τα "επιπλέον" πέρα από το ήδη
  μόνιμο base, μόνο όταν ο router ΕΙΝΑΙ σίγουρος για email).
- **isEmailCurtain παραμένει ΠΑΝΤΑ αποκλειστικό branch** (ΠΟΤΕ
  ενώνεται με item/trader/routed-email) - είναι πραγματική κουρτίνα με
  δικά της callbacks, όχι routed context.

### Δύο ακόμα ζωντανά ευρήματα από το πρώτο πραγματικό compound test

Ο χρήστης δοκίμασε: "άνοιξέ μου ένα είδος και στείλε μου ένα μήνυμα
στον Μυλωνά να τον ενημερώσεις ότι το έκανες". Το είδος δημιουργήθηκε
σωστά (✅), αλλά δύο fails:

1. **Λείπε κλικαριστό link για άνοιγμα είδους** - το `open_document`
   υποστηρίζει ΜΟΝΟ SOSOURCE-based παραστατικά (SALDOC/PURDOC/κλπ),
   ΠΟΤΕ MTRL (τα είδη δεν έχουν SOSOURCE). Ο χρήστης θυμόταν σωστά ότι
   ΕΙΧΕ δει κλικαριστό link σε προηγούμενο test - ήταν όμως για ΤΡΑΔΕΡ
   (`trader:OBJECTNAME:trdrId`, `JarvisTools.ExecuteOpenTrader`), ΟΧΙ
   για είδος - ΔΕΝ υπήρχε ποτέ αντίστοιχο "άνοιξε είδος" μηχανισμό. Το
   `ITEM` Designer object (ΤΟ ΙΔΙΟ που ήδη χρησιμοποιεί το
   `create_item`) υποστηρίζει AUTOLOCATE ΑΚΡΙΒΩΣ όπως τα trader objects -
   φτιάχτηκε νέο, παράλληλο scheme:
   - `JarvisItems.ExecuteOpenItem(xSupport, mtrlId)` - `ITEM[AUTOLOCATE=
     mtrlId]` μέσω `ExecS1Command` (ΑΠΛΟΥΣΤΕΡΟ από trader - ΕΝΑ πάντα
     σταθερό object name, καμία ανάγκη για objectName param).
   - `"item:mtrlId"` inline link scheme στο `index.html`
     (`ITEM_LINK_RE`, `renderInlineLinks`, `handleDocLinkClick` -
     `data-item-id` -> postCommand `open_item`).
   - `JarvisShell.xaml.cs`: νέο `OpenItem(cmd)` handler, ίδιο idiom με
     `OpenTrader` (Dispatcher.BeginInvoke reentrancy fix).
   - Νέα οδηγία στο `itemMode` (Forge) block: μετά από επιτυχές
     `create_item`, ΠΑΝΤΑ `[άνοιγμα είδους](item:mtrlId)` - ΕΚΤΟΣ bulk
     import (εκεί ΜΟΝΟ στο τελικό report, ΟΧΙ ένα link ανά είδος -
     άχρηστος θόρυβος).
2. **"δεν έχω πρόσβαση στα mail" - πραγματικό router gap, ΟΧΙ σωστή
   άρνηση** - το "βρες από τα email μου ένα του Μυλωνά και διάβασε από
   εκεί τη διεύθυνσή του" ΔΕΝ έπιασε `emailHit` καθόλου (το
   `emailVerbHit` κάλυπτε ΜΟΝΟ ρήματα ΑΠΟΣΤΟΛΗΣ - στείλε/απάντησε/
   γράψε - ΟΧΙ αναζήτησης/ανάγνωσης). Έπεσε σε sticky από το ΑΣΧΕΤΟ
   προηγούμενο "item" turn, οπότε ο Jarvis ΓΝΗΣΙΑ δεν είχε το
   `read_email` tool διαθέσιμο - η άρνησή του ήταν τεχνικά σωστή ΩΣ
   ΠΡΟΣ ΤΑ ΕΡΓΑΛΕΙΑ ΠΟΥ ΕΙΧΕ, αλλά λάθος behavior συνολικά. Fix: νέο
   `emailReadHit` σήμα - (βρες/ψάξε/διάβασε/δες/κοίτα) + (email/mail/
   εισερχόμενα) - πλέον routes σωστά σε Echo, δίνοντας `read_email`.

### Δύο ακόμα ευρήματα από μεγαλύτερο compound test (ίδια μέρα)

Ο χρήστης έστειλε ένα μήνυμα με 4 ζητούμενα μαζί (item + reporting
query για trader + email + CRM task) - επιβεβαιώθηκαν δύο ακόμα gaps:

1. **Ρήματα σε ΔΙΑΦΟΡΕΤΙΚΗ κλίση από την προστακτική** - "θέλω να
   **στείλεις**...αφού **ελέγξεις** τα email" ΔΕΝ ταίριαζε με τα
   keywords (μόνο "στείλε"/προστακτική υπήρχε, "ελέγξεις" καθόλου δεν
   υπήρχε). Προληπτικό fix (πριν καν δει ο χρήστης το αποτέλεσμα, απλή
   λογική εξαγωγή από το ίδιο το test message): `createVerbs`/
   `emailVerbStems`/`emailReadVerbStems` ξαναγράφτηκαν σε ΡΙΖΕΣ
   ρημάτων (stems) αντί για ολόκληρες κλιτές λέξεις - τα Ελληνικά
   ρήματα έχουν συχνά ΔΥΟ διαφορετικές ρίζες (present/aorist, π.χ.
   γράφω/έγραψα, ψάχνω/έψαξα, ανοίγω/άνοιξα) - και οι δύο
   συμπεριλήφθηκαν όπου διαφέρουν. Προστέθηκε ΚΑΙ το "ελεγξ"/"ελεγχ"
   (έλεγχος/checking) σαν read-intent σήμα - ήταν το ΑΚΡΙΒΩΣ ρήμα που
   έλειπε στο πραγματικό test.
2. **"δεν γίνεται να μην καταλαβαίνει ποιος είναι ο User" - αληθινό
   αρχιτεκτονικό κενό** - ζητήθηκε "βάλε μια εργασία σε μένα" και ο
   Jarvis ρώτησε "ποιο είναι το όνομά σου στο Soft1;", ΠΑΡΟΤΙ το
   session τον είχε ΗΔΗ χαιρετίσει ονομαστικά στην αρχική οθόνη
   ("Γεια σου, Χ!"). Ρίζα: αυτό το greeting είναι ΚΑΘΑΡΑ cosmetic UI
   text (`JarvisShell.GetDisplayName` -> `window.setGreeting` JS,
   ΜΙΑ φορά στο `NavigationCompleted`) - ΠΟΤΕ δεν έφτανε στον ίδιο τον
   Jarvis/system prompt. Fix: νέο `JarvisTools.
   GetCurrentUserDisplayName(xSupport)` (ΙΔΙΟ fallback chain PRSN.NAME
   -> USERS.NAME -> null με το ήδη υπάρχον `GetDisplayName`, static -
   reusable) - υπολογίζεται ΜΙΑ φορά πριν το loop (ίδιο idiom με
   `reportDecimalPlaces`), προστίθεται στο "Τρέχον context" (ΠΑΝΤΑ
   unconditional) μαζί με ρητή οδηγία: "σε μένα"/"εμένα" -> χρησιμοποίησε
   ΑΠΕΥΘΕΙΑΣ αυτό το UserId ως `actorUserId`, ΠΟΤΕ μην ρωτήσεις.

### ✅ Email: συνημμένο αρχείο στο send_email (ίδια μέρα)

Ζωντανό αίτημα χρήστη: "να μου βγάλει μια λίστα δεδομένων και να την
στείλει με email" - ο Jarvis σωστά ανέφερε ότι δεν μπορεί να στείλει ως
συνημμένο (πραγματικό κενό, όχι bug - το `send_email` δεν είχε ΚΑΝΕΝΑ
attachment param, μόνο `to`/`subject`/`body` - plain text μόνο).

**Design**: ΝΕΑ προαιρετικά `attachmentContent`/`attachmentFilename`
στο `send_email` - ο Jarvis ΦΤΙΑΧΝΕΙ το περιεχόμενο ο ίδιος (π.χ. CSV
κείμενο από ένα ήδη γνωστό `query_data` αποτέλεσμα στο context του),
ΔΕΝ διαβάζει/γράφει τοπικό αρχείο. ΞΕΧΩΡΙΣΤΟ από το ήδη υπάρχον
`export_query_to_file` (εκείνο φτιάχνει αρχείο ΣΤΟΝ ΔΙΣΚΟ για τον
χειριστή να το ανοίξει, ΟΧΙ για attach σε email). Server-side
(`JarvisEmailAccess.SendEmailAsync`), base64-encode του UTF8 κειμένου →
Graph `fileAttachment` (`contentBytes`) στο ίδιο `sendMail` payload -
ΕΝΑ HTTP call, όχι δύο. `contentType` = `text/csv` αν το filename
τελειώνει σε `.csv`, αλλιώς `text/plain`.

**Scope**: ΜΟΝΟ `send_email` (όχι `reply_email` ακόμα - δεν ζητήθηκε).

**Verification**: compile-check μόνο (ίδια 6 προϋπάρχοντα errors,
καμία νέα). Κανένα ζωντανό test ακόμα.

## 🔴 Bugfix ΣΟΒΑΡΟ: "ΜΟΝΙΜΑ 400 σε ΚΑΘΕ μήνυμα" (ίδια μέρα)

Ζωντανό, σοβαρό bug - ο χειριστής ζήτησε αποστολή λίστας 148 πελατών
μέσω email (χρησιμοποιώντας το ΝΕΟ attachment support πιο πάνω). Το UI
"κόλλησε" (άδεια φούσκα απάντησης, stop button χάθηκε αλλά τίποτα δεν
εμφανίστηκε) - ΜΕΤΑ από αυτό, **ΚΑΘΕ επόμενο μήνυμα** (ακόμα και
"είσαι εδώ;") έσκαγε με "✖ Σφάλμα από το AI (400)" - **μόνιμα**, μέχρι
που ο χειριστής δοκίμασε την ήδη υπάρχουσα εντολή `CLEAR` (καθαρισμός
ιστορικού) και ξεκόλλησε.

**Ρίζα** (`JarvisAgentClient.AskAsync`): όταν η απάντηση του Claude
κόβεται στη μέση (`stop_reason=="max_tokens"` - το μεγάλο
`attachmentContent`/`body` με 148 γραμμές πελατών χτύπησε το τότε
`MaxTokens=8000`), ο κώδικας ΗΔΗ είχε προσθέσει το ημιτελές assistant
μήνυμα στο `history` (χρειάζεται ΠΑΝΤΑ, για τα thinking blocks) - αν
αυτό το μήνυμα είχε ΗΜΙΤΕΛΕΣ/dangling `tool_use` block, ΚΑΝΕΝΑ tool δεν
εκτελούνταν (ο κώδικας θεωρούσε `stopReason != "tool_use"` σαν
"τελική απάντηση" και έκανε `return`) - άρα ΠΟΤΕ δεν προστέθηκε το
αντίστοιχο `tool_result`. Το Anthropic API απαιτεί ΚΑΘΕ `tool_use` να
έχει `tool_result` στο ΕΠΟΜΕΝΟ μήνυμα - χωρίς αυτό, η ιστορία μένει
ΜΟΝΙΜΑ κατεστραμμένη, και επειδή η ΙΔΙΑ ιστορία στέλνεται σε ΚΑΘΕ
επόμενο request, 400 σε ΚΑΘΕ επόμενο μήνυμα ασχέτως περιεχομένου.

**Fix (δύο μέρη)**:
1. Νέος ρητός έλεγχος `if (stopReason == "max_tokens")` - αφαιρεί το
   μόλις προστεθέν, ενδεχομένως κατεστραμμένο assistant μήνυμα ΠΡΙΝ
   επιστρέψει (`history.RemoveAt(history.Count - 1)`) - η ιστορία
   μένει καθαρή/valid, ο χειριστής παίρνει ΚΑΘΑΡΟ μήνυμα σφάλματος
   ("η απάντηση έκοψε στη μέση, δοκίμασε κάτι πιο σύντομο") αντί για
   άδεια φούσκα + μόνιμη καταστροφή.
2. `MaxTokens` 8000 → **16000** - μειώνει τη συχνότητα (ΔΕΝ λύνει την
   κατηγορία bug μόνο του - πάντα μπορεί να υπάρξει αρκετά μεγάλο
   αίτημα να ξαναχτυπήσει όποιο όριο υπάρχει, γι' αυτό το (1) είναι το
   ΠΡΑΓΜΑΤΙΚΟ δομικό fix).

**Verification**: compile-check μόνο (ίδια 6 errors, καμία νέα).
Κανένα ζωντανό test ακόμα - το CLEAR ΕΠΙΒΕΒΑΙΩΘΗΚΕ ζωντανά ως άμεσο
workaround, το ίδιο το δομικό fix (να ΜΗΝ ξανασυμβεί) όχι ακόμα.

## ✅ export_shown_table: το κουμπί export γίνεται και οδηγία (ίδια μέρα)

Ρητό αίτημα χρήστη: "το κουμπί PDF στο παράθυρο της λίστας πρέπει να
είναι οδηγία για τον agent, όχι απλά κουμπί. Το ίδιο και για τα
υπόλοιπα κουμπιά (CSV, XLSX)".

**Design**: ΝΕΟ tool `export_shown_table(format)` που ξαναχρησιμοποιεί
ΑΚΡΙΒΩΣ τον ίδιο μηχανισμό με τα ήδη υπάρχοντα κουμπιά Excel/CSV/PDF
(`exportBlocks` στο `index.html`) - καμία διπλή λογική. Το backend
(`JarvisTools.ExecuteExportShownTable`) δεν κάνει καμία δουλειά, απλά
προωθεί το format (ίδιο idiom "Claude/UI υπολογίζει, το tool
ΜΕΤΑΦΕΡΕΙ" με `show_contact_results`) μέσω νέου `onExportShownTable`
callback (`JarvisAgentClient.AskAsync`) σε νέα JS συνάρτηση
`window.triggerTableExport(format)` (`ExecuteScriptAsync`) - αυτή
διαβάζει `lastMainExportEl` (το ΤΕΛΕΥΤΑΙΟ assistant μήνυμα με πίνακα
στο κύριο chat, ήδη κρατημένο πάνω στο DOM element ως
`el.__jarvisBlocks` από παλιότερο feature) και καλεί το ΙΔΙΟ
`exportBlocks(...)` που θα έτρεχε το κλικ.

**ΞΕΧΩΡΙΣΤΟ από το `export_query_to_file`**: εκείνο ξανατρέχει το SQL
από την αρχή (για μεγάλα result sets πέρα από το 200-row/100-row
preview cap) - το `export_shown_table` ΔΕΝ χρειάζεται sql/filename,
ξαναχρησιμοποιεί ό,τι ΗΔΗ φαίνεται στην οθόνη.

**Scope v1**: ΜΟΝΟ κύριο chat (`onExportShownTable` wired μόνο στο
main chat call site στο `JarvisShell.xaml.cs` - ΟΧΙ ακόμα στις
κουρτίνες Browser/Email/Courier/Help, δεν ζητήθηκε).

**Verification**: compile-check μόνο (ίδια 6 errors, καμία νέα).
Κανένα ζωντανό test ακόμα.

## ✅ PDF→email attach: πλήρες round-trip flow (ίδια μέρα)

Ζωντανή διευκρίνιση χρήστη πάνω στο `export_shown_table` - flow σε 3
βήματα: "1) του ζητάω δεδομένα, τα βγάζει σε λίστα με κουμπιά, αντί να
πατήσει ο χειριστής το κουμπί PDF το πατάει ο agent - σε εκείνο το
σημείο έχει φτιάξει το αρχείο και ξέρει και σε ποιο path. 2) βρίσκει
την email διεύθυνση, βάζει συνημμένο το αρχείο που ξέρει ήδη το path.
3) το στέλνει γιατί έχει τα δικαιώματα". Το `export_shown_table` ήταν
μέχρι τώρα fire-and-forget (δεν περίμενε/δεν ήξερε το path) - έγινε
πραγματικό round-trip:

- **index.html**: `window.triggerTableExport` πλέον επιστρέφει
  `Promise<path|null>` - `requestId`-based matching (ίδιο σκεπτικό με
  το ήδη υπάρχον `dashboard_result`/requestId pattern) πάνω στο
  `export_result` μήνυμα, με safety timeout (30s) ώστε η Promise να
  ΜΗΝ μείνει κρεμασμένη αν κάτι πάει στραβά.
- **JarvisShell.xaml.cs**: `HandleExportAsync`/`PostExportResult`
  στέλνουν πίσω ΚΑΙ το πραγματικό `path` ΚΑΙ το `requestId` (πριν μόνο
  το markdown-formatted `text`). Το `onExportShownTable` callback
  άλλαξε από `Action<string>` σε async lambda που κάνει `await
  ExecuteScriptAsync(...)` (WebView2 περιμένει Promise-returning JS
  functions και επιστρέφει το resolved αποτέλεσμα ως JSON).
- **JarvisAgentClient.cs/JarvisTools.cs**: `onExportShownTable`
  `Func<string,Task<string>>`, `ExecuteExportShownTable` async - το
  tool result έχει πλέον `path`.
- **JarvisEmailAccess.cs**: νέο `attachmentFilePath` στο `send_email` -
  διαβάζει ΠΡΑΓΜΑΤΙΚΑ bytes από τον δίσκο (`File.ReadAllBytes`, ΟΧΙ
  UTF8-encode κειμένου όπως το ήδη υπάρχον `attachmentContent` - ένα
  PDF/Excel είναι binary), content type από την κατάληξη
  (application/pdf, .xlsx mime type, κλπ). Προηγείται του
  `attachmentContent` αν δοθούν και τα δύο.
- **System prompt**: νέα οδηγία - μετά από `export_shown_table`, αν ο
  χειριστής θέλει ΚΑΙ αποστολή email με το ΙΔΙΟ αρχείο συνημμένο,
  κάλεσε `send_email` με `attachmentFilePath` = το `path` από το tool
  result - ΞΕΧΩΡΙΣΤΟ βήμα, ίδιος κανόνας επιβεβαίωσης με πάντα.

**Verification**: compile-check μόνο (ίδια 6 errors, καμία νέα).
Κανένα ζωντανό test ακόμα - το πλήρες flow (δείξε πίνακα → agent
πατάει PDF → περιμένει το path → επισυνάπτει → στέλνει) χρειάζεται
πραγματική δοκιμή στο Soft1.

## ✅ Επιλογή γραμμών μέσω οδηγίας (export_shown_table rowIndices, ίδια μέρα)

Ζωντανή συζήτηση χρήστη - αρχιτεκτονικό σημείο: "δεν βλέπω τυποποιημένα
τα flows ενώ έχουμε δομές... έχουμε τρόπο να εμφανίζουμε λίστες, μέσα
στις λίστες έχουμε επιλογές γραμμών, εξαγωγές αρχείων. Δεν μπορώ να
καταλάβω γιατί αυτά δεν είναι μεταφρασμένα flows προς χρήση του agent".
Μετά από επισκόπηση: το export ΗΔΗ έγινε agent-invokable
(`export_shown_table`) - το ΜΟΝΟ πραγματικό κενό που έμεινε ήταν η
**επιλογή γραμμών** (checkboxes) - μέχρι τώρα ΜΟΝΟ ο χειριστής μπορούσε
να τσεκάρει ΣΥΓΚΕΚΡΙΜΕΝΕΣ γραμμές πριν export.

**Design**: το `export_shown_table` παίρνει προαιρετικό `rowIndices`
(array από 0-based δείκτες ΣΤΙΣ γραμμές δεδομένων, ΟΧΙ header) - ο
Jarvis υπολογίζει ΜΟΝΟΣ ΤΟΥ ποιες γραμμές θέλει (π.χ. "μόνο τους
πρώτους 10"/"μόνο πάνω από 1000€") βάσει των δεδομένων που ΗΔΗ έγραψε
στον πίνακα - **καμία γλώσσα φίλτρων** χρειάστηκε, απλά "ποιες γραμμές
θέλω" (leverages το ότι ο Claude ήδη έχει τέλεια ανάκληση των δεδομένων
που ο ίδιος παρήγαγε).

- `index.html`: `applyRowSelection(el, blocks, explicitIndices)` - ΝΕΟ
  τρίτο προαιρετικό param, όταν δίνεται παρακάμπτει το checkbox-based
  φιλτράρισμα (ΚΑΜΙΑ αλλαγή στο ήδη υπάρχον, όταν δεν δίνεται).
  `window.triggerTableExport(format, rowIndices)` περνάει το array στο
  `exportBlocks`.
- `JarvisTools.cs`: tool schema νέο `rowIndices` (array of integer),
  `ExecuteExportShownTable` το εξάγει από το `JArray` σε `int[]`.
- `JarvisAgentClient.cs`: `onExportShownTable`
  `Func<string,int[],Task<string>>` (πριν `Func<string,Task<string>>`).
- `JarvisShell.xaml.cs`: η lambda σειριοποιεί το `int[]` σε JSON array
  literal μέσα στο `ExecuteScriptAsync` call (ή `"null"` αν δεν δόθηκε).

**Verification**: compile-check μόνο (ίδια 6 errors, καμία νέα).
Κανένα ζωντανό test ακόμα.

---

## Ιστορικό / γιατί υπάρχουν αχρησιμοποίητα αρχεία

Δοκιμάστηκαν με τη σειρά, κρατούνται σαν τεκμηρίωση:

1. **Plan A** (`JarvisObject.cs`, custom Designer Object `CCCJARVIS` +
   Panel `JarvisPanel` + `[WorksOn]` + `InsertWPFContent`) — έσκαγε στον
   Designer με Argument Out Of Range / access violation (`XDll.dll`) κατά τη
   δημιουργία Primary Band Table/Virtual Table. Parked.
2. **Plan C.1** (`JarvisWindow.xaml`, καθαρό WPF `Window` μέσω "Dll Form" job
   type) — άνοιγε αλλά **κενό**: WPF `Window` φτιαγμένο από ξένο (μη-WPF) host
   δεν παίρνει σωστά `Application`/Dispatcher context. `JarvisWindow.xaml`/
   `.xaml.cs` διαγράφηκαν οριστικά 20/08 (μηδέν call sites, 100% νεκρός
   κώδικας - επιβεβαιώθηκε).
3. **Plan C.2** (`JarvisHostForm.cs`, WinForms `Form` + `ElementHost` που
   φιλοξενεί WPF content) — **αυτό δουλεύει**, είναι η τρέχουσα αρχιτεκτονική.

### 20/08 - "Μαύρο πλαίσιο" artifact πάνω από το Jarvis: ΕΚΤΟΣ S1Jarvis

Εκτεταμένη ζωντανή έρευνα (μεγάλο μέρος της ημέρας) για ένα μαύρο
ορθογώνιο που εμφανιζόταν πάνω από την οθόνη χαιρετισμού του Jarvis -
σταθερό μέγεθος/θέση (δεν άλλαζε με resize του window), καμία
αλληλεπίδραση ποντικιού (δεξί-κλικ "Inspect" έδειχνε το περιεχόμενο
ΠΙΣΩ από το κουτί, όχι το ίδιο).

**Αποκλείστηκαν, με τη σειρά, μέσω ζωντανών δοκιμών**:
1. Stale WebView2 cache folder (διαγράφηκε, καμία επίδραση).
2. Δεύτερο/orphan `JarvisHostForm` instance (Win32 window enumeration πάνω
   στο ζωντανό Soft1 process, `EnumWindows`/`EnumChildWindows` - βρέθηκε
   ΕΝΑ και μοναδικό, καθαρό chain, καμία διπλή εμφάνιση).
3. Το ίδιο το reparenting/docking mechanism του Soft1 (διαγνωστικό
   `JarvisHostForm2`, εντελώς άδειο Form χωρίς καμία δική μας UI - "Dll
   Form" job type σε ξεχωριστό test menu item - καθαρό, κανένα artifact).
4. Stale/παγωμένο WebView2 composited frame από το raw reparenting
   (`webView.Reload()` one-shot, καμία επίδραση).
5. Το δεύτερο WebView2 (`browserView`, Browser mode) - αφαιρέθηκε εντελώς
   από το visual tree, καμία επίδραση.
6. VS Hot Reload / Live Visual Tree overlay (επιμένει χωρίς debugger
   attached).
7. Sophos Endpoint hooking (άσχετο, ρητά επιβεβαιωμένο από χρήστη).

**Πραγματική αιτία (εντοπίστηκε από τον χρήστη)**: desktop/DWM-level
artifact, ΕΙΔΙΚΟ στη ΔΕΞΙΑ οθόνη (δύο-οθονών setup) - μετακινώντας
ολόκληρο το Soft1 window στην αριστερή οθόνη, το κουτί εξαφανίζεται
εντελώς. Κάτι έμεινε "κρεμασμένο" στο Windows compositor εκείνης της
οθόνης (πιθανό κατάλοιπο από άλλη εφαρμογή) - **εντελώς άσχετο με τον
S1Jarvis κώδικα**. Fix εκτός κώδικα: restart Windows Explorer ή restart
υπολογιστή.

Όλες οι προσωρινές diagnostic αλλαγές (browserPane removal, Reload()
test, `JarvisHostForm2`) αφαιρέθηκαν/καθαρίστηκαν μετά την εύρεση της
αιτίας. Το `AreDevToolsEnabled` γύρισε οριστικά σε `false` (μόνιμο
lockdown, ρητό αίτημα χρήστη 16/08).
