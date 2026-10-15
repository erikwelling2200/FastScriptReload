using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using FastScriptReload.Editor.Compilation;
using FastScriptReload.Runtime;
using ImmersiveVRTools.Runtime.Common;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEngine;
using Debug = UnityEngine.Debug;
using CompilationAssembly = UnityEditor.Compilation.Assembly;
using PackageInfo = UnityEditor.PackageManager.PackageInfo;
using PackageSource = UnityEditor.PackageManager.PackageSource;

namespace FastScriptReload.Editor.AgentBridge
{
    /// <summary>
    /// Hot-reloads single files on request of a coding agent and answers, per request, whether the editor now runs the new code.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Requests and answers are files under <c>Library/.agent-reload/</c>, written by the <c>agent-reload</c> hook of the
    /// changefied Claude plugin. A request names one edited file and a snapshot of that file as it was when the editor last
    /// compiled. <see cref="AgentReloadGate"/> compares the two; only an edit it proves safe is compiled and redirected, and only a
    /// redirect that left nothing behind is answered "Applied". Everything else is answered "RecompileNeeded" and the file is
    /// remembered as stale, so every later answer can list what the editor is still missing.
    /// </para>
    /// <para>
    /// An edit to a package's own checkout reaches this editor too when it consumes that package from the package cache. The
    /// cached copy is what this editor compiled, so it serves as the snapshot and the checkout's version is patched in on top.
    /// Whatever the gate decides, the checkout file is then copied into the package cache as well, so a recompile keeps the
    /// edit instead of loading the pinned version again, and an edit the gate refuses only needs a recompile rather than a
    /// rollout. The pinned original is kept under <c>Library/.agent-reload/pristine/</c>; while any cached file differs from
    /// it, every answer lists it and <see cref="AgentReloadBuildGate"/> fails builds, because a build would ship package code
    /// that was never rolled out. A rollout moves the pin to a fresh cache folder, which ends the divergence by itself;
    /// <see cref="RestorePinnedPackages"/> ends it without one. Package Manager may extract a cache folder anew at any time, so
    /// every domain reload offers every synced file again. Editors find each other through a per-user registry under
    /// <c>%LOCALAPPDATA%/Changefied/agent-reload/editors/</c>.
    /// </para>
    /// <para>
    /// The bridge drives Fast Script Reload's compiler and method redirector itself. FSR's own file watchers and auto reload
    /// are switched off, so nothing is hot-reloaded without passing the gate.
    /// </para>
    /// </remarks>
    [InitializeOnLoad]
    public static class AgentReloadBridge
    {
        private const double HeartbeatSeconds = 1;
        private const int MaxPatchesPerCompile = 40;
        private const int InlineSizeMargin = 40;
        private const double UnreadResultMinutes = 10;
        private const double InFlightMarkerMinutes = 2;
        private const string SessionKey = "FastScriptReload.AgentBridge.Session";

        private static readonly string ProjectPath = Directory.GetParent(Application.dataPath).FullName;
        private static readonly string Root = Path.Combine(ProjectPath, "Library", ".agent-reload");
        private static readonly string StatePath = Path.Combine(Root, "state.json");
        private static readonly string RequestsPath = Path.Combine(Root, "requests");
        private static readonly string ResultsPath = Path.Combine(Root, "results");
        private static readonly string BaselinesPath = Path.Combine(Root, "baseline");
        private static readonly string InFlightPath = Path.Combine(Root, "inflight");
        private static readonly string SyncedPath = Path.Combine(Root, "synced.json");
        private static readonly string PristinePath = Path.Combine(Root, "pristine");
        private static readonly string RegistryPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Changefied", "agent-reload", "editors", Hash(Normalise(ProjectPath)) + ".json");

        private static Session session;
        private static double nextHeartbeat;
        private static Dictionary<string, CompilationAssembly> assemblyByFile;
        private static string[] cachedPackages;
        private static Pending pending;
        private static Result current;

        /// <summary>True while the editor runs hot-reloaded agent edits and lacks none, so a compile would only cost time.</summary>
        internal static bool RunsOnlyAppliedEdits => session.patched.Count > 0 && session.stale.Count == 0;

        /// <summary>Every file written before this moment is known to the asset database.</summary>
        internal static DateTime ImportedUpToUtc => session.ImportedUpTo;

        internal static void MarkImported(DateTime importedUpToUtc)
        {
            if (importedUpToUtc <= session.ImportedUpTo) return;
            session.importedUtc = importedUpToUtc.ToString("o");
            SaveSession(session);
        }

        /// <summary>Whether the editor already runs this project file as written at <paramref name="writtenUtc"/>, through a hot reload.</summary>
        internal static bool IsAppliedAgentEdit(string file, DateTime writtenUtc)
        {
            var path = Normalise(Path.GetFullPath(file));
            return session.patched.Any(entry => string.IsNullOrEmpty(entry.package) && Normalise(entry.file) == path && ParseUtc(entry.appliedUtc) >= writtenUtc);
        }

