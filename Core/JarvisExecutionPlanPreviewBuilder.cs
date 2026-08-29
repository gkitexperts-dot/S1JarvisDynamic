namespace S1Jarvis.Core
{
    /// <summary>
    /// Narrow adapter used by the shadow coordinator. The authoritative whole
    /// plan validation remains in JarvisWholePlanValidator.
    /// </summary>
    internal static class JarvisExecutionPlanPreviewBuilder
    {
        internal static JarvisExecutionPlanPreview Build(JarvisDependencyGraph graph)
        {
            return JarvisWholePlanValidator.BuildPreview(graph);
        }
    }
}
