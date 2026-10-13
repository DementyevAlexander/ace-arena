using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEditor;

namespace SGG.UnityMCP.Editor.Commands
{
    internal static class EditorAssemblyDiscovery
    {
        const int MaxAssemblies = 2048;
        const int MaxTypeCacheTypes = 200000;

        internal static Assembly[] LoadedAssemblies()
        {
#if UNITY_6000_6_OR_NEWER
            return SupportedAssemblySnapshot();
#else
            var assemblies = AppDomain.CurrentDomain.GetAssemblies();
            Array.Sort(assemblies, CompareAssemblies);
            return assemblies;
#endif
        }

#if UNITY_6000_6_OR_NEWER
        static Assembly[] SupportedAssemblySnapshot()
        {
            var assemblies = new Dictionary<string, Assembly>(StringComparer.Ordinal);
            var identities = new HashSet<Assembly>();
            var names = new HashSet<string>(StringComparer.Ordinal);
            AddAssembly(assemblies, identities, names, typeof(EditorAssemblyDiscovery).Assembly);
            AddAssembly(assemblies, identities, names, typeof(object).Assembly);
            AddAssembly(assemblies, identities, names, typeof(Uri).Assembly);
            AddAssembly(assemblies, identities, names, typeof(List<>).Assembly);
            AddAssembly(assemblies, identities, names, typeof(System.Linq.Enumerable).Assembly);
            AddAssembly(assemblies, identities, names, typeof(System.Xml.XmlDocument).Assembly);
            AddAssembly(assemblies, identities, names, typeof(UnityEngine.Object).Assembly);
            AddAssembly(assemblies, identities, names, typeof(UnityEditor.Editor).Assembly);
            AddTypeCacheAssemblies(assemblies, identities, names, false);
            AddTypeCacheAssemblies(assemblies, identities, names, true);

            try
            {
                var compiled = UnityEditor.Compilation.CompilationPipeline.GetAssemblies();
                for (var index = 0; index < compiled.Length && assemblies.Count < MaxAssemblies; index++)
                {
                    var name = compiled[index] == null ? string.Empty : compiled[index].name;
                    if (string.IsNullOrEmpty(name) || names.Contains(name))
                    {
                        continue;
                    }

                    try { AddAssembly(assemblies, identities, names, Assembly.Load(new AssemblyName(name))); }
                    catch { }
                }
            }
            catch
            {
                // TypeCache remains authoritative when the compilation snapshot is unavailable.
            }

            var result = new Assembly[assemblies.Count];
            assemblies.Values.CopyTo(result, 0);
            Array.Sort(result, CompareAssemblies);
            return result;
        }

        static void AddTypeCacheAssemblies(Dictionary<string, Assembly> assemblies, HashSet<Assembly> identities, HashSet<string> names, bool valueTypes)
        {
            try
            {
                var scanned = 0;
                var types = valueTypes ? TypeCache.GetTypesDerivedFrom<ValueType>() : TypeCache.GetTypesDerivedFrom<object>();
                foreach (var type in types)
                {
                    if (scanned++ >= MaxTypeCacheTypes || assemblies.Count >= MaxAssemblies)
                    {
                        break;
                    }

                    if (type != null)
                    {
                        AddAssembly(assemblies, identities, names, type.Assembly);
                    }
                }
            }
            catch
            {
                // A partial TypeCache snapshot still yields deterministic best-effort discovery.
            }
        }

        static void AddAssembly(Dictionary<string, Assembly> assemblies, HashSet<Assembly> identities, HashSet<string> names, Assembly assembly)
        {
            if (assembly == null || assemblies.Count >= MaxAssemblies || identities.Contains(assembly))
            {
                return;
            }

            try
            {
                var key = assembly.FullName ?? assembly.GetName().Name ?? string.Empty;
                if (!string.IsNullOrEmpty(key) && !assemblies.ContainsKey(key))
                {
                    assemblies.Add(key, assembly);
                    identities.Add(assembly);
                    // Read AssemblyName once per retained assembly, rather than once for
                    // every compiled-name candidate or every type in the TypeCache.
                    var name = assembly.GetName().Name;
                    if (!string.IsNullOrEmpty(name)) names.Add(name);
                }
            }
            catch { }
        }
#endif

        static int CompareAssemblies(Assembly left, Assembly right)
        {
            return string.Compare(SafeAssemblyName(left), SafeAssemblyName(right), StringComparison.Ordinal);
        }

        static string SafeAssemblyName(Assembly assembly)
        {
            try { return assembly == null ? string.Empty : assembly.FullName ?? assembly.GetName().Name ?? string.Empty; }
            catch { return string.Empty; }
        }
    }
}