        /// <summary>Whether an agent request for this file is queued or being processed, so its outcome is not known yet.</summary>
        internal static bool HasRequestInFlight(string file)
        {
            var path = Normalise(Path.GetFullPath(file));
            if (pending != null && Normalise(pending.Result.file) == path) return true;
            // The hook marks the file before the edit, closing the gap until its request is written.
            var marker = new FileInfo(InFlightMarker(path));
            if (marker.Exists && (DateTime.UtcNow - marker.LastWriteTimeUtc).TotalMinutes < InFlightMarkerMinutes) return true;
            foreach (var request in new DirectoryInfo(RequestsPath).GetFiles("*.json"))
            {
                try
                {
                    if (Normalise(Path.GetFullPath(JsonUtility.FromJson<Request>(File.ReadAllText(request.FullName)).file)) == path) return true;
                }
                catch (IOException)
                {
                    // Taken by the bridge between listing and reading; it is in flight either way.
                    return true;
                }
            }
            return false;
        }

        static AgentReloadBridge()
        {
            Directory.CreateDirectory(RequestsPath);
            Directory.CreateDirectory(ResultsPath);
            Directory.CreateDirectory(InFlightPath);
            Directory.CreateDirectory(Path.GetDirectoryName(RegistryPath));
            DisableFastScriptReloadAutomation();
            session = StartSession();
            CompilationPipeline.compilationStarted += OnCompilationStarted;
            CompilationPipeline.assemblyCompilationFinished += OnAssemblyCompilationFinished;
            EditorApplication.update += Tick;
            EditorApplication.quitting += () => File.Delete(RegistryPath);
        }

        /// <summary>FSR's own watchers would hot-reload edits that never passed the gate.</summary>
        private static void DisableFastScriptReloadAutomation()
        {
            FastScriptReloadPreference.EnableAutoReloadForChangedFiles.SetEditorPersistedValue(false);
            FastScriptReloadPreference.EnableExperimentalEditorHotReloadSupport.SetEditorPersistedValue(false);
            // Added-field support rewrites every patched type with extra fields, which the redirector's field check then refuses.
            FastScriptReloadPreference.EnableExperimentalAddedFieldsSupport.SetEditorPersistedValue(false);
            FastScriptReloadPreference.IsDidFieldsOrPropertyCountChangedCheckDisabled.SetEditorPersistedValue(false);
            // That dialog is modal and blocks the editor; AgentReloadAutoRefreshGuard decides about refreshes instead.
            FastScriptReloadPreference.StopShowingAutoReloadEnabledDialogBox.SetEditorPersistedValue(true);
        }

        /// <summary>
        /// Opens the compile epoch this domain belongs to. Code loaded by this domain is the source as it was when the last
        /// successful compile started; a domain reload, with or without a compile, drops every redirect.
        /// </summary>
        private static Session StartSession()
        {
            var loaded = LoadSession();
            // Package edits are offered again from the synced list below, which also carries the branch the gate checks.
            var requeue = loaded.patched.Where(entry => string.IsNullOrEmpty(entry.package)).ToList();
            if (!string.IsNullOrEmpty(loaded.pendingCompileStartUtc))
            {
                // The compile loaded every project file from disk. A package edit that could not be synced is still not in it, but
                // one whose checkout matches the cache now is: nothing would ever offer it again, so it would stay listed for good.
                requeue.Clear();
                loaded = new Session { epochUtc = loaded.pendingCompileStartUtc, stale = loaded.stale.Where(entry => !string.IsNullOrEmpty(entry.package) && !CacheMatchesCheckout(entry)).ToList() };
            }
            else if (string.IsNullOrEmpty(loaded.epochUtc))
            {
                loaded = new Session { epochUtc = Process.GetCurrentProcess().StartTime.ToUniversalTime().ToString("o") };
            }
            // The compile, if any, loaded the synced cache files, unless Package Manager extracted the folder anew; a domain reload
            // without one dropped their patches. Either way the gate decides again, and answers NoCodeChange to what is in place.
            var synced = LoadSynced();
            foreach (var entry in requeue) MarkStale(loaded, entry.file, entry.package, entry.packagePath, "a recompile or domain reload dropped its hot-reloaded change");
            foreach (var entry in synced) MarkStale(loaded, entry.checkoutFile, entry.package, entry.packagePath, "a recompile or domain reload happened since it was synced; it is being checked again");
            loaded.patched.Clear();
            SaveSession(loaded);

            foreach (var directory in Directory.Exists(BaselinesPath) ? Directory.GetDirectories(BaselinesPath) : Array.Empty<string>())
            {
                if (Path.GetFileName(directory) != loaded.EpochId) Directory.Delete(directory, true);
            }

            var requests = requeue.Select(entry => new Request { file = entry.file, baseline = BaselineFor(loaded, entry.file) })
                .Concat(synced.Select(entry => new Request { file = entry.checkoutFile, package = entry.package, packagePath = entry.packagePath, checkoutBranch = entry.checkoutBranch }));
            foreach (var request in requests)
            {
                request.id = "reapply-" + Guid.NewGuid().ToString("N");
                WriteAtomically(Path.Combine(RequestsPath, request.id + ".json"), JsonUtility.ToJson(request));
            }
            return loaded;
        }

