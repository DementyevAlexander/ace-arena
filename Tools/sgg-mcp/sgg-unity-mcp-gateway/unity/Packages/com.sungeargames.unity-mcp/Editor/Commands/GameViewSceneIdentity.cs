using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using UnityEngine.SceneManagement;

namespace SGG.UnityMCP.Editor.Commands
{
    internal static class GameViewSceneIdentity
    {
        internal const int MaxLoadedScenes = 256;
        const int MaxScenePathLength = 512;

        internal static ulong HandleIdentity(Scene scene)
        {
#if UNITY_6000_4_OR_NEWER
            return scene.handle.GetRawData();
#else
            return LegacyHandleIdentity(scene.handle);
#endif
        }

        internal static ulong LegacyHandleIdentity(int handle)
        {
            return unchecked((ulong)(uint)handle);
        }

        internal static bool TryCaptureLoadedScenes(out LoadedSceneSetIdentity identity)
        {
            var count = SceneManager.sceneCount;
            if (count < 0 || count > MaxLoadedScenes)
            {
                identity = default;
                return false;
            }

            var entries = new List<SceneEntry>(count);
            for (var index = 0; index < count; index++)
            {
                var scene = SceneManager.GetSceneAt(index);
                if (!scene.IsValid() || !scene.isLoaded)
                {
                    identity = default;
                    return false;
                }
                var path = (scene.path ?? string.Empty).Replace('\\', '/');
                if (path.Length > MaxScenePathLength)
                {
                    identity = default;
                    return false;
                }
                entries.Add(new SceneEntry(HandleIdentity(scene), path));
            }
            entries.Sort(SceneEntry.Compare);

            var builder = new StringBuilder();
            builder.Append(entries.Count).Append('|');
            for (var index = 0; index < entries.Count; index++)
            {
                var entry = entries[index];
                builder.Append(entry.Handle.ToString(CultureInfo.InvariantCulture)).Append(':');
                builder.Append(entry.Path.Length).Append(':').Append(entry.Path).Append('|');
            }
            using (var sha256 = SHA256.Create())
            {
                var digest = sha256.ComputeHash(Encoding.UTF8.GetBytes(builder.ToString()));
                var hash = new StringBuilder(digest.Length * 2);
                for (var index = 0; index < digest.Length; index++) hash.Append(digest[index].ToString("x2"));
                identity = new LoadedSceneSetIdentity(entries.Count, hash.ToString());
                return true;
            }
        }

        internal static bool Matches(LoadedSceneSetIdentity scheduled, LoadedSceneSetIdentity current)
        {
            return scheduled.Count == current.Count
                && !string.IsNullOrEmpty(scheduled.Fingerprint)
                && string.Equals(scheduled.Fingerprint, current.Fingerprint, StringComparison.Ordinal);
        }

        readonly struct SceneEntry
        {
            internal SceneEntry(ulong handle, string path)
            {
                Handle = handle;
                Path = path ?? string.Empty;
            }

            internal readonly ulong Handle;
            internal readonly string Path;

            internal static int Compare(SceneEntry left, SceneEntry right)
            {
                var handle = left.Handle.CompareTo(right.Handle);
                return handle != 0 ? handle : string.Compare(left.Path, right.Path, StringComparison.Ordinal);
            }
        }
    }

    internal readonly struct LoadedSceneSetIdentity
    {
        internal LoadedSceneSetIdentity(int count, string fingerprint)
        {
            Count = count;
            Fingerprint = fingerprint ?? string.Empty;
        }

        internal int Count { get; }
        internal string Fingerprint { get; }
    }
}
