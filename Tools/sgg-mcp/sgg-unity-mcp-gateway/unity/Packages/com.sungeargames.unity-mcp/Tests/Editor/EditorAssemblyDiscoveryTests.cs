using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using SGG.UnityMCP.Editor.Commands;
using UnityEditor;

namespace SGG.UnityMCP.Editor.Tests
{
    public sealed class EditorAssemblyDiscoveryTests
    {
        [Test]
        public void LoadedAssembliesAreBoundedUniqueDeterministicAndCompleteForKnownEditorTypes()
        {
            var first = EditorAssemblyDiscovery.LoadedAssemblies();
            var second = EditorAssemblyDiscovery.LoadedAssemblies();

            Assert.That(first, Is.Not.Empty);
            Assert.That(first.Length, Is.LessThanOrEqualTo(2048));
            Assert.That(first, Does.Contain(typeof(object).Assembly));
            Assert.That(first, Does.Contain(typeof(EditorAssemblyDiscovery).Assembly));
            Assert.That(first, Does.Contain(typeof(EditorAssemblyDiscoveryTests).Assembly));
            Assert.That(second, Is.EqualTo(first));

            var unique = new HashSet<Assembly>();
            for (var index = 0; index < first.Length; index++)
            {
                Assert.That(first[index], Is.Not.Null);
                Assert.That(unique.Add(first[index]), Is.True, "Duplicate assembly at index " + index);
                if (index > 0)
                {
                    Assert.That(CompareAssemblies(first[index - 1], first[index]), Is.LessThanOrEqualTo(0));
                }
            }
        }

        static int CompareAssemblies(Assembly left, Assembly right)
        {
            var byName = string.Compare(left.GetName().Name ?? string.Empty, right.GetName().Name ?? string.Empty, StringComparison.Ordinal);
            return byName != 0
                ? byName
                : string.Compare(left.FullName ?? string.Empty, right.FullName ?? string.Empty, StringComparison.Ordinal);
        }

#if UNITY_6000_6_OR_NEWER
        [Test]
        public void NameAndIdentityCachesPreserveLegacySupportedSnapshotAndOrder()
        {
            Assert.That(EditorAssemblyDiscovery.LoadedAssemblies(), Is.EqualTo(LegacySupportedSnapshot()));
        }

        // The pre-optimization algorithm is intentionally retained in this parity fixture.
        // It is slow only in tests; production must never repeat GetName for every candidate.
        static Assembly[] LegacySupportedSnapshot()
        {
            var assemblies = new Dictionary<string, Assembly>(StringComparer.Ordinal);
            LegacyAdd(assemblies, typeof(EditorAssemblyDiscovery).Assembly);
            LegacyAdd(assemblies, typeof(object).Assembly);
            LegacyAdd(assemblies, typeof(Uri).Assembly);
            LegacyAdd(assemblies, typeof(List<>).Assembly);
            LegacyAdd(assemblies, typeof(System.Linq.Enumerable).Assembly);
            LegacyAdd(assemblies, typeof(System.Xml.XmlDocument).Assembly);
            LegacyAdd(assemblies, typeof(UnityEngine.Object).Assembly);
            LegacyAdd(assemblies, typeof(UnityEditor.Editor).Assembly);
            LegacyAddTypes(assemblies, false);
            LegacyAddTypes(assemblies, true);
            try
            {
                var compiled = UnityEditor.Compilation.CompilationPipeline.GetAssemblies();
                for (var index = 0; index < compiled.Length && assemblies.Count < 2048; index++)
                {
                    var name = compiled[index] == null ? string.Empty : compiled[index].name;
                    if (string.IsNullOrEmpty(name) || LegacyContainsName(assemblies, name)) continue;
                    try { LegacyAdd(assemblies, Assembly.Load(new AssemblyName(name))); }
                    catch { }
                }
            }
            catch { }
            var result = new Assembly[assemblies.Count];
            assemblies.Values.CopyTo(result, 0);
            Array.Sort(result, (left, right) => string.Compare(left.FullName ?? left.GetName().Name ?? string.Empty,
                right.FullName ?? right.GetName().Name ?? string.Empty, StringComparison.Ordinal));
            return result;
        }

        static void LegacyAddTypes(Dictionary<string, Assembly> assemblies, bool valueTypes)
        {
            try
            {
                var scanned = 0;
                var types = valueTypes ? TypeCache.GetTypesDerivedFrom<ValueType>() : TypeCache.GetTypesDerivedFrom<object>();
                foreach (var type in types)
                {
                    if (scanned++ >= 200000 || assemblies.Count >= 2048) break;
                    if (type != null) LegacyAdd(assemblies, type.Assembly);
                }
            }
            catch { }
        }

        static bool LegacyContainsName(Dictionary<string, Assembly> assemblies, string name)
        {
            foreach (var assembly in assemblies.Values)
            {
                try { if (string.Equals(assembly.GetName().Name, name, StringComparison.Ordinal)) return true; }
                catch { }
            }
            return false;
        }

        static void LegacyAdd(Dictionary<string, Assembly> assemblies, Assembly assembly)
        {
            if (assembly == null || assemblies.Count >= 2048) return;
            try
            {
                var key = assembly.FullName ?? assembly.GetName().Name ?? string.Empty;
                if (!string.IsNullOrEmpty(key) && !assemblies.ContainsKey(key)) assemblies.Add(key, assembly);
            }
            catch { }
        }
#endif
    }
}