        private static void OnCompilationStarted(object context)
        {
            session.pendingCompileStartUtc = DateTime.UtcNow.ToString("o");
            SaveSession(session);
        }

        private static void OnAssemblyCompilationFinished(string assembly, CompilerMessage[] messages)
        {
            if (messages.All(message => message.type != CompilerMessageType.Error)) return;
            // A failed compile reloads nothing, so the epoch stays where it was.
            session.pendingCompileStartUtc = null;
            SaveSession(session);
        }

        private static void Tick()
        {
            try
            {
                if (pending != null) FinishPending();
                else if (!EditorApplication.isCompiling && !EditorApplication.isUpdating) StartNextRequest();
            }
            catch (Exception exception)
            {
                // One failing request must not stop the bridge, and its hook must not wait for an answer that never comes.
                Debug.LogException(exception);
                pending = null;
                if (current != null) Reject(current, "the agent bridge failed while processing this edit: " + Shorten(exception.Message));
            }
            current = null;

            if (EditorApplication.timeSinceStartup < nextHeartbeat) return;
            nextHeartbeat = EditorApplication.timeSinceStartup + HeartbeatSeconds;
            WriteState();
            DeleteUnreadResults();
        }

        private static void StartNextRequest()
        {
            var next = new DirectoryInfo(RequestsPath).GetFiles("*.json").OrderBy(file => file.LastWriteTimeUtc).FirstOrDefault();
            if (next == null) return;

            string json;
            try
            {
                json = File.ReadAllText(next.FullName);
            }
            finally
            {
                next.Delete();
            }
            Request request;
            try
            {
                request = JsonUtility.FromJson<Request>(json);
            }
            catch (ArgumentException exception)
            {
                Debug.LogError($"[Agent hot reload] ignored malformed request {next.Name}: {exception.Message}");
                return;
            }
            var file = Path.GetFullPath(request.file);
            var package = string.IsNullOrEmpty(request.package) ? null : request.package;
            var result = new Result { id = request.id, file = file, package = package, packagePath = package == null ? null : request.packagePath, project = ProjectPath };
            current = result;

            string compiledFile, compiledSource;
            if (package == null)
            {
                if (!File.Exists(file))
                {
                    Reject(result, "the file was deleted, which only a full compile applies");
                    return;
                }
                var unavailable = UnavailableReason();
                if (unavailable != null)
                {
                    Reject(result, unavailable);
                    return;
                }
                compiledFile = file;
                if (File.GetLastWriteTimeUtc(file) < session.EpochStart && !session.IsTracked(file))
                {
                    Answer(result, "Compiled", "a full compile that started after this edit already loaded it");
                    return;
                }
                if (string.IsNullOrEmpty(request.baseline) || !File.Exists(request.baseline) || Path.GetFileName(Path.GetDirectoryName(request.baseline)) != session.EpochId)
                {
                    Reject(result, "there is no snapshot of this file from the last compile (it is new, or it changed outside agent edits since that compile)");
                    return;
                }
                compiledSource = File.ReadAllText(request.baseline);
            }
            else
            {
                var info = PackageInfo.GetAllRegisteredPackages().FirstOrDefault(candidate => candidate.name == package);
                if (info == null)
                {
                    Answer(result, "NotInProject", $"this project does not use package {package}");
                    return;
                }
                if (info.source == PackageSource.Embedded || info.source == PackageSource.Local)
                {
                    Answer(result, "NotInProject", $"this project compiles its own copy of {package} at {info.resolvedPath}, not this checkout");
                    return;
                }
                // The checkout may be on a branch this project does not consume; its code is then not this project's to run.
                var hash = info.packageId.LastIndexOf('#');
                var pinnedBranch = hash < 0 ? null : info.packageId.Substring(hash + 1);
                if (pinnedBranch == null || pinnedBranch != request.checkoutBranch)
                {
                    var pinned = pinnedBranch == null ? "without naming a branch" : $"from branch {pinnedBranch}";
                    Answer(result, "OtherBranch", $"this project uses {package} {pinned}, but the checkout is on {(string.IsNullOrEmpty(request.checkoutBranch) ? "an unknown branch" : request.checkoutBranch)}, so this edit is not this project's code");
                    return;
                }
                compiledFile = Path.GetFullPath(Path.Combine(info.resolvedPath, request.packagePath));
                // From here on every answer copies the checkout file into the cache, whatever the verdict.
                result.cacheRoot = Path.GetFullPath(info.resolvedPath);
                result.checkoutBranch = request.checkoutBranch;
                if (!File.Exists(file))
                {
                    Reject(result, File.Exists(compiledFile) ? "the file was deleted, which only a recompile applies" : "the file was deleted and is not in this editor's package cache either");
                    return;
                }
                if (!File.Exists(compiledFile))
                {
                    Reject(result, $"the file is new to {package}, which only a recompile applies");
                    return;
                }
                compiledSource = CompiledPackageSource(compiledFile);
                if (compiledSource == null)
                {
                    Reject(result, "the cached copy of this file changed since the last compile without being synced from the checkout, so what the editor runs is unknown");
                    return;
                }
                var unavailable = UnavailableReason();
                if (unavailable != null)
                {
                    Reject(result, unavailable);
                    return;
                }
            }

            if (!AssemblyByFile().TryGetValue(Normalise(compiledFile), out var assembly))
            {
                Answer(result, "NotInProject", "the file is not compiled into any assembly of this project");
                return;
            }

            var verdict = AgentReloadGate.Analyse(compiledSource, File.ReadAllText(file), assembly.defines);
            // An earlier patch in this domain keeps running unless the file is patched back to what was compiled.
            if (verdict.NoCodeChange && !session.IsPatched(file))
            {
                session.stale.RemoveAll(entry => entry.file == file);
                SaveSession(session);
                Answer(result, "NoCodeChange", package == null ? "the code is identical to what the editor compiled" : "the code is identical to the package code this editor compiled");
                return;
            }
            if (CompilationPipeline.codeOptimization == CodeOptimization.Release) CheckInlining(verdict, assembly);
            if (verdict.Violations.Count > 0)
            {
                Reject(result, verdict.Violations.ToArray());
                return;
            }
            if (session.patchCount >= MaxPatchesPerCompile)
            {
                Reject(result, $"{MaxPatchesPerCompile} hot reloads since the last compile; recompile to unload the generated assemblies");
                return;
            }

            DynamicCompilationBase.LogHowToFixMessageOnCompilationError = false;
            // The rewriter reads these from FSR's manager; they must match the options the redirector is given below.
            FastScriptReloadManager.Instance.AssemblyChangesLoaderEditorOptionsNeededInBuild.UpdateValues(false, false);
            var dispatcher = UnityMainThreadDispatcher.Instance.EnsureInitialized();
            // Their static constructors read editor state, which is only allowed on the main thread.
            System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(typeof(DynamicCompilationBase).TypeHandle);
            System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(typeof(DotnetExeDynamicCompilation).TypeHandle);
            var defines = assembly.defines.ToList();
            pending = new Pending
            {
                SourceReadUtc = DateTime.UtcNow,
                Result = result,
                Verdict = verdict,
                Compile = Task.Run(() => DynamicAssemblyCompiler.Compile(new List<string> { file }, dispatcher, defines)),
            };
        }

