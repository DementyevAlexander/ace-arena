using System;
using System.Reflection;
using NUnit.Framework;
using SGG.UnityMCP.Editor.Commands;

namespace SGG.UnityMCP.Editor.Tests
{
    public sealed class ManifestHandlerResolverTests
    {
        [Test]
        public void SharedHandlerTypeIsLookedUpOncePerAssemblyIncludingMissingMethods()
        {
            var assemblies = new[] { typeof(object).Assembly, typeof(ManifestHandlerResolverTests).Assembly };
            var lookups = 0;
            var resolver = new ManifestHandlerResolver(assemblies, (assembly, name) =>
            {
                lookups++;
                return assembly.GetType(name);
            });
            var prefix = typeof(Handlers).FullName + ".";
            for (var iteration = 0; iteration < 47; iteration++)
            {
                Assert.That(resolver.Resolve(prefix + "First"), Is.EqualTo(typeof(Handlers).GetMethod("First")));
                Assert.That(resolver.Resolve(prefix + "Second"), Is.EqualTo(typeof(Handlers).GetMethod("Second", BindingFlags.NonPublic | BindingFlags.Static)));
                Assert.That(resolver.Resolve(prefix + "Missing"), Is.Null);
                Assert.That(resolver.Resolve(prefix + "Instance"), Is.Null);
            }
            Assert.That(lookups, Is.EqualTo(assemblies.Length));
        }

        [Test]
        public void DuplicateTypeNamesKeepAssemblyPrecedenceAndMissingMethodFallback()
        {
            var assemblies = new[] { typeof(object).Assembly, typeof(ManifestHandlerResolverTests).Assembly };
            var resolver = new ManifestHandlerResolver(assemblies, (assembly, name) =>
                assembly == assemblies[0] ? typeof(EarlierHandlers) : typeof(LaterHandlers));
            Assert.That(resolver.Resolve("Same.Type.Shared"), Is.EqualTo(typeof(EarlierHandlers).GetMethod("Shared")));
            Assert.That(resolver.Resolve("Same.Type.LaterOnly"), Is.EqualTo(typeof(LaterHandlers).GetMethod("LaterOnly")));
        }

        [Test]
        public void EarlierHandlerShortCircuitsLaterAssemblyTypeLoadFailure()
        {
            var assemblies = new[] { typeof(ManifestHandlerResolverTests).Assembly, typeof(object).Assembly };
            var earlierLookups = 0;
            var laterLookups = 0;
            var resolver = new ManifestHandlerResolver(assemblies, (assembly, name) =>
            {
                if (assembly == assemblies[0])
                {
                    earlierLookups++;
                    return typeof(Handlers);
                }
                laterLookups++;
                throw new TypeLoadException("Unrelated later assembly cannot load this type.");
            });

            Assert.That(resolver.Resolve("Same.Type.First"), Is.EqualTo(typeof(Handlers).GetMethod("First")));
            Assert.That(resolver.Resolve("Same.Type.Second"), Is.EqualTo(typeof(Handlers).GetMethod("Second", BindingFlags.NonPublic | BindingFlags.Static)));
            Assert.That(resolver.Resolve("Same.Type.First"), Is.EqualTo(typeof(Handlers).GetMethod("First")));
            Assert.That(earlierLookups, Is.EqualTo(1));
            Assert.That(laterLookups, Is.Zero);

            // If the earlier type lacks the method, preserve the old lookup failure
            // instead of silently treating a broken later assembly as a missing type.
            Assert.Throws<TypeLoadException>(() => resolver.Resolve("Same.Type.Missing"));
            Assert.That(earlierLookups, Is.EqualTo(1));
            Assert.That(laterLookups, Is.EqualTo(1));
        }

        [Test]
        public void MissingTypeIsCachedWithinBuildButNextBuildReceivesFreshSnapshot()
        {
            var assembly = typeof(ManifestHandlerResolverTests).Assembly;
            var available = false;
            var lookups = 0;
            Func<Assembly, string, Type> lookup = (unused, name) => { lookups++; return available ? typeof(Handlers) : null; };
            var first = new ManifestHandlerResolver(new[] { assembly }, lookup);
            Assert.That(first.Resolve("External.Handlers.First"), Is.Null);
            available = true;
            Assert.That(first.Resolve("External.Handlers.Second"), Is.Null);
            Assert.That(lookups, Is.EqualTo(1));
            var next = new ManifestHandlerResolver(new[] { assembly }, lookup);
            Assert.That(next.Resolve("External.Handlers.First"), Is.EqualTo(typeof(Handlers).GetMethod("First")));
            Assert.That(lookups, Is.EqualTo(2));
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase("NoSeparator")]
        [TestCase(".First")]
        [TestCase("Handlers.")]
        public void InvalidHandlerDoesNotScanAssemblies(string handler)
        {
            var resolver = new ManifestHandlerResolver(new[] { typeof(object).Assembly }, (assembly, name) =>
                throw new InvalidOperationException("Invalid handler must not discover types."));
            Assert.That(resolver.Resolve(handler), Is.Null);
        }

        public sealed class Handlers
        {
            public static void First() { }
            private static void Second() { }
            public void Instance() { }
        }

        public static class EarlierHandlers { public static void Shared() { } }
        public static class LaterHandlers
        {
            public static void Shared() { }
            public static void LaterOnly() { }
        }
    }
}
