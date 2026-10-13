using System;
using SGG.UnityMCP.Editor.Setup;

namespace SGG.UnityMCP.Editor.UI.State
{
    internal enum CanonicalPathDependencyAction
    {
        None,
        Install,
        Update,
        Manual,
        Resolving,
        Reload,
        Invalid,
        Busy,
    }

    internal sealed class CanonicalPathDependencyState
    {
        CanonicalPathDependencyState(
            bool registered,
            string installedVersion,
            string source,
            string declaredVersion,
            string requiredVersion,
            bool requestRunning,
            bool providerAvailable,
            bool manifestReadable)
        {
            Registered = registered;
            InstalledVersion = installedVersion ?? string.Empty;
            Source = source ?? string.Empty;
            DeclaredVersion = declaredVersion ?? string.Empty;
            RequiredVersion = requiredVersion ?? string.Empty;
            RequestRunning = requestRunning;
            ProviderAvailable = providerAvailable;
            ManifestReadable = manifestReadable;
            Action = DetermineAction();
        }

        public CanonicalPathDependencyAction Action { get; }
        public string InstalledVersion { get; }
        public string Source { get; }
        public string DeclaredVersion { get; }
        public string RequiredVersion { get; }
        public bool Registered { get; }
        public bool RequestRunning { get; }
        public bool ProviderAvailable { get; }
        public bool ManifestReadable { get; }
        public bool CanApply => Action == CanonicalPathDependencyAction.Install || Action == CanonicalPathDependencyAction.Update;
        public bool IsUpdate => Action == CanonicalPathDependencyAction.Update;
        public bool IsReady => Action == CanonicalPathDependencyAction.None;
        public bool NeedsAttention => !IsReady;
        public bool IsBusy => Action == CanonicalPathDependencyAction.Busy;

        public static CanonicalPathDependencyState Evaluate(
            bool registered,
            string installedVersion,
            string source,
            string declaredVersion,
            string requiredVersion,
            bool requestRunning,
            bool providerAvailable,
            bool manifestReadable)
        {
            return new CanonicalPathDependencyState(
                registered, installedVersion, source, declaredVersion, requiredVersion,
                requestRunning, providerAvailable, manifestReadable);
        }

        // Evidence equality binds a UI confirmation; it is not mutation approval.
        public bool SameEvidence(CanonicalPathDependencyState other)
        {
            return other != null
                && Registered == other.Registered
                && string.Equals(InstalledVersion, other.InstalledVersion, StringComparison.Ordinal)
                && string.Equals(Source, other.Source, StringComparison.Ordinal)
                && string.Equals(DeclaredVersion, other.DeclaredVersion, StringComparison.Ordinal)
                && string.Equals(RequiredVersion, other.RequiredVersion, StringComparison.Ordinal)
                && RequestRunning == other.RequestRunning
                && ProviderAvailable == other.ProviderAvailable
                && ManifestReadable == other.ManifestReadable;
        }

        CanonicalPathDependencyAction DetermineAction()
        {
            if (RequestRunning)
            {
                return CanonicalPathDependencyAction.Busy;
            }

            if (!ManifestReadable || !UnityCliUpdateCheck.TryCompareSemVer(RequiredVersion, RequiredVersion, out _))
            {
                return CanonicalPathDependencyAction.Invalid;
            }

            if (!Registered)
            {
                if (DeclaredVersion.Length == 0)
                {
                    return CanonicalPathDependencyAction.Install;
                }

                return IsFileOrGitDeclaration(DeclaredVersion)
                    ? CanonicalPathDependencyAction.Manual
                    : CanonicalPathDependencyAction.Resolving;
            }

            if (!UnityCliUpdateCheck.TryCompareSemVer(InstalledVersion, RequiredVersion, out var installedComparison))
            {
                return CanonicalPathDependencyAction.Invalid;
            }

            if (!string.Equals(Source, "Registry", StringComparison.Ordinal))
            {
                return installedComparison >= 0 && ProviderAvailable
                    ? CanonicalPathDependencyAction.None
                    : CanonicalPathDependencyAction.Manual;
            }

            if (installedComparison >= 0)
            {
                return ProviderAvailable ? CanonicalPathDependencyAction.None : CanonicalPathDependencyAction.Reload;
            }

            if (DeclaredVersion.Length == 0 || string.Equals(DeclaredVersion, InstalledVersion, StringComparison.Ordinal))
            {
                return CanonicalPathDependencyAction.Update;
            }

            if (UnityCliUpdateCheck.TryCompareSemVer(DeclaredVersion, RequiredVersion, out var declaredRequiredComparison)
                && declaredRequiredComparison >= 0
                && UnityCliUpdateCheck.TryCompareSemVer(DeclaredVersion, InstalledVersion, out var declaredInstalledComparison)
                && declaredInstalledComparison > 0)
            {
                return CanonicalPathDependencyAction.Resolving;
            }

            return CanonicalPathDependencyAction.Manual;
        }

        static bool IsFileOrGitDeclaration(string value)
        {
            return value.StartsWith("file:", StringComparison.OrdinalIgnoreCase)
                || value.StartsWith("git:", StringComparison.OrdinalIgnoreCase)
                || value.StartsWith("git+", StringComparison.OrdinalIgnoreCase)
                || value.StartsWith("git@", StringComparison.OrdinalIgnoreCase)
                || value.StartsWith("ssh:", StringComparison.OrdinalIgnoreCase)
                || value.StartsWith("http:", StringComparison.OrdinalIgnoreCase)
                || value.StartsWith("https:", StringComparison.OrdinalIgnoreCase);
        }
    }
}