        private static void FinishPending()
        {
            if (!pending.Compile.IsCompleted) return;
            var (result, verdict, compile, appliedUtc) = (pending.Result, pending.Verdict, pending.Compile, pending.SourceReadUtc);
            pending = null;
            current = result;

            if (compile.IsFaulted || compile.Result.IsError)
            {
                var error = compile.IsFaulted ? compile.Exception.GetBaseException().Message : string.Join("\n", compile.Result.MessagesFromCompilerProcess);
                Reject(result, "Fast Script Reload could not compile its patched copy of the file. Either the code has the error below, or it uses a pattern FSR cannot rewrite; a full compile tells which: " + Shorten(error));
                return;
            }

            var loader = AssemblyChangesLoader.Instance;
            try
            {
                loader.DynamicallyUpdateMethodsForCreatedAssembly(compile.Result.CompiledAssembly, new AssemblyChangesLoaderEditorOptionsNeededInBuild(false, false));
            }
            catch (Exception exception)
            {
                Reject(result, "redirecting methods failed part-way, so the editor may run a mix of old and new code: " + Shorten(exception.Message));
                return;
            }
            session.patchCount++;

            var blocking = loader.LastDetourIssues.Where(issue => Concerns(issue, verdict.ChangedMembers)).Select(issue => issue.ToString()).ToArray();
            if (blocking.Length > 0)
            {
                Reject(result, blocking.Select(issue => "not redirected: " + issue).ToArray());
                return;
            }

            session.patched.RemoveAll(entry => entry.file == result.file);
            // Patched back to exactly what was compiled: the editor runs the compiled code again, so there is nothing left to track.
            var revertedToCompiled = verdict.ChangedMembers.Count == 0;
            if (!revertedToCompiled) session.patched.Add(new PatchedFile { file = result.file, package = result.package, packagePath = result.packagePath, appliedUtc = appliedUtc.ToString("o") });
            session.stale.RemoveAll(entry => entry.file == result.file);
            SaveSession(session);
            result.caveats = verdict.Caveats.ToArray();
            if (revertedToCompiled) Answer(result, "Reverted", "the earlier hot patch is undone; the editor runs the compiled code again");
            else Answer(result, "Applied", "redirected " + string.Join(", ", verdict.ChangedMembers.Select(member => member.DisplayName)));
        }

