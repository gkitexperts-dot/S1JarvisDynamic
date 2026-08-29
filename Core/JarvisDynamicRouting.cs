using System;
using System.Collections.Generic;
using System.Data;
using Softone;

namespace S1Jarvis.Core
{
    /// <summary>
    /// Canonical Soft1/SQL names for the dynamic routing tables created in the
    /// customer database. These names intentionally mirror the real Soft1
    /// custom-table schema and must not be replaced by longer logical aliases.
    /// </summary>
    internal static class JarvisDynamicRoutingSchema
    {
        internal static class Knowledge
        {
            internal const string Table = "CCCJROUTKNOW";
            internal const string Id = "CCCJROUTKNOW";
            internal const string Company = "COMPANY";
            internal const string TaskType = "TASKTYPE";
            internal const string PatternType = "PATTERNTYPE";
            internal const string PromptText = "PROMPTTEXT";
            internal const string IntentDescription = "INTENTDESCR";
            internal const string MetadataJson = "METADATAJSON";
            internal const string Priority = "PRIORITY";
            internal const string Confidence = "CONFIDENCE";
            internal const string IsActive = "ISACTIVE";
            internal const string Source = "SOURCE";
            internal const string ApprovedBy = "APPROVEDBY";
            internal const string ApprovedAt = "APPROVEDAT";
            internal const string SuccessCount = "SUCCESSCOUNT";
            internal const string FailCount = "FAILCOUNT";
            internal const string CreatedAt = "CREATEDAT";
            internal const string UpdatedAt = "UPDATEDAT";
        }

        internal static class Candidate
        {
            internal const string Table = "CCCJROUTECAND";
            internal const string Id = "CCCJROUTECAND";
            internal const string Company = "COMPANY";
            internal const string OriginalPrompt = "ORIGINALPROMPT";
            internal const string ProposedTaskType = "PROPTASKTYPE";
            internal const string ProposedPatternType = "PROPATTERNTYPE";
            internal const string ProposedPromptText = "PROPROMPTTEXT";
            internal const string ProposedMetadataJson = "PROPMETADJSON";
            internal const string Confidence = "CONFIDENCE";
            internal const string Source = "SOURCE";
            internal const string Status = "STATUS";
            internal const string CreatedBy = "CREATEDBY";
            internal const string CreatedAt = "CREATEDAT";
            internal const string ReviewedBy = "REVIEWEDBY";
            internal const string ReviewedAt = "REVIEWEDAT";
            internal const string ReviewNotes = "REVIEWNOTES";
        }

        internal static class Log
        {
            internal const string Table = "CCCJROUTELOG";
            internal const string Id = "CCCJROUTELOG";
            internal const string Company = "COMPANY";
            internal const string UserId = "USERID";
            internal const string PromptText = "PROMPTTEXT";
            internal const string ResolutionPass = "RESOLPASS";
            internal const string ResolvedTasks = "RESOLVEDTASKS";
            internal const string MatchSource = "MATCHSOURCE";
            internal const string MatchId = "MATCHID";
            internal const string Confidence = "CONFIDENCE";
            internal const string Success = "SUCCESS";
            internal const string FailReason = "FAILREASON";
            internal const string FinalOutcome = "FINALOUTCOME";
            internal const string CreatedAt = "CREATEDAT";
        }
    }

    internal sealed class JarvisRoutingKnowledgeRecord
    {
        public int Id { get; set; }
        public int Company { get; set; }
        public string TaskType { get; set; }
        public string PatternType { get; set; }
        public string PromptText { get; set; }
        public string IntentDescription { get; set; }
        public string MetadataJson { get; set; }
        public int Priority { get; set; }
        public double Confidence { get; set; }
        public bool IsActive { get; set; }
        public string Source { get; set; }
        public int? ApprovedBy { get; set; }
        public DateTime? ApprovedAt { get; set; }
        public int SuccessCount { get; set; }
        public int FailCount { get; set; }
        public DateTime? CreatedAt { get; set; }
        public DateTime? UpdatedAt { get; set; }
    }

