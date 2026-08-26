namespace S1Jarvis.UI
{
    public partial class JarvisShell
    {
        // Explicit static constructor removes beforefieldinit semantics from
        // JarvisShell. This guarantees that all static field initializers in
        // the partial class (DR router, host-safety and isolated file-picker
        // class-handler registration) run before the first JarvisShell
        // instance is created. Without this, side-effect-only static fields
        // may be initialized too late or not before Loaded, making the host
        // safety hooks nondeterministic inside the Soft1 process.
        static JarvisShell()
        {
        }
    }
}
