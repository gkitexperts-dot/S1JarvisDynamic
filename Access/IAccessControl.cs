namespace S1Jarvis.Access
{
    // Ίδιο interface με S1Courier.Access.IAccessControl - SYNC σκόπιμα, το
    // Soft1 host δεν παίζει καλά με async στο UI thread.
    public interface IAccessControl
    {
        AccessCheckResponse CheckAccess(AccessCheckRequest request);
    }
}
