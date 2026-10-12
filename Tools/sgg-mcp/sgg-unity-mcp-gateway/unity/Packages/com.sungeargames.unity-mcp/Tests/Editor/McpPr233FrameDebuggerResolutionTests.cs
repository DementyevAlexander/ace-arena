using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using SGG.UnityMCP.Editor.Commands;

namespace SGG.UnityMCP.Editor.Tests
{
    public sealed class McpPr233FrameDebuggerResolutionTests
    {
        static readonly string[] UtilityTypeNames =
        {
            "UnityEditorInternal.FrameDebuggerUtility",
            "UnityEditor.FrameDebuggerUtility",
            "UnityEditorInternal.FrameDebuggerInternal.FrameDebuggerUtility"
        };

        static readonly string[] EventDataTypeNames =
        {
            "UnityEditorInternal.FrameDebuggerEventData",
            "UnityEditor.FrameDebuggerEventData",
            "UnityEditorInternal.FrameDebuggerInternal.FrameDebuggerEventData"
        };

        [TestCase(0)]
        [TestCase(1)]
        [TestCase(2)]
        [TestCase(-1)]
        public void UtilityLookupPreservesExactNameOrderAndShortCircuits(int firstAvailableIndex)
        {
            var types = new[] { typeof(ByValueUtility), typeof(ByRefUtility), typeof(NonPublicUtility) };
            var attempts = new List<string>();
            var map = AvailableTypes(UtilityTypeNames, types, firstAvailableIndex);

            var resolved = FrameDebuggerReadonlyCommands.ResolveFrameDebuggerUtilityType(MapResolver(map, attempts));

            Assert.That(resolved, Is.EqualTo(firstAvailableIndex < 0 ? null : types[firstAvailableIndex]));
            Assert.That(attempts, Is.EqualTo(ExpectedAttempts(UtilityTypeNames, firstAvailableIndex)));
        }

        [TestCase(0)]
        [TestCase(1)]
        [TestCase(2)]
        [TestCase(-1)]
        public void EventDataLookupPreservesExactNameOrderAndShortCircuits(int firstAvailableIndex)
        {
            var types = new[] { typeof(FixtureEventData), typeof(OtherEventData), typeof(NewLocationEventData) };
            var attempts = new List<string>();
            var map = AvailableTypes(EventDataTypeNames, types, firstAvailableIndex);

            var resolved = FrameDebuggerReadonlyCommands.ResolveFrameDebuggerEventDataType(MapResolver(map, attempts));

            Assert.That(resolved, Is.EqualTo(firstAvailableIndex < 0 ? null : types[firstAvailableIndex]));
            Assert.That(attempts, Is.EqualTo(ExpectedAttempts(EventDataTypeNames, firstAvailableIndex)));
        }

        [Test]
        public void UtilityAndEventDataLookupsRemainIndependent()
        {
            var attempts = new List<string>();
            var map = new Dictionary<string, Type>(StringComparer.Ordinal)
            {
                { UtilityTypeNames[0], typeof(ByValueUtility) },
                { EventDataTypeNames[2], typeof(FixtureEventData) }
            };
            var resolver = MapResolver(map, attempts);

            var utilityType = FrameDebuggerReadonlyCommands.ResolveFrameDebuggerUtilityType(resolver);
            var eventDataType = FrameDebuggerReadonlyCommands.ResolveFrameDebuggerEventDataType(resolver);

            Assert.That(utilityType, Is.EqualTo(typeof(ByValueUtility)));
            Assert.That(eventDataType, Is.EqualTo(typeof(FixtureEventData)));
            Assert.That(attempts, Is.EqualTo(new[]
            {
                UtilityTypeNames[0], EventDataTypeNames[0], EventDataTypeNames[1], EventDataTypeNames[2]
            }));
        }

        [TestCase(0)]
        [TestCase(1)]
        public void PresentLegacyUtilityDoesNotFallThroughWhenMethodIsMissing(int legacyIndex)
        {
            var attempts = new List<string>();
            var map = new Dictionary<string, Type>(StringComparer.Ordinal);
            map.Add(UtilityTypeNames[legacyIndex], typeof(UtilityWithoutMethod));
            for (var index = legacyIndex + 1; index < UtilityTypeNames.Length; index++)
            {
                map.Add(UtilityTypeNames[index], typeof(ByValueUtility));
            }

            var utilityType = FrameDebuggerReadonlyCommands.ResolveFrameDebuggerUtilityType(MapResolver(map, attempts));
            var byRef = true;
            var method = FrameDebuggerReadonlyCommands.ResolveGetFrameEventDataMethod(utilityType, typeof(FixtureEventData), out byRef);

            Assert.That(utilityType, Is.EqualTo(typeof(UtilityWithoutMethod)));
            Assert.That(method, Is.Null);
            Assert.That(byRef, Is.False);
            Assert.That(attempts, Is.EqualTo(ExpectedAttempts(UtilityTypeNames, legacyIndex)));
        }

