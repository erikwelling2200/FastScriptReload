using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using UnityEditor;
using UnityEditorInternal;
using UnityEngine;
using PackageInfo = UnityEditor.PackageManager.PackageInfo;
using PackageSource = UnityEditor.PackageManager.PackageSource;

namespace FastScriptReload.Editor.AgentBridge
{
    /// <summary>
    /// Keeps Unity's auto refresh from recompiling away hot-reloaded edits, without letting any other change go unimported.
    /// </summary>
    /// <remarks>
    /// While the editor runs hot-reloaded agent edits and nothing is stale, auto refresh is held off for this session only
    /// (<see cref="AssetDatabase.DisallowAutoRefresh"/>, which is not persisted). In its place, every focus gain and every few
    /// seconds in focus, the project's files are scanned on a worker thread for anything written since the last import:
    /// <list type="bullet">
    /// <item>other assets are imported one by one, which leaves the scripts and the hot-reloaded code alone;</item>
    /// <item>a script that is not an applied agent edit, a deleted file, or anything that shapes compilation triggers the normal
    /// full refresh, because only a compile brings that into the editor.</item>
    /// </list>
    /// </remarks>
    [InitializeOnLoad]
    public static class AgentReloadAutoRefreshGuard
    {
        private const double FocusedScanSeconds = 5;

        private static readonly HashSet<string> CompilationInputs = new HashSet<string> { ".cs", ".asmdef", ".asmref", ".dll", ".rsp" };

        private const string BlockingKey = "FastScriptReload.AgentBridge.BlockingAutoRefresh";

        /// <summary>
        /// Mirrors Unity's auto-refresh block, which is a native counter that outlives the domain; kept in
        /// <see cref="SessionState"/> for the same reason, so a domain reload cannot leave a block nobody lifts.
        /// </summary>
        private static bool blocking
        {
            get => SessionState.GetBool(BlockingKey, false);
            set => SessionState.SetBool(BlockingKey, value);
        }
        private static bool wasActive;
        private static double nextFocusedScan;
        private static Task<ScanResult> scan;
        private static DateTime scanStartedUtc;

        static AgentReloadAutoRefreshGuard()
        {
            EditorApplication.update += Tick;
        }

        private static void Tick()
        {
            var shouldBlock = AgentReloadBridge.RunsOnlyAppliedEdits;
            if (shouldBlock != blocking)
            {
                if (shouldBlock) AssetDatabase.DisallowAutoRefresh();
                else AssetDatabase.AllowAutoRefresh();
                blocking = shouldBlock;
            }

            var active = InternalEditorUtility.isApplicationActive;
            var gainedFocus = active && !wasActive;
            wasActive = active;
            if (!blocking) return;

            if (scan != null)
            {
                if (scan.IsCompleted) Apply();
                return;
            }
            if (gainedFocus || (active && EditorApplication.timeSinceStartup >= nextFocusedScan))
            {
                nextFocusedScan = EditorApplication.timeSinceStartup + FocusedScanSeconds;
                StartScan();
            }
        }

        private static void StartScan()
        {
            scanStartedUtc = DateTime.UtcNow;
            var since = AgentReloadBridge.ImportedUpToUtc;
            var roots = ScanRoots();
            scan = Task.Run(() => Scan(roots, since));
        }

        private static void Apply()
        {
            var result = scan.Result;
            scan = null;
            if (result.Changed.Count == 0) return;

            // An agent edit still being hot-reloaded is decided by its answer: applied needs nothing, refused makes it stale,
            // and a stale file lifts this guard so Unity's own refresh compiles it.
            result.Changed.RemoveAll(change => !change.Deleted && change.FullPath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) && AgentReloadBridge.HasRequestInFlight(change.FullPath));
            if (result.Changed.Count == 0) return;

            var compileReason = result.Changed.Select(change => CompileReason(change)).FirstOrDefault(reason => reason != null);
            if (compileReason != null)
            {
                Debug.Log($"[Agent hot reload] {compileReason}; refreshing, which recompiles and replaces hot-reloaded code.");
                Log($"refresh: {compileReason}");
                AgentReloadBridge.MarkImported(scanStartedUtc);
                AssetDatabase.AllowAutoRefresh();
                blocking = false;
                AssetDatabase.Refresh();
                return;
            }

