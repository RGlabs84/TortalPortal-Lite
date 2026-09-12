using TortalPortalLite.Core;

namespace TortalPortalLite.Core.Hooks
{
    /// <summary>
    /// Runs once, right after HookInstaller.InstallAll, and makes patch-resolution failures LOUD rather
    /// than the silent swallow SubsystemRegistry.SafePatch uses for ordinary best-effort patch sets.
    /// These particular targets are load-bearing for every engine that will ever register against them
    /// (a failed broker means every handler on it simply never fires, with no exception anywhere to
    /// notice by) - Valheim stamps every assembly AssemblyVersion 0.0.0.0, so a silently-mismatched game
    /// build is a real, previously-hit failure mode in this workspace (see
    /// libs-Tools/REFERENCE-DLL-PROVENANCE.md), not a hypothetical one.
    /// </summary>
    public static class PatchSelfTest
    {
        public static bool AllOk { get; private set; }

        public static void Run()
        {
            AllOk = true;
            foreach ((string name, bool ok) in HookInstaller.Status())
            {
                if (ok)
                {
                    PortalDebug.LogInfo($"[PatchSelfTest] OK  - {name}");
                }
                else
                {
                    AllOk = false;
                    PortalDebug.LogError($"[PatchSelfTest] FAILED - {name} did not resolve. Every engine relying on this hook will silently do nothing until this is fixed.");
                }
            }

            if (!AllOk)
            {
                bool accept = GlobalConfig.AcceptUnverifiedBuild?.Value == true;
                string verdict = accept
                    ? "AcceptUnverifiedBuild is on, so the mod continues running in this degraded state."
                    : "Set [1 - General] AcceptUnverifiedBuild=true to run anyway, at your own risk.";
                PortalDebug.LogError($"[PatchSelfTest] One or more vanilla patch targets failed to resolve - this usually means the Valheim build changed underneath this mod. {verdict}");
            }
        }
    }
}
