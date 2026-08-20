using System;
using System.Collections.Generic;
using System.Data;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Softone;

namespace S1Jarvis.Core
{
    // ══════════════════════════════════════════════════════════════════════
    // DashboardPanels
    //
    // ΝΕΟ 20/08, ρητό αίτημα χρήστη - το Commercial dashboard "δε θα καλεί
    // καθόλου agent παρά θα τρέχει τα queries". Πριν: HandleDashboardQueryAsync
    // έστελνε prompt στον πλήρη AI agent loop (4 tool-use round-trips, αργό -
    // βλ. παλιά BuildDashboardPrompt). Τώρα: 20 "slots" (ParamCode
    // 500040-500059), ΕΝΑ cccParams row ανά panel -
    //   ParamValue        = τύπος γραφήματος (αριθμός, βλ. ChartTypeFromNumber)
    //   ParamValueString  = SQL query, ΕΝΑ παράμετρος placeholder (:1 = η
    //                       επιλεγμένη ημερομηνία, βλ. GetSQLDataSet(sql, date))
    // Άδειο/λείπον ParamValueString = το panel παραλείπεται σιωπηλά -
    // επιτρέπει σταδιακή συμπλήρωση 4->20 χωρίς rebuild/deployment.
    //
    // ΣΧΗΜΑ αποτελέσματος SQL: στήλη 0 = labels (π.χ. όνομα πελάτη/προϊόντος),
    // στήλες 1..N = ένα dataset η καθεμία (label = όνομα στήλης, π.χ. "Τζίρο").
    //
    // Η έξοδος (BuildDashboardText) είναι το ΙΔΙΟ ```chart fenced-block
    // κείμενο που παλιά έγραφε ο Claude - ΜΗΔΕΝ αλλαγές χρειάζονται στο
    // index.html/frontend, το ήδη υπάρχον renderDashboardResult/
    // parseAssistant/upgradeTablesToCharts/blocksToHtml/mountPendingCharts
    // το δείχνουν ΑΚΡΙΒΩΣ όπως πριν.
    // ══════════════════════════════════════════════════════════════════════
    public static class DashboardPanels
    {
        public const int FirstParamCode = 500040;
        public const int LastParamCode = 500059; // 20 slots

        // Τίτλοι ΜΟΝΟ για τα 4 αρχικά, γνωστά panels (κληρονομιά από το
        // παλιό AI-driven BuildDashboardPrompt) - ρητή απόφαση χρήστη "δύο
        // τιμές ανά param" (τύπος+SQL, ΟΧΙ τρίτο param για τίτλο). Νέα
        // panels (5-20) δείχνονται ΧΩΡΙΣ τίτλο μέχρι να αποφασιστεί
        // μηχανισμός ονομασίας - βλ. buildChartJsConfig στο index.html,
        // ήδη χειρίζεται graceful κενό title (κρύβει την τίτλο-γραμμή).
        private static readonly Dictionary<int, string> DefaultTitles = new Dictionary<int, string>
        {
            [500040] = "Top 10 πελάτες με τζίρο",
            [500041] = "Top 10 προϊόντα σε τεμάχια",
            [500042] = "Top 10 προϊόντα με τζίρο",
            [500043] = "Τρέχουσες τιμές ανά προϊόν",
        };

        private static string ChartTypeFromNumber(int n)
        {
            switch (n)
            {
                case 2: return "line";
                case 3: return "pie";
                case 4: return "doughnut";
                default: return "bar"; // 1 ή οτιδήποτε άγνωστο -> ασφαλές default
            }
        }

        // Επιστρέφει null αν ΚΑΝΕΝΑ panel δεν είναι ρυθμισμένο/είχε
        // δεδομένα - ο caller δείχνει το ήδη υπάρχον "Δεν βρέθηκαν δεδομένα"
        // placeholder (ίδιο μήνυμα με πριν, βλ. renderDashboardResult).
        public static string BuildDashboardText(XSupport xSupport, string date)
        {
            var sb = new StringBuilder();
            int panelsRendered = 0;

            for (int code = FirstParamCode; code <= LastParamCode; code++)
            {
                string sql;
                int chartTypeNum;
                if (!TryReadPanelParam(xSupport, code, out sql, out chartTypeNum))
                    continue;
                if (string.IsNullOrWhiteSpace(sql))
                    continue; // δεν έχει ρυθμιστεί ακόμα αυτό το slot

                DataTable dt;
                try
                {
                    XTable result = xSupport.GetSQLDataSet(sql, date);
                    dt = result.CreateDataTable(true);
                }
                catch (Exception ex)
                {
                    // ΕΝΑ σπασμένο panel (π.χ. λάθος SQL σε ένα slot) ΔΕΝ
                    // πρέπει να ρίξει όλο το dashboard - log και συνέχισε
                    // στο επόμενο.
                    DebugLog.Log($"[DashboardPanels] panel {code} SQL EXCEPTION: {ex.Message}");
                    continue;
                }

                if (dt.Rows.Count == 0 || dt.Columns.Count < 2)
                    continue; // καμία πώληση/καμία στήλη δεδομένων εκείνη την ημέρα

                var labels = new JArray();
                foreach (DataRow row in dt.Rows)
                    labels.Add(row[0] == DBNull.Value ? "" : Convert.ToString(row[0]));

                var datasets = new JArray();
                for (int col = 1; col < dt.Columns.Count; col++)
                {
                    var data = new JArray();
                    foreach (DataRow row in dt.Rows)
                    {
                        object v = row[col];
                        data.Add(v == DBNull.Value ? 0.0 : Convert.ToDouble(v));
                    }
                    datasets.Add(new JObject
                    {
                        ["label"] = dt.Columns[col].ColumnName,
                        ["data"] = data
                    });
                }

                string title;
                DefaultTitles.TryGetValue(code, out title);

                var spec = new JObject
                {
                    ["type"] = ChartTypeFromNumber(chartTypeNum),
                    ["title"] = title ?? "",
                    ["labels"] = labels,
                    ["datasets"] = datasets
                };

                sb.Append("```chart\n").Append(spec.ToString(Formatting.None)).Append("\n```\n");
                panelsRendered++;
            }

            return panelsRendered > 0 ? sb.ToString() : null;
        }

        private static bool TryReadPanelParam(XSupport xSupport, int paramCode, out string sql, out int chartTypeNum)
        {
            sql = null;
            chartTypeNum = 1;
            try
            {
                XTable p = xSupport.GetSQLDataSet(
                    "SELECT TOP 1 ParamValue, ParamValueString FROM cccParams WHERE ParamCode = :1 " +
                    "AND (paramsIsActive = 1 OR paramsIsActive IS NULL) ORDER BY cccParams DESC",
                    paramCode);
                DataTable pt = p.CreateDataTable(true);
                if (pt.Rows.Count == 0) return false;

                DataRow row = pt.Rows[0];
                sql = row["ParamValueString"] == DBNull.Value ? null : Convert.ToString(row["ParamValueString"]);
                chartTypeNum = row["ParamValue"] == DBNull.Value ? 1 : Convert.ToInt32(Convert.ToDouble(row["ParamValue"]));
                return true;
            }
            catch (Exception ex)
            {
                DebugLog.Log($"[DashboardPanels] param {paramCode} read EXCEPTION: {ex.Message}");
                return false;
            }
        }
    }
}
