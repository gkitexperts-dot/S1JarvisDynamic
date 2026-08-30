namespace S1Jarvis.Core
{
    /// <summary>
    /// Numeric/tunable values that are themselves policy. Decision engines read
    /// them from here instead of owning private copies. Behavioral policy text
    /// remains in JarvisPolicyRegistry; this class is the typed parameter block
    /// of the same central Policies Inventory subsystem.
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
    }
}
