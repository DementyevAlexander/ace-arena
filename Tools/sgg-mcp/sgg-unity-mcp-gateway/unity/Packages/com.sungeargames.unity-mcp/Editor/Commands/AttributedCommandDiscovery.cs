using System;
using System.Collections.Generic;
using UnityEditor;

namespace SGG.UnityMCP.Editor.Commands
{
    internal static class AttributedCommandDiscovery
    {
        /// <summary>
        /// Use Unity's attribute index to avoid reflecting unrelated domain types. The registry
        /// still visits the selected types in its original assembly/type/method order, preserving
        /// duplicate-command precedence rather than trusting TypeCache's unordered results.
        /// </summary>
        internal static HashSet<Type> CandidateTypes<T>() where T : Attribute
        {
            var types = new HashSet<Type>();
            foreach (var method in TypeCache.GetMethodsWithAttribute<T>())
            {
                var type = method.DeclaringType;
                if (method.IsStatic && type != null && !type.Assembly.IsDynamic) types.Add(type);
            }
            return types;
        }
    }
}
