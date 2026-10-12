using System;
using System.Collections.Generic;
using System.Reflection;

namespace SGG.UnityMCP.Editor.Commands
{
    /// <summary>Build-local reflection cache; a new catalog receives a fresh resolver.</summary>
    internal sealed class ManifestHandlerResolver
    {
        readonly Assembly[] m_assemblies;
        readonly Func<Assembly, string, Type> m_typeLookup;
        readonly Dictionary<Assembly, Dictionary<string, Type>> m_types = new Dictionary<Assembly, Dictionary<string, Type>>();
        readonly Dictionary<string, MethodInfo> m_methods = new Dictionary<string, MethodInfo>(StringComparer.Ordinal);

        internal ManifestHandlerResolver(Assembly[] assemblies, Func<Assembly, string, Type> typeLookup = null)
        {
            m_assemblies = (Assembly[])(assemblies ?? throw new ArgumentNullException(nameof(assemblies))).Clone();
            m_typeLookup = typeLookup ?? ((assembly, name) => assembly.GetType(name));
        }

        internal MethodInfo Resolve(string handlerName)
        {
            if (string.IsNullOrEmpty(handlerName)) return null;
            if (m_methods.TryGetValue(handlerName, out var cached)) return cached;
            var separator = handlerName.LastIndexOf('.');
            if (separator <= 0 || separator == handlerName.Length - 1) return null;

            var typeName = handlerName.Substring(0, separator);
            var methodName = handlerName.Substring(separator + 1);
            // Preserve the previous ordered assembly precedence, including continuing
            // to a later type with the same name when an earlier type lacks the method.
            // Resolve types lazily: an unrelated later assembly must not invalidate
            // an already found handler by throwing during type loading.
            foreach (var assembly in m_assemblies)
            {
                if (!m_types.TryGetValue(assembly, out var types))
                {
                    types = new Dictionary<string, Type>(StringComparer.Ordinal);
                    m_types.Add(assembly, types);
                }
                if (!types.TryGetValue(typeName, out var type))
                {
                    type = m_typeLookup(assembly, typeName);
                    types.Add(typeName, type);
                }
                if (type == null) continue;
                var method = type.GetMethod(methodName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                if (method == null) continue;
                m_methods.Add(handlerName, method);
                return method;
            }
            m_methods.Add(handlerName, null);
            return null;
        }
    }
}
