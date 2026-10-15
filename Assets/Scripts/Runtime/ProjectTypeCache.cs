#if UNITY_EDITOR || LiveScriptReload_Enabled
using System;
using System.Collections.Concurrent;
using System.Reflection;

namespace FastScriptReload.Runtime
{
    /// <summary>Finds the compiled project type a hot-reloaded type replaces, by full name.</summary>
    /// <remarks>
    /// Each name is looked up in the assemblies themselves, which index their types by name. A dictionary of every loaded type
    /// used to be built on first use instead: Mono does not cache <see cref="Type.FullName"/>, and with ~76k types loaded (as in a
    /// VR project with its SDKs) that build took ~30 seconds, blocking the main thread in the first hot reload of every domain.
    /// </remarks>
    public class ProjectTypeCache
    {
        private static readonly ConcurrentDictionary<string, Type> found = new ConcurrentDictionary<string, Type>();

        /// <summary>
        /// The first type named <paramref name="fullName"/> in assembly load order, skipping the assemblies Fast Script Reload
        /// generated itself. Only found types are cached: an assembly loaded later may still define a missing one.
        /// </summary>
        public static bool TryGetType(string fullName, out Type type)
        {
            if (found.TryGetValue(fullName, out type)) return true;
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (assembly.IsDefined(typeof(DynamicallyCreatedAssemblyAttribute), false)) continue;
                type = assembly.GetType(fullName, false);
                if (type == null) continue;
                found.TryAdd(fullName, type);
                return true;
            }
            return false;
        }
    }
}
#endif
