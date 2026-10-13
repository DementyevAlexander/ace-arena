using System;
using NUnit.Framework;
using SGG.UnityMCP.Editor.CanonicalPath;
using SGG.UnityMCP.Editor.Paths;
using UnityEditor;
using UnityEngine;

namespace SGG.UnityMCP.Editor.CanonicalPath.Tests
{
    public sealed class CanonicalPathProviderCorpusTests
    {
        const string CorpusPath = "Packages/com.sungeargames.unity-mcp/Tests/CanonicalPath/CanonicalPathCorpus.json";

        [Test]
        public void PublishedProviderMatchesEmbeddedDefaultMountCorpus()
        {
            var asset = AssetDatabase.LoadAssetAtPath<TextAsset>(CorpusPath);
            Assert.That(asset, Is.Not.Null, "missing embedded CanonicalPath corpus");
            var corpus = JsonUtility.FromJson<Corpus>(asset.text);
            Assert.That(corpus, Is.Not.Null);
            Assert.That(corpus.vectors, Is.Not.Null.And.Not.Empty);

            foreach (var vector in corpus.vectors)
            {
                Assert.That(CanonicalPathProviderAdapter.Normalize(vector.input), Is.EqualTo(vector.canonical), vector.name);
            }
        }

        [Test]
        public void SggContainmentRejectsRawTraversalBeforeProviderCleanup()
        {
            var paths = new CanonicalPathService();
            var root = new CanonicalPathValue("c:/u/game");
            var candidate = new CanonicalPathValue("c:/u/game/Assets/../ProjectSettings/ProjectSettings.asset");

            var exception = Assert.Throws<ArgumentException>(() => paths.AssertInsideProject(root, candidate));
            Assert.That(exception.Message, Does.Contain("path_traversal_rejected"));
        }

        [Serializable]
        sealed class Corpus
        {
            public Vector[] vectors;
        }

        [Serializable]
        sealed class Vector
        {
            public string name;
            public string input;
            public string canonical;
        }
    }
}