    internal sealed class JarvisRoutingCandidateRecord
    {
        public int Id { get; set; }
        public int Company { get; set; }
        public string OriginalPrompt { get; set; }
        public string ProposedTaskType { get; set; }
        public string ProposedPatternType { get; set; }
        public string ProposedPromptText { get; set; }
        public string ProposedMetadataJson { get; set; }
        public double Confidence { get; set; }
        public string Source { get; set; }
        public string Status { get; set; }
        public int? CreatedBy { get; set; }
        public DateTime? CreatedAt { get; set; }
        public int? ReviewedBy { get; set; }
        public DateTime? ReviewedAt { get; set; }
        public string ReviewNotes { get; set; }
    }

    internal sealed class JarvisRoutingLogRecord
    {
        public int Id { get; set; }
        public int Company { get; set; }
        public int UserId { get; set; }
        public string PromptText { get; set; }
        public short ResolutionPass { get; set; }
        public string ResolvedTasks { get; set; }
        public string MatchSource { get; set; }
        public int? MatchId { get; set; }
        public double Confidence { get; set; }
        public bool Success { get; set; }
        public string FailReason { get; set; }
        public string FinalOutcome { get; set; }
        public DateTime? CreatedAt { get; set; }
    }

    /// <summary>
    /// Read-only access to approved/active dynamic routing knowledge.
    /// Phase 1 intentionally performs no writes to CCCJROUTKNOW,
    /// CCCJROUTECAND or CCCJROUTELOG.
    ///
    /// Fail-open rule: missing tables, schema mismatch or read failure returns an
    /// empty result, so default hardcoded Task Registry routing remains usable.
    /// </summary>
    internal static class JarvisDynamicRoutingRepository
    {
        internal static IReadOnlyList<JarvisRoutingKnowledgeRecord> LoadActiveKnowledge(XSupport xSupport)
        {
            var result = new List<JarvisRoutingKnowledgeRecord>();
            if (xSupport == null || xSupport.ConnectionInfo == null)
                return result;

            int company = xSupport.ConnectionInfo.CompanyId;

            try
            {
                const string sql = @"
SELECT
    CCCJROUTKNOW,
    COMPANY,
    TASKTYPE,
    PATTERNTYPE,
    PROMPTTEXT,
    INTENTDESCR,
    METADATAJSON,
    PRIORITY,
    CONFIDENCE,
    ISACTIVE,
    SOURCE,
    APPROVEDBY,
    APPROVEDAT,
    SUCCESSCOUNT,
    FAILCOUNT,
    CREATEDAT,
    UPDATEDAT
FROM CCCJROUTKNOW
WHERE ISNULL(ISACTIVE, 0) = 1
  AND (ISNULL(COMPANY, 0) = 0 OR COMPANY = :1)
ORDER BY
    CASE WHEN COMPANY = :1 THEN 0 ELSE 1 END,
    ISNULL(PRIORITY, 0) DESC,
    ISNULL(CONFIDENCE, 0) DESC,
    CCCJROUTKNOW;";

                XTable table = xSupport.GetSQLDataSet(sql, company);
                DataTable data = table == null ? null : table.CreateDataTable(true);
                if (data == null)
                    return result;

                foreach (DataRow row in data.Rows)
                {
                    result.Add(new JarvisRoutingKnowledgeRecord
                    {
                        Id = ToInt(row[JarvisDynamicRoutingSchema.Knowledge.Id]),
                        Company = ToInt(row[JarvisDynamicRoutingSchema.Knowledge.Company]),
                        TaskType = ToStringValue(row[JarvisDynamicRoutingSchema.Knowledge.TaskType]),
                        PatternType = ToStringValue(row[JarvisDynamicRoutingSchema.Knowledge.PatternType]),
                        PromptText = ToStringValue(row[JarvisDynamicRoutingSchema.Knowledge.PromptText]),
                        IntentDescription = ToStringValue(row[JarvisDynamicRoutingSchema.Knowledge.IntentDescription]),
                        MetadataJson = ToStringValue(row[JarvisDynamicRoutingSchema.Knowledge.MetadataJson]),
                        Priority = ToInt(row[JarvisDynamicRoutingSchema.Knowledge.Priority]),
                        Confidence = ToDouble(row[JarvisDynamicRoutingSchema.Knowledge.Confidence]),
                        IsActive = ToInt(row[JarvisDynamicRoutingSchema.Knowledge.IsActive]) == 1,
                        Source = ToStringValue(row[JarvisDynamicRoutingSchema.Knowledge.Source]),
                        ApprovedBy = ToNullableInt(row[JarvisDynamicRoutingSchema.Knowledge.ApprovedBy]),
                        ApprovedAt = ToNullableDateTime(row[JarvisDynamicRoutingSchema.Knowledge.ApprovedAt]),
                        SuccessCount = ToInt(row[JarvisDynamicRoutingSchema.Knowledge.SuccessCount]),
                        FailCount = ToInt(row[JarvisDynamicRoutingSchema.Knowledge.FailCount]),
                        CreatedAt = ToNullableDateTime(row[JarvisDynamicRoutingSchema.Knowledge.CreatedAt]),
                        UpdatedAt = ToNullableDateTime(row[JarvisDynamicRoutingSchema.Knowledge.UpdatedAt])
                    });
                }
            }
            catch (Exception ex)
            {
                DebugLog.Log("[ROUTING-DYNAMIC] knowledge read unavailable; default routing remains active: " + ex.Message);
                result.Clear();
            }

            return result;
        }

