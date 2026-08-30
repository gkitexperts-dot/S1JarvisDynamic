using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace S1Jarvis.Core
{
    /// <summary>
    /// Authoritative fact-only companion knowledge shared by every Jarvis agent.
    /// Behavioral rules belong to JarvisPolicyRegistry; execution contracts belong
    /// to task/tool registries and validators. This class contains only confirmed
    /// business/schema facts recovered from the mature runtime and returns the
    /// smallest useful slice for a request.
    /// </summary>
    internal static class JarvisKnowledgeCompanion
    {
        private const string Marker = "[JARVIS_KNOWLEDGE_CONTEXT]";

        internal static string BuildForTask(string taskType)
        {
            return Marker + "\n" + BuildTaskKnowledge(taskType).ToString(Formatting.None);
        }

        internal static string BuildForRequest(string agentName, string providerRequestJson)
        {
            JObject request;
            try { request = JObject.Parse(providerRequestJson ?? "{}"); }
            catch { request = new JObject(); }

            string taskType = (string)request["metadata"]?["jarvis_task"];
            HashSet<string> toolNames = ReadToolNames(request["tools"] as JArray);
            JObject knowledge = !string.IsNullOrWhiteSpace(taskType)
                ? BuildTaskKnowledge(taskType)
                : BuildToolKnowledge(toolNames, agentName);
            return Marker + "\n" + knowledge.ToString(Formatting.None);
        }

        internal static string[] ValidateCoverage()
        {
            var issues = new List<string>();
            foreach (JarvisTaskDescriptor task in JarvisTaskRegistry.AllTasks)
            {
                if (task == null) continue;
                bool needsSoft1Knowledge = (task.Tools ?? new string[0])
                    .Any(x => string.Equals(x, "query_data", StringComparison.OrdinalIgnoreCase));
                if (!needsSoft1Knowledge) continue;

                JObject schema = BuildTaskKnowledge(task.TaskType)["schema"] as JObject;
                if (schema == null || !schema.Properties().Any())
                    issues.Add("Task with query_data has no companion schema slice: " + task.TaskType);
            }
            return issues.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        }

        private static JObject BuildTaskKnowledge(string taskType)
        {
            string task = (taskType ?? string.Empty).Trim();
            var result = NewEnvelope(task);

            if (string.Equals(task, "CreateCrmTask", StringComparison.OrdinalIgnoreCase))
            {
                AddEntityKnowledge(result);
                AddSchema(result, "USERS", UsersSchema());
                AddSchema(result, "TRDR", TraderSchema());
                return result;
            }

            if (string.Equals(task, "CreateCalendarEvent", StringComparison.OrdinalIgnoreCase))
            {
                AddEntityKnowledge(result);
                return result;
            }

            if (string.Equals(task, "SendEmail", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(task, "ReplyEmail", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(task, "ReadInbox", StringComparison.OrdinalIgnoreCase))
            {
                AddEntityKnowledge(result);
                AddSchema(result, "PRSN", PersonSchema());
                return result;
            }

            JarvisTaskDescriptor descriptor = JarvisTaskRegistry.Find(task);
            bool usesQueryData = descriptor != null && (descriptor.Tools ?? new string[0])
                .Any(x => string.Equals(x, "query_data", StringComparison.OrdinalIgnoreCase));

            if (usesQueryData || string.Equals(task, "ReportData", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(task, "FindTrader", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(task, "CreateOrder", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(task, "ResolveDocumentConversion", StringComparison.OrdinalIgnoreCase))
                AddFullSoft1Knowledge(result);
            else
                AddEntityKnowledge(result);

            return result;
        }

        private static JObject BuildToolKnowledge(HashSet<string> tools, string agentName)
        {
            var result = NewEnvelope(string.Empty);
            if (tools.Contains("query_data") || string.Equals(agentName, "Atlas", StringComparison.OrdinalIgnoreCase))
                AddFullSoft1Knowledge(result);
            else if (string.Equals(agentName, "Compass", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(agentName, "Echo", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(agentName, "Scout", StringComparison.OrdinalIgnoreCase))
            {
                AddEntityKnowledge(result);
                if (string.Equals(agentName, "Echo", StringComparison.OrdinalIgnoreCase))
                    AddSchema(result, "PRSN", PersonSchema());
            }
            return result;
        }

        private static JObject NewEnvelope(string taskType)
        {
            return new JObject
            {
                ["source"] = "JarvisKnowledgeCompanion",
                ["taskType"] = taskType ?? string.Empty,
                ["schema"] = new JObject()
            };
        }

        private static void AddFullSoft1Knowledge(JObject result)
        {
            AddEntityKnowledge(result);
            AddSchema(result, "TRDR", TraderSchema());
            AddSchema(result, "FINDOC", FindocSchema());
            AddSchema(result, "SERIES", SeriesSchema());
            AddSchema(result, "TRDBALSHEET", BalanceSchema());
            AddSchema(result, "CCCLOADING", LoadingSchema());
            AddSchema(result, "CCCLOADCOMPS", LoadingLinesSchema());
            AddSchema(result, "USERS", UsersSchema());
            AddSchema(result, "PRSN", PersonSchema());
            result["documentSources"] = BuildDocumentSources();
        }

        private static void AddEntityKnowledge(JObject result)
        {
            result["businessEntities"] = JarvisBusinessEntityCatalog.BuildAgentContext();
        }

        private static void AddSchema(JObject result, string name, JObject value)
        {
            JObject schema = result["schema"] as JObject;
            if (schema == null)
            {
                schema = new JObject();
                result["schema"] = schema;
            }
            schema[name] = value;
        }

        private static JObject TraderSchema()
        {
            return new JObject
            {
                ["identity"] = "TRDR",
                ["fields"] = new JArray(new object[] { "TRDR", "CODE", "NAME", "AFM", "SODTYPE", "COMPANY" }),
                ["roleDiscriminator"] = "SODTYPE"
            };
        }

        private static JObject FindocSchema()
        {
            return new JObject
            {
                ["identity"] = "FINDOC",
                ["fields"] = new JArray(new object[] { "FINDOC", "TRDR", "TRNDATE", "FINCODE", "SUMAMNT", "SERIES", "SOSOURCE", "COMPANY", "INSUSER", "INSDATE" }),
                ["seriesJoin"] = "SERIES.COMPANY=FINDOC.COMPANY AND SERIES.SERIES=FINDOC.SERIES AND SERIES.SOSOURCE=FINDOC.SOSOURCE",
                ["traderJoin"] = "TRDR.COMPANY=FINDOC.COMPANY AND TRDR.TRDR=FINDOC.TRDR",
                ["knownNonTable"] = "FINTRD"
            };
        }

        private static JObject SeriesSchema()
        {
            return new JObject
            {
                ["joinKeys"] = new JArray(new object[] { "COMPANY", "SERIES", "SOSOURCE" }),
                ["knownFields"] = new JArray(new object[] { "COMPANY", "SERIES", "SOSOURCE", "NAME" }),
                ["purpose"] = "document type/series metadata for FINDOC"
            };
        }

        private static JObject BalanceSchema()
        {
            return new JObject
            {
                ["fields"] = new JArray(new object[] { "TRDR", "FISCPRD", "LDEBIT", "LCREDIT" }),
                ["purpose"] = "progressive trader balances by fiscal year/period"
            };
        }

        private static JObject LoadingSchema()
        {
            return new JObject
            {
                ["executionDateField"] = "Insdate",
                ["knownInvalidExecutionDateFields"] = new JArray(new object[] { "StartTime", "Executiondate" })
            };
        }

        private static JObject LoadingLinesSchema()
        {
            return new JObject
            {
                ["purpose"] = "loading lines/compartments",
                ["parent"] = "CCCLOADING"
            };
        }

        private static JObject UsersSchema()
        {
            return new JObject
            {
                ["identity"] = "USERS",
                ["fields"] = new JArray(new object[] { "USERS", "NAME" })
            };
        }

        private static JObject PersonSchema()
        {
            return new JObject
            {
                ["identity"] = "PRSN",
                ["knownFields"] = new JArray(new object[] { "NAME", "NAME2", "EMAIL", "EMAIL1" })
            };
        }

        private static JObject BuildDocumentSources()
        {
            return new JObject
            {
                ["1351"] = "Sales/Invoices",
                ["1353"] = "Sales services",
                ["1251"] = "Supplier receipt/delivery note",
                ["1253"] = "Purchase services",
                ["5151"] = "Internal movement/production",
                ["1412"] = "Transfer to supplier",
                ["1413"] = "Transfer from customer",
                ["2021"] = "CRM action (SOACTION; identifier is soactionId, not FINDOC)"
            };
        }

        private static HashSet<string> ReadToolNames(JArray tools)
        {
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (tools == null) return result;
            foreach (JObject tool in tools.OfType<JObject>())
            {
                string name = (string)tool["name"];
                if (string.IsNullOrWhiteSpace(name)) name = (string)tool["function"]?["name"];
                if (!string.IsNullOrWhiteSpace(name)) result.Add(name.Trim());
            }
            return result;
        }
    }
}
