namespace Leaf.Plugins.LeafCommons;

/// <summary>
/// Starts background work that must not run as the request that happened to start it. A
/// Task.Run inside a request carries that request's ambient state: its HttpContext while the
/// request lasts, and RedLeaf's execution scope for good. A settled reply's review would then act
/// for whoever RedCompute's settle callback is signed in as (the last browser user, or
/// LocalDefault), and a connector loop for whoever saved the settings that started it.
/// </summary>
internal static class Detached
{
    public static Task Run(Func<Task> work)
    {
        if (ExecutionContext.IsFlowSuppressed()) return Task.Run(work);
        using (ExecutionContext.SuppressFlow()) return Task.Run(work);
    }
}
