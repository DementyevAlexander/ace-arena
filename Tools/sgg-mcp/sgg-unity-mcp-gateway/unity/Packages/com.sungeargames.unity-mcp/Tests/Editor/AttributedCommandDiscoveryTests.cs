using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using SGG.UnityMCP.Editor.Commands;

namespace SGG.UnityMCP.Editor.Tests
{
    public sealed class AttributedCommandDiscoveryTests
    {
        [Test]
        public void IndexedCommandsMatchLegacyCompleteReflectionOrder()
        {
            var assemblies = EditorAssemblyDiscovery.LoadedAssemblies();
            Assert.That(Discover<McpCommandAttribute>(assemblies, true),
                Is.EqualTo(Discover<McpCommandAttribute>(assemblies, false)));
        }

        [Test]
        public void IndexedFixturesPreservePrivateStructBaseAndDuplicateMethodOrder()
        {
            var assemblies = new[] { typeof(AttributedCommandDiscoveryTests).Assembly };
            var indexed = Discover<FixtureAttribute>(assemblies, true);
            Assert.That(indexed, Is.EqualTo(Discover<FixtureAttribute>(assemblies, false)));
            Assert.That(indexed, Does.Contain(typeof(PublicHandlers).GetMethod("Shared")));
            Assert.That(indexed, Does.Contain(typeof(PrivateHandlers).GetMethod("Shared", BindingFlags.NonPublic | BindingFlags.Static)));
            Assert.That(indexed, Does.Contain(typeof(StructHandlers).GetMethod("StructCommand")));
            Assert.That(indexed, Does.Contain(typeof(BaseHandlers).GetMethod("BaseCommand")));
            Assert.That(indexed, Has.No.Member(typeof(PublicHandlers).GetMethod("InstanceCommand")));
            Assert.That(indexed, Has.No.Member(typeof(DerivedHandlers).GetMethod("BaseCommand")));
        }

        static List<MethodInfo> Discover<T>(Assembly[] assemblies, bool indexed) where T : Attribute
        {
            var candidates = indexed ? AttributedCommandDiscovery.CandidateTypes<T>() : null;
            var result = new List<MethodInfo>();
            foreach (var assembly in assemblies)
            {
                if (assembly.IsDynamic) continue;
                Type[] types;
                try { types = assembly.GetTypes(); }
                catch (ReflectionTypeLoadException exception) { types = exception.Types; }
                foreach (var type in types)
                {
                    if (type == null || (candidates != null && !candidates.Contains(type))) continue;
                    foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
                        if (method.GetCustomAttribute<T>() != null) result.Add(method);
                }
            }
            return result;
        }

        [AttributeUsage(AttributeTargets.Method)]
        public sealed class FixtureAttribute : Attribute { }
        public sealed class PublicHandlers
        {
            [Fixture] public static void Shared() { }
            [Fixture] public void InstanceCommand() { }
        }
        public static class PrivateHandlers { [Fixture] private static void Shared() { } }
        public struct StructHandlers { [Fixture] public static void StructCommand() { } }
        public class BaseHandlers { [Fixture] public static void BaseCommand() { } }
        public sealed class DerivedHandlers : BaseHandlers { }
    }
}
