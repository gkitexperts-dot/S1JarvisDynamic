using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace S1Jarvis.Core
{
    /// <summary>
    /// Deterministic architecture smoke tests. These checks validate general
    /// invariants without depending on a particular natural-language test prompt.
    /// </summary>
    internal static class JarvisArchitectureRegressionAudit
    {
        internal static string[] Validate()
        {
            var issues = new List<string>();
            ValidateTenantScope(issues);
            ValidateKnowledgeCompanion(issues);
            ValidateLastMileContextInjection(issues);
            ValidateActiveContextLifecycle(issues);
            ValidateExportContract(issues);
            return issues.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        }

        private static void ValidateTenantScope(List<string> issues)
        {
            if (!JarvisTenantScope.IsVisible(0, 7))
                issues.Add("Tenant scope regression: global company 0 must be visible.");
            if (!JarvisTenantScope.IsVisible(7, 7))
                issues.Add("Tenant scope regression: current company must be visible.");
            if (JarvisTenantScope.IsVisible(8, 7))
                issues.Add("Tenant scope regression: foreign company must not be visible.");
        }

        private static void ValidateKnowledgeCompanion(List<string> issues)
        {
            string report = JarvisKnowledgeCompanion.BuildForTask("ReportData");
            if (string.IsNullOrWhiteSpace(report) ||
                report.IndexOf("[JARVIS_KNOWLEDGE_CONTEXT]", StringComparison.Ordinal) < 0 ||
                report.IndexOf("FINDOC", StringComparison.OrdinalIgnoreCase) < 0 ||
                report.IndexOf("TRDR", StringComparison.OrdinalIgnoreCase) < 0)
                issues.Add("Knowledge companion regression: ReportData lacks authoritative schema context.");

            issues.AddRange(JarvisKnowledgeCompanion.ValidateCoverage());
        }

        private static void ValidateLastMileContextInjection(List<string> issues)
        {
            var request = new JObject
            {
                ["system"] = new JArray(new JObject { ["type"] = "text", ["text"] = "protocol" }),
                ["metadata"] = new JObject { ["jarvis_task"] = "ReportData" },
                ["tools"] = new JArray(new JObject { ["name"] = "query_data" }),
                ["messages"] = new JArray()
            };

            string once = JarvisPolicyRequestEnricher.Apply("Atlas", request.ToString());
            string twice = JarvisPolicyRequestEnricher.Apply("Atlas", once);
            if (Count(twice, "[JARVIS_POLICY_CONTEXT]") != 1)
                issues.Add("Context injection regression: policy context must be injected exactly once.");
            if (Count(twice, "[JARVIS_KNOWLEDGE_CONTEXT]") != 1)
                issues.Add("Context injection regression: knowledge context must be injected exactly once.");
        }

        private static void ValidateActiveContextLifecycle(List<string> issues)
        {
            var context = new JarvisActiveOrchestrationContext();
            const string prompt = "architecture-regression-original";
            if (!string.Equals(context.PreparePrompt(prompt), prompt, StringComparison.Ordinal))
                issues.Add("Active context regression: closed context must be transparent.");
            if (context.HasOpenRun)
                issues.Add("Active context regression: PreparePrompt must not open unsupported runs.");

            context.Begin(prompt);
            if (!context.HasOpenRun)
                issues.Add("Active context regression: Begin must open a run.");
            string continuation = context.PreparePrompt("architecture-regression-followup");
            if (continuation.IndexOf("[JARVIS_ACTIVE_ORCHESTRATION_CONTEXT]", StringComparison.Ordinal) < 0 ||
                continuation.IndexOf(prompt, StringComparison.Ordinal) < 0)
                issues.Add("Active context regression: continuation must carry original structured context.");

            context.Complete();
            if (context.HasOpenRun)
                issues.Add("Active context regression: Complete must close the run.");
        }

        private static void ValidateExportContract(List<string> issues)
        {
            JarvisTaskDescriptor report = JarvisTaskRegistry.Find("ReportData");
            JarvisTaskDescriptor export = JarvisTaskRegistry.Find("ExportData");
            if (report == null || !(report.Produces ?? new string[0]).Contains("query_sql", StringComparer.OrdinalIgnoreCase))
                issues.Add("Export regression: ReportData must expose query_sql provenance.");
            if (export == null) issues.Add("Export regression: ExportData task is missing.");
            bool binding = JarvisDependencyBinder.AllRules.Any(x =>
                string.Equals(x.SourceTaskType, "ReportData", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(x.TargetTaskType, "ExportData", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(x.TargetInput, "source_result", StringComparison.OrdinalIgnoreCase));
            if (!binding) issues.Add("Export regression: ReportData dataset is not bound to ExportData source_result.");
        }

        private static int Count(string text, string token)
        {
            if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(token)) return 0;
            int count = 0;
            int offset = 0;
            while (true)
            {
                int index = text.IndexOf(token, offset, StringComparison.Ordinal);
                if (index < 0) return count;
                count++;
                offset = index + token.Length;
            }
        }
    }
}