        /// <summary>An unredirected item matters when it is a changed member, its declaring type, or code generated for it.</summary>
        private static bool Concerns(DetourIssue issue, List<AgentReloadGate.ChangedMember> changed)
        {
            return changed.Any(member =>
            {
                if (issue.TypeFullName == member.TypeMetadataName)
                {
                    return issue.MemberName == null || member.RuntimeNames.Any(name => issue.MemberName == name || issue.MemberName.EndsWith("." + name) || issue.MemberName.Contains("<" + name + ">"));
                }
                return issue.TypeFullName.StartsWith(member.TypeMetadataName + "+<") && member.RuntimeNames.Any(name => issue.TypeFullName.Contains("<" + name + ">"));
            });
        }

        /// <summary>
        /// With Release code optimization the JIT inlines small non-virtual methods into their callers, where a redirect never
        /// reaches. Mono's limit is 20 bytes of IL; anything near it is refused.
        /// </summary>
        private static void CheckInlining(AgentReloadGate.Verdict verdict, CompilationAssembly assembly)
        {
            var loaded = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(candidate => candidate.GetName().Name == assembly.name);
            foreach (var member in verdict.ChangedMembers)
            {
                var type = loaded?.GetType(member.TypeMetadataName);
                var methods = type?.GetMethods(BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                    .Where(method => member.RuntimeNames.Any(name => method.Name == name || method.Name.EndsWith("." + name))).ToList();
                if (member.IsOperator || methods == null || methods.Count == 0)
                {
                    verdict.Violations.Add($"{member.DisplayName}: cannot rule out inlining under Release code optimization (switch the editor to Debug mode)");
                }
                else if (methods.Any(MayBeInlined))
                {
                    verdict.Violations.Add($"{member.DisplayName} is small enough to be inlined under Release code optimization (switch the editor to Debug mode)");
                }
            }
        }

        private static bool MayBeInlined(MethodInfo method)
        {
            var flags = method.GetMethodImplementationFlags();
            if ((flags & MethodImplAttributes.NoInlining) != 0) return false;
            if (method.IsVirtual && !method.IsFinal) return false;
            var body = method.GetMethodBody();
            if (body == null || body.ExceptionHandlingClauses.Count > 0) return false;
            return (flags & MethodImplAttributes.AggressiveInlining) != 0 || body.GetILAsByteArray().Length < InlineSizeMargin;
        }

        /// <returns>Why no edit can be hot-reloaded right now, or <c>null</c>.</returns>
        private static string UnavailableReason()
        {
            var hotReload = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(candidate => candidate.GetName().Name == "SingularityGroup.HotReload.Editor");
            var running = hotReload?.GetType("SingularityGroup.HotReload.Editor.EditorCodePatcher")?.GetProperty("Running", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
            if (running != null && (bool)running.GetValue(null)) return "Hot Reload is running in this editor; two hot reloaders patching the same methods cannot be trusted";
            return null;
        }

        private static Dictionary<string, CompilationAssembly> AssemblyByFile()
        {
            if (assemblyByFile != null) return assemblyByFile;
            assemblyByFile = new Dictionary<string, CompilationAssembly>();
            foreach (var assembly in CompilationPipeline.GetAssemblies(AssembliesType.Editor))
            {
                foreach (var source in assembly.sourceFiles) assemblyByFile[Normalise(Path.GetFullPath(FileUtil.GetPhysicalPath(source)))] = assembly;
            }
            return assemblyByFile;
        }

        /// <summary>Packages this editor compiles from the package cache, so edits to their checkouts can be patched in here.</summary>
        private static string[] CachedPackages()
        {
            return cachedPackages ?? (cachedPackages = PackageInfo.GetAllRegisteredPackages()
                .Where(package => package.source != PackageSource.Embedded && package.source != PackageSource.Local && package.source != PackageSource.BuiltIn)
                .Select(package => package.name).ToArray());
        }

        /// <summary>The hook's in-flight marker for a normalised path; the hook derives the same name.</summary>
        private static string InFlightMarker(string normalisedPath) => Path.Combine(InFlightPath, Hash(normalisedPath));

        /// <summary>Where the hook snapshots <paramref name="file"/> for this epoch; the hook derives the same path.</summary>
        private static string BaselineFor(Session target, string file)
        {
            return Path.Combine(BaselinesPath, target.EpochId, Hash(Normalise(Path.GetFullPath(file))) + ".cs");
        }

        private static string Normalise(string path) => path.Replace('\\', '/').ToLowerInvariant();

        private static string Hash(string text)
        {
            using (var sha1 = SHA1.Create())
            {
                return string.Concat(sha1.ComputeHash(Encoding.UTF8.GetBytes(text)).Select(b => b.ToString("x2")));
            }
        }

        private static DateTime ParseUtc(string value) => DateTime.Parse(value, null, System.Globalization.DateTimeStyles.RoundtripKind);

        private static void Reject(Result result, params string[] reasons)
        {
            MarkStale(session, result.file, result.package, result.packagePath, reasons[0]);
            SaveSession(session);
            Answer(result, "RecompileNeeded", reasons);
        }

        private static void MarkStale(Session target, string file, string package, string packagePath, string reason)
        {
            target.stale.RemoveAll(entry => entry.file == file);
            target.stale.Add(new StaleFile { file = file, package = package, packagePath = packagePath, reason = reason });
        }

        private static void Answer(Result result, string verdict, params string[] reasons)
        {
            result.verdict = verdict;
            result.reasons = reasons;
            result.caveats = result.caveats ?? Array.Empty<string>();
            if (!string.IsNullOrEmpty(result.cacheRoot))
            {
                var error = SyncToCache(result);
                result.cacheSynced = error == null;
                if (error != null) result.reasons = reasons.Append("it was not copied into this editor's package cache, so only a package rollout brings it in: " + error).ToArray();
            }
            result.diverged = PackageCacheDivergence();
            result.stale = session.stale.Where(entry => entry.file != result.file).ToArray();
            result.epochId = session.EpochId;
            WriteAtomically(Path.Combine(ResultsPath, result.id + ".json"), JsonUtility.ToJson(result, true));
            File.Delete(InFlightMarker(Normalise(result.file)));
        }

        /// <returns>
        /// The cached file as this editor compiled it: its snapshot when it was synced over since, otherwise the file itself, or
        /// <c>null</c> when it changed since the compile without a snapshot being taken.
        /// </returns>
        private static string CompiledPackageSource(string cacheFile)
        {
            var snapshot = BaselineFor(session, cacheFile);
            if (File.Exists(snapshot)) return File.ReadAllText(snapshot);
            return File.GetLastWriteTimeUtc(cacheFile) < session.EpochStart ? File.ReadAllText(cacheFile) : null;
        }

        /// <summary>Copies the checkout's version of the file over this editor's package cache, keeping the pinned original.</summary>
        /// <returns>Why the file could not be copied, or <c>null</c>.</returns>
        private static string SyncToCache(Result result)
        {
            var cacheFile = Path.Combine(result.cacheRoot, result.packagePath);
            var inCheckout = File.Exists(result.file);
            var inCache = File.Exists(cacheFile);
            if (inCheckout && inCache && File.ReadAllBytes(result.file).SequenceEqual(File.ReadAllBytes(cacheFile))) return null;
            if (!inCheckout && !inCache) return null;
            // Unity ignores a file without a .meta in an immutable package, and cannot write one there itself.
            if (inCheckout && !inCache && !File.Exists(result.file + ".meta")) return "it has no .meta file in the checkout yet; open the package project so Unity creates one, then edit the file again";

            try
            {
                var synced = LoadSynced();
                var entry = synced.FirstOrDefault(candidate => candidate.package == result.package && candidate.packagePath == result.packagePath && Normalise(candidate.cacheRoot) == Normalise(result.cacheRoot));
                if (entry == null)
                {
                    entry = new SyncedFile { package = result.package, packagePath = result.packagePath, cacheRoot = result.cacheRoot, existedInPin = inCache };
                    if (inCache) CopyWithMeta(cacheFile, PristineFor(entry));
                    synced.Add(entry);
                }
                entry.checkoutFile = result.file;
                entry.checkoutBranch = result.checkoutBranch;

                // Later requests in this epoch need what was compiled, which the copy below overwrites.
                var snapshot = BaselineFor(session, cacheFile);
                if (inCache && !File.Exists(snapshot) && File.GetLastWriteTimeUtc(cacheFile) < session.EpochStart)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(snapshot));
                    File.Copy(cacheFile, snapshot);
                }

                if (inCheckout)
                {
                    // Written rather than copied, so the cache file is dated now and Unity's refresh sees the change.
                    Directory.CreateDirectory(Path.GetDirectoryName(cacheFile));
                    WriteWritable(cacheFile, File.ReadAllBytes(result.file));
                    if (!File.Exists(cacheFile + ".meta")) WriteWritable(cacheFile + ".meta", File.ReadAllBytes(result.file + ".meta"));
                }
                else
                {
                    DeleteWithMeta(cacheFile);
                }

                if (!Diverges(entry))
                {
                    synced.Remove(entry);
                    DeleteWithMeta(PristineFor(entry));
                }
                SaveSynced(synced);
                return null;
            }
            catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException)
            {
                return exception.Message;
            }
        }