            // One import per asset: batching them with StartAssetEditing makes StopAssetEditing run a refresh of its own, which
            // picks up the hot-reloaded scripts and compiles them away, and can leave its import unfinished under the block.
            // A script that reaches this point is an applied agent edit: the editor already runs it, and importing it would compile it.
            var assetPaths = result.Changed.Where(change => !IsCompilationInput(change.FullPath)).Select(change => change.AssetPath).Where(path => path != null).Distinct().ToList();
            if (assetPaths.Count == 0) return;
            foreach (var path in assetPaths) AssetDatabase.ImportAsset(path);
            Log($"imported {string.Join(", ", assetPaths)} without a compile");
            AgentReloadBridge.MarkImported(scanStartedUtc);
        }

        /// <summary>The console is cleared on recompile, which is exactly when a reason is wanted; this file survives it.</summary>
        private static void Log(string line)
        {
            var path = Path.Combine(Directory.GetParent(Application.dataPath).FullName, "Library", ".agent-reload", "guard.log");
            File.AppendAllText(path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {line}\n");
        }

        /// <returns>Why this change needs a compile, or <c>null</c> when importing it on its own is enough.</returns>
        private static string CompileReason(Change change)
        {
            if (change.Deleted) return $"{change.FullPath} was deleted";
            if (change.AssetPath == null) return $"{change.FullPath} changed outside any known asset root";

            if (!IsCompilationInput(change.FullPath)) return null;
            if (change.FullPath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) && AgentReloadBridge.IsAppliedAgentEdit(change.FullPath, change.WrittenUtc)) return null;
            return $"{change.FullPath} changed and only a compile brings it into the editor";
        }

        /// <summary>Whether the file, or the asset a .meta file belongs to, feeds script compilation.</summary>
        private static bool IsCompilationInput(string fullPath)
        {
            var subject = fullPath.EndsWith(".meta", StringComparison.OrdinalIgnoreCase) ? fullPath.Substring(0, fullPath.Length - 5) : fullPath;
            return CompilationInputs.Contains(Path.GetExtension(subject).ToLowerInvariant());
        }

        /// <summary>Folders Unity imports from: Assets, and every embedded or local package, mapped to their asset paths.</summary>
        private static List<(string Physical, string Asset)> ScanRoots()
        {
            var project = Directory.GetParent(Application.dataPath).FullName;
            var roots = new List<(string, string)> { (Path.Combine(project, "Assets"), "Assets") };
            foreach (var package in PackageInfo.GetAllRegisteredPackages())
            {
                if (package.source == PackageSource.Embedded || package.source == PackageSource.Local) roots.Add((package.resolvedPath, package.assetPath));
            }
            return roots;
        }

        private static ScanResult Scan(List<(string Physical, string Asset)> roots, DateTime sinceUtc)
        {
            var result = new ScanResult();
            foreach (var (physical, asset) in roots)
            {
                if (!Directory.Exists(physical)) continue;
                Walk(new DirectoryInfo(physical), physical, asset, sinceUtc, result);
            }
            return result;
        }

        private static void Walk(DirectoryInfo directory, string physicalRoot, string assetRoot, DateTime sinceUtc, ScanResult result)
        {
            foreach (var file in directory.EnumerateFiles())
            {
                if (IsIgnoredByUnity(file.Name) || file.LastWriteTimeUtc < sinceUtc) continue;
                var relative = file.FullName.Substring(physicalRoot.Length).TrimStart('\\', '/').Replace('\\', '/');
                var assetPath = $"{assetRoot}/{relative}";
                if (assetPath.EndsWith(".meta", StringComparison.OrdinalIgnoreCase)) assetPath = assetPath.Substring(0, assetPath.Length - 5);
                result.Changed.Add(new Change { FullPath = file.FullName, AssetPath = assetPath, WrittenUtc = file.LastWriteTimeUtc });
            }

            // A deletion leaves no file to date; a .meta whose asset is gone is the trace it leaves behind.
            foreach (var meta in directory.EnumerateFiles("*.meta"))
            {
                var assetFile = meta.FullName.Substring(0, meta.FullName.Length - 5);
                if (!File.Exists(assetFile) && !Directory.Exists(assetFile)) result.Changed.Add(new Change { FullPath = assetFile, Deleted = true });
            }

            foreach (var child in directory.EnumerateDirectories())
            {
                if (IsIgnoredByUnity(child.Name)) continue;
                Walk(child, physicalRoot, assetRoot, sinceUtc, result);
            }
        }

        /// <summary>Unity skips hidden names, names ending in '~', and a few reserved ones.</summary>
        private static bool IsIgnoredByUnity(string name)
        {
            return name.StartsWith(".") || name.EndsWith("~") || name == "cvs" || name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase);
        }

        private class ScanResult
        {
            public readonly List<Change> Changed = new List<Change>();
        }

        private class Change
        {
            public string FullPath;
            public string AssetPath;
            public DateTime WrittenUtc;
            public bool Deleted;
        }
    }
}
