using System;
using System.Collections.Generic;
using System.Linq;

namespace S1Jarvis.Core
{
    /// <summary>
    /// Typed/tunable values that are themselves policy. Decision engines and
    /// renderers read them from here instead of owning private copies.
    /// Behavioral policy text remains in JarvisPolicyRegistry; this class is the
    /// typed parameter block of the same central Policies Inventory subsystem.
    /// </summary>
    internal static class JarvisPolicySettings
    {
        internal static class Routing
        {
            internal const double DefaultAcceptThreshold = 0.82;
            internal const double DefaultMinimumForDynamicPass = 0.45;
            internal const double AmbiguityMargin = 0.12;
            internal const double DynamicAcceptThreshold = 0.78;
            internal const double ConflictingDynamicThreshold = 0.88;
            internal const double ConflictingDynamicLead = 0.18;
            internal const double CompanySpecificBonus = 0.06;
            internal const double MaxPriorityBonus = 0.05;
            internal const double MaxHistoryBonus = 0.06;
            internal const double MaxHistoryPenalty = 0.10;
            internal const double ReinforcementWeight = 0.50;
            internal const double HistoryEvidenceFullSample = 20.0;
            internal const double PriorityStepWeight = 0.005;
        }

        /// <summary>
        /// Canonical user-facing presentation policy. These values are consumed by
        /// every deterministic renderer and are also exposed to the Jarvis
        /// presentation context. No agent/task owns a private date/number/table
        /// convention.
        /// </summary>
        internal static class Presentation
        {
            internal const string CultureName = "el-GR";
            internal const string DateFormat = "dd/MM/yyyy";
            internal const string DateTimeFormat = "dd/MM/yyyy HH:mm";
            internal const string NumberFormat = "#,##0.##";
            internal const string CurrencyNumberFormat = "#,##0.00";
            internal const string CurrencySuffix = " €";
            internal const string NullDisplay = "—";

            internal const string TextAlignmentMarker = ":---";
            internal const string NumericAlignmentMarker = "---:";
            internal const string DateAlignmentMarker = ":---:";

            internal const int DefaultPreviewRows = 50;
            internal const int MaxChatTableRows = 250;

            internal static readonly string[] DateColumnHints =
            {
                "DATE", "TRNDATE", "INSDATE", "FROMDATE", "TODATE", "REMINDERDATE",
                "ΗΜΕΡΟΜΗΝΙΑ", "ΗΜΕΡΟΜΗΝΙΑΣ"
            };

            internal static readonly string[] CurrencyColumnHints =
            {
                "AMNT", "AMOUNT", "SUM", "TOTAL", "VALUE", "PRICE", "COST", "BALANCE",
                "ΠΟΣΟ", "ΑΞΙΑ", "ΣΥΝΟΛΟ", "ΥΠΟΛΟΙΠΟ", "ΤΙΜΗ"
            };

            internal static readonly IDictionary<string, string> ColumnLabels =
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["FINDOC"] = "ID",
                    ["FINCODE"] = "Παραστατικό",
                    ["TRNDATE"] = "Ημερομηνία",
                    ["INSDATE"] = "Ημερομηνία Καταχώρησης",
                    ["SUMAMNT"] = "Ποσό",
                    ["TOTAL"] = "Σύνολο",
                    ["BALANCE"] = "Υπόλοιπο",
                    ["CUSTOMER_CODE"] = "Κωδικός πελάτη",
                    ["CUSTOMER_NAME"] = "Πελάτης",
                    ["SUPPLIER_CODE"] = "Κωδικός προμηθευτή",
                    ["SUPPLIER_NAME"] = "Προμηθευτής",
                    ["TRDR"] = "ID Συναλλασσόμενου",
                    ["TRDR_CODE"] = "Κωδικός",
                    ["TRDR_NAME"] = "Συναλλασσόμενος",
                    ["SERIES"] = "Σειρά",
                    ["SERIES_NAME"] = "Σειρά / Τύπος",
                    ["DOCUMENT_TYPE"] = "Τύπος Παραστατικού",
                    ["SOACTION"] = "ID Εργασίας",
                    ["SOACTIONID"] = "ID Εργασίας",
                    ["MTRL"] = "ID Είδους",
                    ["CODE"] = "Κωδικός",
                    ["NAME"] = "Περιγραφή"
                };

            internal static string GetColumnLabel(string columnName)
            {
                if (string.IsNullOrWhiteSpace(columnName)) return string.Empty;
                string label;
                if (ColumnLabels.TryGetValue(columnName.Trim(), out label)) return label;
                return columnName.Trim().Replace("_", " ");
            }

            internal static bool ColumnNameMatches(string columnName, IEnumerable<string> hints)
            {
                string name = (columnName ?? string.Empty).Trim().ToUpperInvariant();
                return !string.IsNullOrWhiteSpace(name) &&
                       (hints ?? Enumerable.Empty<string>()).Any(h =>
                           !string.IsNullOrWhiteSpace(h) && name.Contains(h.Trim().ToUpperInvariant()));
            }

            internal static string BuildPolicyEnvelope()
            {
                return "[JARVIS_PRESENTATION_POLICY_PROFILE]\n" +
                       "culture=" + CultureName + "\n" +
                       "date=" + DateFormat + "\n" +
                       "datetime=" + DateTimeFormat + "\n" +
                       "number=" + NumberFormat + "\n" +
                       "currency=" + CurrencyNumberFormat + CurrencySuffix + "\n" +
                       "null=" + NullDisplay + "\n" +
                       "alignment=text:left,number:right,date:center\n" +
                       "addressable_results=clickable_when_authoritative_reference_exists\n" +
                       "continuations=use_same_presentation_policy";
            }
        }
    }
}
