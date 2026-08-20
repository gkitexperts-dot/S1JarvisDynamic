using Softone;

namespace S1Jarvis.SoftoneIntegration
{
    // Σημείο εισόδου - φορτώνεται από το Soft1 όταν κάνει load το NETDLL
    // (ίδιο μοτίβο με το S1Init του S1Courier).
    //
    // ΠΡΟΣΟΧΗ: αν το S1Jarvis.dll καταχωρηθεί ΔΙΠΛΑ στο ήδη υπάρχον NETDLL
    // (S1Courier), πρέπει πρώτα να επιβεβαιωθεί ότι η έκδοση του Soft1 σας
    // δέχεται πάνω από ένα NETDLL entry. Αν όχι, οι δύο θα πρέπει τελικά να
    // ζήσουν στο ίδιο assembly (βλ. README.md).
    public class S1Init : TXCode
    {
        public override void Initialize()
        {
            base.Initialize();
            JarvisCore.XSupport = XSupport;
        }
    }

    public static class JarvisCore
    {
        public static XSupport XSupport { get; set; }
    }
}