        /// <summary>Package files this editor's cache holds in a version other than the one pinned, as <c>package/path</c>.</summary>
        internal static string[] PackageCacheDivergence()
        {
            return CurrentSynced().Where(Diverges).Select(entry => $"{entry.package}/{entry.packagePath.Replace('\\', '/')}" + (entry.existedInPin ? File.Exists(CacheFileOf(entry)) ? "" : " (deleted)" : " (new)")).ToArray();
        }

        /// <summary>Puts the pinned version of every synced package file back into the cache and recompiles.</summary>
        [MenuItem("Tools/Agent Hot Reload/Restore Pinned Packages")]
        internal static void RestorePinnedPackages()
        {
            var restored = new List<string>();
            foreach (var entry in CurrentSynced().Where(Diverges))
            {
                var cacheFile = CacheFileOf(entry);
                DeleteWithMeta(cacheFile);
                if (entry.existedInPin) CopyWithMeta(PristineFor(entry), cacheFile);
                restored.Add($"{entry.package}/{entry.packagePath.Replace('\\', '/')}");
            }
            SaveSynced(new List<SyncedFile>());
            if (Directory.Exists(PristinePath)) Directory.Delete(PristinePath, true);
            // The redirects stay until the compile below; nothing is to be offered again after it.
            session.patched.RemoveAll(entry => !string.IsNullOrEmpty(entry.package));
            session.stale.RemoveAll(entry => !string.IsNullOrEmpty(entry.package));
            SaveSession(session);
            Debug.Log(restored.Count == 0 ? "[Agent hot reload] the package cache already holds the pinned versions." : "[Agent hot reload] restored the pinned version of " + string.Join(", ", restored) + "; recompiling.");
            if (restored.Count > 0) AssetDatabase.Refresh();
        }

