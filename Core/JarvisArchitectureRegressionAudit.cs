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
            ValidateControlledPilotRoutingBoundary(issues);
            ValidateExportContract(issues);
            ValidateStructuredDocumentScope(issues);
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
                report.IndexOf("TRDR", StringComparison.OrdinalIgnoreCase) < 0 ||
                report.IndexOf("SERIES", StringComparison.OrdinalIgnoreCase) < 0 ||
                report.IndexOf("NAME", StringComparison.OrdinalIgnoreCase) < 0)
                issues.Add("Knowledge companion regression: ReportData lacks authoritative schema/document-type context.");

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
            if (!context.HasOpenRun || string.IsNullOrWhiteSpace(context.RunId))
                issues.Add("Active context regression: Begin must open a run with lineage id.");
            string continuation = context.PreparePrompt("architecture-regression-followup");
            if (continuation.IndexOf("[JARVIS_ACTIVE_ORCHESTRATION_CONTEXT]", StringComparison.Ordinal) < 0 ||
                continuation.IndexOf(prompt, StringComparison.Ordinal) < 0)
                issues.Add("Active context regression: continuation must carry original structured context.");

            context.Complete();
            if (context.HasOpenRun)
                issues.Add("Active context regression: Complete must close the run.");
        }

        private static void ValidateControlledPilotRoutingBoundary(List<string> issues)
        {
            // Ordinary free-form conversation must stay on the normal processing
            // path and must not trigger a second semantic-provider call merely
            // because NEW routing is enabled.
            if (JarvisExecutionShadowHarness.ShouldAttemptControlledPilot("πως σε λενε;"))
                issues.Add("Routing regression: ordinary conversation must not invoke the controlled semantic planner.");

            // Promoted tasks are identified from the authoritative task registry,
            // not a private UI keyword table. Keep one representative read and one
            // representative external-action assertion here.
            if (!JarvisExecutionShadowHarness.ShouldAttemptControlledPilot("sales report for this month"))
                issues.Add("Routing regression: promoted ReportData intent must invoke the controlled semantic planner.");
            if (!JarvisExecutionShadowHarness.ShouldAttemptControlledPilot("send email to the customer"))
                issues.Add("Routing regression: promoted SendEmail intent must invoke the controlled semantic planner.");

            // Once Jarvis owns an orchestration run, every continuation belongs to
            // that run even when the follow-up wording itself contains no task hint.
            var activeContext = new JarvisActiveOrchestrationContext();
            activeContext.Begin("sales report for this month");
            if (!JarvisExecutionShadowHarness.ShouldAttemptControlledPilot("και στειλτο και στον Γιωργο", activeContext))
                issues.Add("Routing regression: an open Jarvis orchestration run must remain planner-owned for continuation turns.");
            activeContext.Complete();
        }

        private static void ValidateExportContract(List<string> issues)
        {
            JarvisTaskDescriptor report = JarvisTaskRegistry.Find("ReportData");
            JarvisTaskDescriptor export = JarvisTaskRegistry.Find("ExportData");
            if (report == null || !(report.Produces ?? new string[0]).Contains("query_sql", StringComparer.OrdinalIgnoreCase))
                issues.Add("Export regression: ReportData must expose query_sql provenance.");
            if (report != null && !(report.OptionalInputs ?? new string[0]).Contains("operator_scope", StringComparer.OrdinalIgnoreCase))
                issues.Add("Runtime regression: ReportData must accept structured operator_scope.");
            if (report != null && !(report.OptionalInputs ?? new string[0]).Contains("document_scope", StringComparer.OrdinalIgnoreCase))
                issues.Add("Document regression: ReportData must accept structured document_scope.");

            if (export == null) issues.Add("Export regression: ExportData task is missing.");
            else if (!(export.RequiredInputs ?? new string[0]).Contains("export_request", StringComparer.OrdinalIgnoreCase))
                issues.Add("Export regression: ExportData must be autonomous through export_request.");
            else if ((export.RequiredInputs ?? new string[0]).Contains("source_result", StringComparer.OrdinalIgnoreCase))
                issues.Add("Export regression: source_result must be optional/upstream-bound, not mandatory.");
            if (export != null && !(export.OptionalInputs ?? new string[0]).Contains("document_scope", StringComparer.OrdinalIgnoreCase))
                issues.Add("Export regression: ExportData must accept structured document_scope.");
            if (export != null && !(export.OptionalInputs ?? new string[0]).Contains("entity_role", StringComparer.OrdinalIgnoreCase))
                issues.Add("Export regression: ExportData must accept structured entity_role.");

            bool binding = JarvisDependencyBinder.AllRules.Any(x =>
                string.Equals(x.SourceTaskType, "ReportData", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(x.TargetTaskType, "ExportData", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(x.TargetInput, "source_result", StringComparison.OrdinalIgnoreCase));
            if (!binding) issues.Add("Export regression: ReportData dataset is not bound to ExportData source_result.");
        }

        private static void ValidateStructuredDocumentScope(List<string> issues)
        {
            // FPRMS is the authoritative document-category discriminator. SERIES
            // is descriptive metadata only, so smoke-test fixtures must exercise
            // the same contract as production validation.
            string invoiceDataset = new JObject
            {
                ["rows"] = new JArray(new JObject
                {
                    ["FPRMS_NAME"] = "Τιμολόγιο",
                    ["SERIES_NAME"] = "Τιμολόγια Πωλήσεων"
                })
            }.ToString();
            if (JarvisDocumentScopeValidator.Validate("invoice", invoiceDataset).Length != 0)
                issues.Add("Document regression: invoice scope rejected an invoice FPRMS row.");

            string mixedDataset = new JObject
            {
                ["rows"] = new JArray(
                    new JObject { ["FPRMS_NAME"] = "Τιμολόγιο", ["SERIES_NAME"] = "Τιμολόγια Πωλήσεων" },
                    new JObject { ["FPRMS_NAME"] = "Δελτίο Αποστολής", ["SERIES_NAME"] = "Δελτία Αποστολής" })
            }.ToString();
            if (JarvisDocumentScopeValidator.Validate("invoice", mixedDataset).Length == 0)
                issues.Add("Document regression: invoice scope accepted a delivery-note FPRMS row.");

            string creditInvoiceDataset = new JObject
            {
                ["rows"] = new JArray(new JObject
                {
                    ["FPRMS_NAME"] = "Πιστωτικό Τιμολόγιο",
                    ["SERIES_NAME"] = "Πιστωτικά Πωλήσεων"
                })
            }.ToString();
            if (JarvisDocumentScopeValidator.Validate("invoice", creditInvoiceDataset).Length == 0)
                issues.Add("Document regression: invoice scope accepted a credit-invoice FPRMS row.");
            if (JarvisDocumentScopeValidator.Validate("credit", creditInvoiceDataset).Length != 0)
                issues.Add("Document regression: credit scope rejected a credit-invoice FPRMS row.");

            string unverifiableDataset = new JObject
            {
                ["rows"] = new JArray(new JObject { ["FINCODE"] = "X" })
            }.ToString();
            if (JarvisDocumentScopeValidator.Validate("invoice", unverifiableDataset).Length == 0)
                issues.Add("Document regression: specific scope must fail closed without authoritative FPRMS metadata.");
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