        [TestCase(false, 0)]
        [TestCase(false, 1)]
        [TestCase(true, 0)]
        [TestCase(true, 1)]
        public void LookupFailureFallsThroughOnlyToTheNextExactName(bool eventData, int throwingIndex)
        {
            var names = eventData ? EventDataTypeNames : UtilityTypeNames;
            var expectedType = eventData ? typeof(FixtureEventData) : typeof(ByValueUtility);
            var attempts = new List<string>();
            Func<string, Type> resolver = fullName =>
            {
                attempts.Add(fullName);
                if (string.Equals(fullName, names[throwingIndex], StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("Synthetic type lookup failure");
                }
                return string.Equals(fullName, names[throwingIndex + 1], StringComparison.Ordinal) ? expectedType : null;
            };
            Type resolved = null;

            Assert.DoesNotThrow(() => resolved = ResolveKnownType(eventData, resolver));

            Assert.That(resolved, Is.EqualTo(expectedType));
            Assert.That(attempts, Is.EqualTo(ExpectedAttempts(names, throwingIndex + 1)));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void AllLookupFailuresReturnNullWithoutEscaping(bool eventData)
        {
            var names = eventData ? EventDataTypeNames : UtilityTypeNames;
            var attempts = new List<string>();
            Func<string, Type> resolver = fullName =>
            {
                attempts.Add(fullName);
                throw new InvalidOperationException("Synthetic type lookup failure");
            };
            Type resolved = null;

            Assert.DoesNotThrow(() => resolved = ResolveKnownType(eventData, resolver));

            Assert.That(resolved, Is.Null);
            Assert.That(attempts, Is.EqualTo(names));
        }

        [TestCase(typeof(ByValueUtility), false, true, typeof(bool))]
        [TestCase(typeof(ByRefUtility), true, true, typeof(bool))]
        [TestCase(typeof(NonPublicUtility), false, false, typeof(bool))]
        [TestCase(typeof(UnconstrainedReturnUtility), false, true, typeof(string))]
        [TestCase(typeof(OutUtility), true, true, typeof(bool))]
        [TestCase(typeof(InUtility), true, true, typeof(bool))]
        public void MethodLookupAcceptsExistingStaticParameterShapes(Type utilityType, bool expectedByRef, bool expectedPublic, Type expectedReturnType)
        {
            var byRef = true;

            var method = FrameDebuggerReadonlyCommands.ResolveGetFrameEventDataMethod(utilityType, typeof(FixtureEventData), out byRef);

            Assert.That(method, Is.Not.Null);
            Assert.That(method.DeclaringType, Is.EqualTo(utilityType));
            Assert.That(method.IsPublic, Is.EqualTo(expectedPublic));
            Assert.That(method.ReturnType, Is.EqualTo(expectedReturnType));
            Assert.That(byRef, Is.EqualTo(expectedByRef));
            AssertMethodShape(method, typeof(FixtureEventData), byRef);
            if (utilityType == typeof(OutUtility)) Assert.That(method.GetParameters()[1].IsOut, Is.True);
            if (utilityType == typeof(InUtility)) Assert.That(method.GetParameters()[1].IsIn, Is.True);
        }

        [TestCase(typeof(WrongNameCaseUtility))]
        [TestCase(typeof(InstanceUtility))]
        [TestCase(typeof(OneParameterUtility))]
        [TestCase(typeof(ThreeParameterUtility))]
        [TestCase(typeof(LongFirstParameterUtility))]
        [TestCase(typeof(ByRefFirstParameterUtility))]
        [TestCase(typeof(WrongEventDataUtility))]
        [TestCase(typeof(WrongByRefEventDataUtility))]
        public void MethodLookupRejectsUnsupportedShapesAndClearsByRef(Type utilityType)
        {
            var byRef = true;

            var method = FrameDebuggerReadonlyCommands.ResolveGetFrameEventDataMethod(utilityType, typeof(FixtureEventData), out byRef);

            Assert.That(method, Is.Null);
            Assert.That(byRef, Is.False);
        }

        [TestCase(false, true)]
        [TestCase(true, false)]
        [TestCase(false, false)]
        public void NullMetadataInputsReturnNullAndClearByRef(bool utilityAvailable, bool eventDataAvailable)
        {
            var utilityType = utilityAvailable ? typeof(ByValueUtility) : null;
            var eventDataType = eventDataAvailable ? typeof(FixtureEventData) : null;
            var byRef = true;

            var method = FrameDebuggerReadonlyCommands.ResolveGetFrameEventDataMethod(utilityType, eventDataType, out byRef);

            Assert.That(method, Is.Null);
            Assert.That(byRef, Is.False);
        }

        [Test]
        public void MetadataResolutionDoesNotInitializeUtilityOrInvokeGetterOrMethod()
        {
            AssertMetadataProbeUntouched();
            var attempts = new List<string>();
            var map = new Dictionary<string, Type>(StringComparer.Ordinal)
            {
                { UtilityTypeNames[2], typeof(MetadataOnlyUtility) },
                { EventDataTypeNames[2], typeof(FixtureEventData) }
            };
            var resolver = MapResolver(map, attempts);

            var utilityType = FrameDebuggerReadonlyCommands.ResolveFrameDebuggerUtilityType(resolver);
            var eventDataType = FrameDebuggerReadonlyCommands.ResolveFrameDebuggerEventDataType(resolver);
            var method = FrameDebuggerReadonlyCommands.ResolveGetFrameEventDataMethod(utilityType, eventDataType, out var byRef);

            Assert.That(utilityType, Is.EqualTo(typeof(MetadataOnlyUtility)));
            Assert.That(eventDataType, Is.EqualTo(typeof(FixtureEventData)));
            Assert.That(method, Is.Not.Null);
            Assert.That(method.DeclaringType, Is.EqualTo(utilityType));
            Assert.That(byRef, Is.False);
            AssertMethodShape(method, eventDataType, byRef);
            Assert.That(attempts, Is.EqualTo(new[]
            {
                UtilityTypeNames[0], UtilityTypeNames[1], UtilityTypeNames[2],
                EventDataTypeNames[0], EventDataTypeNames[1], EventDataTypeNames[2]
            }));
            AssertMetadataProbeUntouched();
        }

        [Test]
        public void DefaultLookupRetainsExactKnownNamesAndCompatibleMethodMetadata()
        {
            var requireAvailability = string.Equals(UnityEngine.Application.unityVersion, "6000.6.2f1", StringComparison.Ordinal);

            var utilityType = FrameDebuggerReadonlyCommands.ResolveFrameDebuggerUtilityType();
            var eventDataType = FrameDebuggerReadonlyCommands.ResolveFrameDebuggerEventDataType();

            if (requireAvailability)
            {
                Assert.That(utilityType, Is.Not.Null, "The exact 6000.6.2f1 host must expose a known utility type.");
                Assert.That(eventDataType, Is.Not.Null, "The exact 6000.6.2f1 host must expose a known event-data type.");
            }
            if (utilityType != null) Assert.That(UtilityTypeNames, Does.Contain(utilityType.FullName));
            if (eventDataType != null) Assert.That(EventDataTypeNames, Does.Contain(eventDataType.FullName));

            var method = FrameDebuggerReadonlyCommands.ResolveGetFrameEventDataMethod(utilityType, eventDataType, out var byRef);

            if (requireAvailability)
            {
                Assert.That(method, Is.Not.Null, "The exact 6000.6.2f1 host must expose the existing accepted method shape.");
            }
            if (method != null)
            {
                Assert.That(method.DeclaringType, Is.Not.Null);
                var parameters = method.GetParameters();
                Assert.That(parameters.Length, Is.EqualTo(2));
                Assert.That(byRef, Is.EqualTo(parameters[1].ParameterType.IsByRef));
                AssertMethodShape(method, eventDataType, byRef);
            }
            else
            {
                Assert.That(byRef, Is.False);
            }
        }

        static Dictionary<string, Type> AvailableTypes(string[] names, Type[] types, int firstAvailableIndex)
        {
            var map = new Dictionary<string, Type>(StringComparer.Ordinal);
            if (firstAvailableIndex >= 0)
            {
                for (var index = firstAvailableIndex; index < names.Length; index++) map.Add(names[index], types[index]);
            }
            return map;
        }

        static Func<string, Type> MapResolver(Dictionary<string, Type> map, List<string> attempts)
        {
            return fullName =>
            {
                attempts.Add(fullName);
                return map.TryGetValue(fullName, out var type) ? type : null;
            };
        }

        static string[] ExpectedAttempts(string[] names, int firstAvailableIndex)
        {
            var count = firstAvailableIndex < 0 ? names.Length : firstAvailableIndex + 1;
            var result = new string[count];
            Array.Copy(names, result, count);
            return result;
        }

        static Type ResolveKnownType(bool eventData, Func<string, Type> resolver)
        {
            return eventData
                ? FrameDebuggerReadonlyCommands.ResolveFrameDebuggerEventDataType(resolver)
                : FrameDebuggerReadonlyCommands.ResolveFrameDebuggerUtilityType(resolver);
        }

        static void AssertMethodShape(MethodInfo method, Type eventDataType, bool byRef)
        {
            Assert.That(method.Name, Is.EqualTo("GetFrameEventData"));
            Assert.That(method.IsStatic, Is.True);
            var parameters = method.GetParameters();
            Assert.That(parameters.Length, Is.EqualTo(2));
            Assert.That(parameters[0].ParameterType, Is.EqualTo(typeof(int)));
            if (byRef)
            {
                Assert.That(parameters[1].ParameterType.IsByRef, Is.True);
                Assert.That(parameters[1].ParameterType.GetElementType(), Is.EqualTo(eventDataType));
            }
            else
            {
                Assert.That(parameters[1].ParameterType, Is.EqualTo(eventDataType));
            }
        }

        static void AssertMetadataProbeUntouched()
        {
            Assert.That(MetadataProbe.StaticConstructorCalls, Is.Zero);
            Assert.That(MetadataProbe.GetterCalls, Is.Zero);
            Assert.That(MetadataProbe.MethodCalls, Is.Zero);
        }

        sealed class FixtureEventData { }
        sealed class OtherEventData { }
        sealed class NewLocationEventData { }
        sealed class UtilityWithoutMethod { }

        sealed class ByValueUtility
        {
            public static bool GetFrameEventData(int index, FixtureEventData eventData)
            {
                throw new InvalidOperationException("Metadata fixture methods must not be invoked");
            }
        }

        sealed class ByRefUtility
        {
            public static bool GetFrameEventData(int index, ref FixtureEventData eventData)
            {
                throw new InvalidOperationException("Metadata fixture methods must not be invoked");
            }
        }

        sealed class NonPublicUtility
        {
            static bool GetFrameEventData(int index, FixtureEventData eventData)
            {
                throw new InvalidOperationException("Metadata fixture methods must not be invoked");
            }
        }

        sealed class UnconstrainedReturnUtility
        {
            public static string GetFrameEventData(int index, FixtureEventData eventData)
            {
                throw new InvalidOperationException("Metadata fixture methods must not be invoked");
            }
        }

        sealed class OutUtility
        {
            public static bool GetFrameEventData(int index, out FixtureEventData eventData)
            {
                throw new InvalidOperationException("Metadata fixture methods must not be invoked");
            }
        }

        sealed class InUtility
        {
            public static bool GetFrameEventData(int index, in FixtureEventData eventData)
            {
                throw new InvalidOperationException("Metadata fixture methods must not be invoked");
            }
        }

        sealed class WrongNameCaseUtility
        {
            public static bool GetframeEventData(int index, FixtureEventData eventData)
            {
                throw new InvalidOperationException("Metadata fixture methods must not be invoked");
            }
        }

        sealed class InstanceUtility
        {
            public bool GetFrameEventData(int index, FixtureEventData eventData)
            {
                throw new InvalidOperationException("Metadata fixture methods must not be invoked");
            }
        }

        sealed class OneParameterUtility
        {
            public static bool GetFrameEventData(int index)
            {
                throw new InvalidOperationException("Metadata fixture methods must not be invoked");
            }
        }

        sealed class ThreeParameterUtility
        {
            public static bool GetFrameEventData(int index, FixtureEventData eventData, bool extra)
            {
                throw new InvalidOperationException("Metadata fixture methods must not be invoked");
            }
        }

        sealed class LongFirstParameterUtility
        {
            public static bool GetFrameEventData(long index, FixtureEventData eventData)
            {
                throw new InvalidOperationException("Metadata fixture methods must not be invoked");
            }
        }

        sealed class ByRefFirstParameterUtility
        {
            public static bool GetFrameEventData(ref int index, FixtureEventData eventData)
            {
                throw new InvalidOperationException("Metadata fixture methods must not be invoked");
            }
        }

        sealed class WrongEventDataUtility
        {
            public static bool GetFrameEventData(int index, OtherEventData eventData)
            {
                throw new InvalidOperationException("Metadata fixture methods must not be invoked");
            }
        }

        sealed class WrongByRefEventDataUtility
        {
            public static bool GetFrameEventData(int index, ref OtherEventData eventData)
            {
                throw new InvalidOperationException("Metadata fixture methods must not be invoked");
            }
        }

        static class MetadataProbe
        {
            public static int StaticConstructorCalls;
            public static int GetterCalls;
            public static int MethodCalls;
        }

        sealed class MetadataOnlyUtility
        {
            static MetadataOnlyUtility()
            {
                MetadataProbe.StaticConstructorCalls++;
            }

            public static bool Enabled
            {
                get
                {
                    MetadataProbe.GetterCalls++;
                    throw new InvalidOperationException("Metadata fixture getters must not be invoked");
                }
            }

            public static bool GetFrameEventData(int index, FixtureEventData eventData)
            {
                MetadataProbe.MethodCalls++;
                throw new InvalidOperationException("Metadata fixture methods must not be invoked");
            }
        }
    }
}