        /// <summary>Synced files of the cache folders this editor uses now. A rollout moves the pin to a new folder, ending the old ones.</summary>
        private static List<SyncedFile> CurrentSynced()
        {
            var roots = PackageInfo.GetAllRegisteredPackages().Select(package => Normalise(Path.GetFullPath(package.resolvedPath)));
            var currentRoots = new HashSet<string>(roots);
            var synced = LoadSynced();
            var ended = synced.Where(entry => !currentRoots.Contains(Normalise(entry.cacheRoot))).ToList();
            if (ended.Count == 0) return synced;
            foreach (var root in ended.Select(entry => entry.cacheRoot).Distinct())
            {
                var pristine = Path.Combine(PristinePath, Path.GetFileName(root));
                if (Directory.Exists(pristine)) Directory.Delete(pristine, true);
            }
            synced.RemoveAll(ended.Contains);
            SaveSynced(synced);
            return synced;
        }

        private static bool Diverges(SyncedFile entry)
        {
            var cacheFile = CacheFileOf(entry);
            if (!entry.existedInPin) return File.Exists(cacheFile);
            return !File.Exists(cacheFile) || !File.ReadAllBytes(cacheFile).SequenceEqual(File.ReadAllBytes(PristineFor(entry)));
        }

        /// <summary>Whether this editor's package cache holds the checkout's version of a stale package file, line endings aside.</summary>
        private static bool CacheMatchesCheckout(StaleFile entry)
        {
            var info = PackageInfo.GetAllRegisteredPackages().FirstOrDefault(candidate => candidate.name == entry.package);
            if (info == null) return true;
            var cacheFile = Path.Combine(info.resolvedPath, entry.packagePath);
            if (!File.Exists(entry.file) || !File.Exists(cacheFile)) return File.Exists(entry.file) == File.Exists(cacheFile);
            return File.ReadAllText(entry.file).Replace("\r\n", "\n") == File.ReadAllText(cacheFile).Replace("\r\n", "\n");
        }

        private static string CacheFileOf(SyncedFile entry) => Path.Combine(entry.cacheRoot, entry.packagePath);

        private static string PristineFor(SyncedFile entry) => Path.Combine(PristinePath, Path.GetFileName(entry.cacheRoot), entry.packagePath);

