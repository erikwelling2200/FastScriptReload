using UnityEditor.Build;
using UnityEditor.Build.Reporting;

namespace FastScriptReload.Editor.AgentBridge
{
    /// <summary>
    /// Fails a build while the package cache holds checkout versions of package files, which <see cref="AgentReloadBridge"/>
    /// syncs there: the build would ship package code that was never committed, pushed and pinned.
    /// </summary>
    internal class AgentReloadBuildGate : IPreprocessBuildWithReport
    {
        public int callbackOrder => int.MinValue;

        public void OnPreprocessBuild(BuildReport report)
        {
            var diverged = AgentReloadBridge.PackageCacheDivergence();
            if (diverged.Length == 0) return;
            throw new BuildFailedException(
                "The package cache holds agent edits that differ from the pinned package versions, so this build would ship code that " +
                "was never rolled out:\n  " + string.Join("\n  ", diverged) + "\nRoll the package out (commit, push, bump the hash in " +
                "packages-lock.json), or use Tools > Agent Hot Reload > Restore Pinned Packages.");
        }
    }
}
