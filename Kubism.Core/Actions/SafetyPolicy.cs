using Kubism.Core.Api;
using Kubism.Core.Catalog;

namespace Kubism.Core.Actions;

public static class SafetyPolicy
{
    /// <summary>
    /// Read-only refuses every mutation. Production requires the resource name to be typed for destructive actions.
    /// <paramref name="confirmation"/> is what the user typed; <paramref name="expected"/> is the name (or count) to match.
    /// </summary>
    public static void Check(SafetyMode mode, string action, string target, bool destructive, string? confirmation, string? expected)
    {
        if (mode == SafetyMode.ReadOnly)
            throw new SafetyException($"Cluster is in read-only mode: {action} {target} refused.");
        if (mode == SafetyMode.Production && destructive && (expected is null || confirmation != expected))
            throw new SafetyException($"Production cluster: type \"{expected}\" to confirm {action} {target}.");
    }

    public static bool NeedsTypedConfirmation(SafetyMode mode, bool destructive) => mode == SafetyMode.Production && destructive;
}