        private static string ToStringValue(object value)
        {
            return value == null || value == DBNull.Value ? string.Empty : Convert.ToString(value);
        }

        private static int ToInt(object value)
        {
            return value == null || value == DBNull.Value ? 0 : Convert.ToInt32(value);
        }

        private static int? ToNullableInt(object value)
        {
            return value == null || value == DBNull.Value ? (int?)null : Convert.ToInt32(value);
        }

        private static double ToDouble(object value)
        {
            return value == null || value == DBNull.Value ? 0.0 : Convert.ToDouble(value);
        }

        private static DateTime? ToNullableDateTime(object value)
        {
            return value == null || value == DBNull.Value ? (DateTime?)null : Convert.ToDateTime(value);
        }
    }

    internal enum JarvisRoutingResolutionPass : short
    {
        DefaultMetadata = 1,
        DynamicKnowledge = 2,
        UserClarification = 3
    }

    internal sealed class JarvisRoutingResolutionState
    {
        public JarvisRoutingResolutionState(string prompt)
        {
            Prompt = prompt ?? string.Empty;
            CurrentPass = JarvisRoutingResolutionPass.DefaultMetadata;
        }

        public string Prompt { get; private set; }
        public JarvisRoutingResolutionPass CurrentPass { get; private set; }
        public bool IsResolved { get; private set; }
        public string ResolutionReason { get; private set; }

        internal void MarkResolved(string reason)
        {
            IsResolved = true;
            ResolutionReason = reason ?? string.Empty;
        }

        internal bool MoveToNextPass()
        {
            if (IsResolved)
                return false;

            switch (CurrentPass)
            {
                case JarvisRoutingResolutionPass.DefaultMetadata:
                    CurrentPass = JarvisRoutingResolutionPass.DynamicKnowledge;
                    return true;
                case JarvisRoutingResolutionPass.DynamicKnowledge:
                    CurrentPass = JarvisRoutingResolutionPass.UserClarification;
                    return true;
                default:
                    return false;
            }
        }
    }

    /// <summary>
    /// Defines the resolution order without deciding confidence thresholds yet.
    /// Thresholds/merge scoring belong to the next routing step.
    /// </summary>
    internal static class JarvisRoutingPassContract
    {
        internal static JarvisRoutingResolutionState Start(string prompt)
        {
            return new JarvisRoutingResolutionState(prompt);
        }

        internal static string Describe(JarvisRoutingResolutionPass pass)
        {
            switch (pass)
            {
                case JarvisRoutingResolutionPass.DefaultMetadata:
                    return "Hardcoded JarvisTaskRegistry metadata";
                case JarvisRoutingResolutionPass.DynamicKnowledge:
                    return "Approved active CCCJROUTKNOW knowledge";
                case JarvisRoutingResolutionPass.UserClarification:
                    return "Explicit user clarification; never guess unresolved intent";
                default:
                    return "Unknown routing pass";
            }
        }
    }
}