        private static void CopyWithMeta(string source, string destination)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destination));
            WriteWritable(destination, File.ReadAllBytes(source));
            if (File.Exists(source + ".meta")) WriteWritable(destination + ".meta", File.ReadAllBytes(source + ".meta"));
        }

        private static void DeleteWithMeta(string file)
        {
            foreach (var path in new[] { file, file + ".meta" })
            {
                if (!File.Exists(path)) continue;
                File.SetAttributes(path, FileAttributes.Normal);
                File.Delete(path);
            }
        }

        /// <summary>Package Manager may leave cache files read-only.</summary>
        private static void WriteWritable(string path, byte[] contents)
        {
            if (File.Exists(path)) File.SetAttributes(path, FileAttributes.Normal);
            File.WriteAllBytes(path, contents);
        }

        private static List<SyncedFile> LoadSynced()
        {
            return File.Exists(SyncedPath) ? JsonUtility.FromJson<SyncedList>(File.ReadAllText(SyncedPath)).files : new List<SyncedFile>();
        }

        /// <summary>Only the bridge reads this file, and a replace that silently failed would lose track of a synced file.</summary>
        private static void SaveSynced(List<SyncedFile> synced) => File.WriteAllText(SyncedPath, JsonUtility.ToJson(new SyncedList { files = synced }, true));

        /// <summary>Answers to re-queued patches have no reader, and a hook that gave up leaves its answer behind.</summary>
        private static void DeleteUnreadResults()
        {
            foreach (var file in new DirectoryInfo(ResultsPath).GetFiles("*.json"))
            {
                if ((DateTime.UtcNow - file.LastWriteTimeUtc).TotalMinutes > UnreadResultMinutes) file.Delete();
            }
        }

        private static void WriteState()
        {
            var state = new State
            {
                heartbeatUtc = DateTime.UtcNow.ToString("o"),
                projectPath = ProjectPath,
                epochUtc = session.epochUtc,
                epochId = session.EpochId,
                baselineDirectory = Path.Combine(BaselinesPath, session.EpochId),
                requestsDirectory = RequestsPath,
                inFlightDirectory = InFlightPath,
                resultsDirectory = ResultsPath,
                codeOptimization = CompilationPipeline.codeOptimization.ToString(),
                compiling = EditorApplication.isCompiling,
                unavailable = UnavailableReason(),
                cachedPackages = CachedPackages(),
                stale = session.stale.ToArray(),
            };
            var json = JsonUtility.ToJson(state, true);
            WriteAtomically(StatePath, json);
            WriteAtomically(RegistryPath, json);
        }

        /// <summary>The hook may be reading the file at this moment; a failed replace is retried by the next write.</summary>
        private static void WriteAtomically(string path, string contents)
        {
            var temporary = path + ".tmp";
            File.WriteAllText(temporary, contents);
            try
            {
                if (File.Exists(path)) File.Replace(temporary, path, null);
                else File.Move(temporary, path);
            }
            catch (IOException) { }
        }

        private static string Shorten(string text) => text.Length > 800 ? text.Substring(0, 800) + "..." : text;

        private static Session LoadSession()
        {
            var json = SessionState.GetString(SessionKey, null);
            return string.IsNullOrEmpty(json) ? new Session() : JsonUtility.FromJson<Session>(json);
        }

        private static void SaveSession(Session value) => SessionState.SetString(SessionKey, JsonUtility.ToJson(value));

        private class Pending
        {
            public Result Result;
            public AgentReloadGate.Verdict Verdict;
            public Task<CompileResult> Compile;

            /// <summary>Taken before the compile reads the file, so a write during the compile counts as newer than the patch.</summary>
            public DateTime SourceReadUtc;
        }

        /// <summary>Survives domain reloads through <see cref="SessionState"/>. JsonUtility turns a null string into "", so test with IsNullOrEmpty.</summary>
        [Serializable]
        private class Session
        {
            public string epochUtc;
            public string pendingCompileStartUtc;
            public int patchCount;
            public string importedUtc;
            public List<PatchedFile> patched = new List<PatchedFile>();
            public List<StaleFile> stale = new List<StaleFile>();

            public DateTime EpochStart => ParseUtc(epochUtc);
            public string EpochId => EpochStart.Ticks.ToString();
            public DateTime ImportedUpTo => string.IsNullOrEmpty(importedUtc) ? EpochStart : ParseUtc(importedUtc);
            public bool IsPatched(string file) => patched.Any(entry => entry.file == file);
            public bool IsTracked(string file) => IsPatched(file) || stale.Any(entry => entry.file == file);
        }

        /// <summary>An edited file the editor runs through a hot reload; <see cref="package"/> is set for a package checkout file.</summary>
        [Serializable]
        private class PatchedFile
        {
            public string file;
            public string package;
            public string packagePath;
            public string appliedUtc;
        }

        [Serializable]
        private class StaleFile
        {
            public string file;
            public string package;
            public string packagePath;
            public string reason;
        }

        [Serializable]
        private class Request
        {
            public string id;
            public string file;
            public string baseline;
            public string package;
            public string packagePath;
            public string checkoutBranch;
        }

        [Serializable]
        private class Result
        {
            public string id;
            public string project;
            public string file;
            public string package;
            public string packagePath;
            public string verdict;
            public string[] reasons;
            public string[] caveats;
            public StaleFile[] stale;
            public string epochId;
            public bool cacheSynced;
            public string[] diverged;

            /// <summary>The cache folder of a package edit's pinned version, which every answer syncs the checkout file into.</summary>
            [NonSerialized] public string cacheRoot;
            [NonSerialized] public string checkoutBranch;
        }

        /// <summary>A package file whose checkout version was copied over this editor's package cache.</summary>
        [Serializable]
        private class SyncedFile
        {
            public string package;
            public string packagePath;
            public string cacheRoot;
            public string checkoutFile;
            public string checkoutBranch;
            public bool existedInPin;
        }

        [Serializable]
        private class SyncedList
        {
            public List<SyncedFile> files = new List<SyncedFile>();
        }

        [Serializable]
        private class State
        {
            public string heartbeatUtc;
            public string projectPath;
            public string epochUtc;
            public string epochId;
            public string baselineDirectory;
            public string requestsDirectory;
            public string inFlightDirectory;
            public string resultsDirectory;
            public string codeOptimization;
            public bool compiling;
            public string unavailable;
            public string[] cachedPackages;
            public StaleFile[] stale;
        }
    }
}
