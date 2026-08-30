namespace S1Jarvis.Core
{
    internal static class JarvisTenantScope
    {
        internal static bool IsVisible(int rowCompany, int currentCompany)
        {
            return rowCompany == 0 || rowCompany == currentCompany;
        }
    }
}
