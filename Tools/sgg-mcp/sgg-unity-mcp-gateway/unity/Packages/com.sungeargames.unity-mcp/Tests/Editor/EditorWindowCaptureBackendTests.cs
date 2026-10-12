using System;
using System.Collections;
using NUnit.Framework;
using SGG.UnityMCP.Editor.Commands;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;

namespace SGG.UnityMCP.Editor.Tests
{
    public sealed class EditorWindowCaptureBackendTests
    {
        [Test]
        public void TargetAndApiHelpersFailClosedForMissingInputs()
        {
            Assert.That(EditorWindowCaptureBackend.IsFocusedTarget(null), Is.False);
            Assert.That(EditorWindowCaptureBackend.TryProveHostViewBinding(null, out _), Is.False);
            Assert.That(EditorWindowCaptureBackend.TryValidateTarget(null, "fnv1a32:00000000", out _, out var targetFailure), Is.False);
            Assert.That(targetFailure.Reason, Is.EqualTo("target_destroyed"));
            Assert.That(EditorWindowCaptureBackend.TryCapture(null, null, out _), Is.False);
        }

        [Test]
        public void CaptureApiHelperAcceptsOnlyTheExactBoolSignature()
        {
            var available = EditorWindowCaptureBackend.TryGetCaptureMethod(out var method, out var failure);
            if (!available)
            {
                Assert.That(method, Is.Null);
                Assert.That(failure, Is.Not.Empty);
                return;
            }

            Assert.That(method, Is.Not.Null);
            Assert.That(method.Name, Is.EqualTo("CaptureEditorWindow"));
            Assert.That(method.ReturnType, Is.EqualTo(typeof(bool)));
            Assert.That(method.IsStatic, Is.True);
            Assert.That(method.GetParameters(), Has.Length.EqualTo(2));
            Assert.That(method.GetParameters()[0].ParameterType, Is.EqualTo(typeof(EditorWindow)));
            Assert.That(method.GetParameters()[1].ParameterType, Is.EqualTo(typeof(RenderTexture)));
        }

        [Test]
        public void ScreenshotStatusProducesParseablePngDimensions()
        {
            var result = EditorScreenshotCommands.EditorWindowScreenshotStatus(
                "ok",
                null,
                0,
                "fnv1a32:00000000",
                "Library/SGGUnityMcp/screenshots/test.png",
                128,
                96,
                true,
                new string('a', 64),
                123,
                true,
                true,
                false);

            EditorWindowScreenshotPayload payload = null;
            Assert.DoesNotThrow(() => payload = JsonUtility.FromJson<EditorWindowScreenshotPayload>(result.DataJson));
            Assert.That(payload, Is.Not.Null);
            Assert.That(payload.width, Is.EqualTo(128));
            Assert.That(payload.height, Is.EqualTo(96));
            Assert.That(payload.dimensions, Is.Not.Null);
            Assert.That(payload.dimensions.width, Is.EqualTo(128));
            Assert.That(payload.dimensions.height, Is.EqualTo(96));
            Assert.That(payload.png, Is.Not.Null);
            Assert.That(payload.png.width, Is.EqualTo(128));
            Assert.That(payload.png.height, Is.EqualTo(96));
        }

        [TestCase("capture_publish_failed", "unity_exception")]
        [TestCase("capture_corrupt_artifact", "unity_exception")]
        [TestCase("path_reparse_point_rejected", "path_not_allowed")]
        [TestCase("capture_target_unavailable", "capture_target_unavailable")]
        public void ScreenshotFailuresUseGatewaySupportedErrorCodes(string rawCode, string expectedCode)
        {
            Assert.That(EditorScreenshotCommands.NormalizeEditorWindowCaptureErrorCode(rawCode), Is.EqualTo(expectedCode));
            Assert.That(GameViewCapturePolicy.UnsafePathCode, Is.EqualTo("path_not_allowed"));
        }

        [UnityTest]
        public IEnumerator CapturesSyntheticFocusedWindowWhenTheUnityApiIsAvailable()
        {
            if (!EditorWindowCaptureBackend.TryGetCaptureMethod(out _, out var apiFailure))
            {
                Assert.Ignore(apiFailure);
            }

            var previousFocus = EditorWindow.focusedWindow;
            var window = ScriptableObject.CreateInstance<SyntheticCaptureWindow>();
            try
            {
                window.titleContent = new GUIContent("SGG MCP synthetic capture");
                window.position = new Rect(80, 80, 128, 96);
                window.ShowUtility();
                window.Focus();
                window.Repaint();
                yield return null;
                yield return null;
                Assert.That(EditorWindowCaptureBackend.IsFocusedTarget(window), Is.True);

                Assert.That(EditorWindowCaptureBackend.TryProveHostViewBinding(window, out var bindingFailure), Is.True, bindingFailure);

                var target = RenderTexture.GetTemporary(128, 96, 0, RenderTextureFormat.ARGB32);
                try
                {
                    Assert.That(EditorWindowCaptureBackend.TryCapture(window, target, out var captureFailure), Is.True, captureFailure);
                }
                finally
                {
                    RenderTexture.ReleaseTemporary(target);
                }
            }
            finally
            {
                if (window != null)
                {
                    if (window.rootVisualElement.panel != null)
                        window.Close();
                    else
                        UnityEngine.Object.DestroyImmediate(window);
                }
                if (previousFocus != null)
                    previousFocus.Focus();
            }
        }

        [UnityTest]
        public IEnumerator CapturesSelectedExistingGameViewWhenTheUnityApiIsAvailable()
        {
            if (!EditorWindowCaptureBackend.TryGetCaptureMethod(out _, out var apiFailure))
            {
                Assert.Ignore(apiFailure);
            }

            var windows = Resources.FindObjectsOfTypeAll<EditorWindow>();
            EditorWindow gameView = null;
            for (var index = 0; index < windows.Length; index++)
            {
                var window = windows[index];
                if (window != null && string.Equals(window.GetType().FullName, "UnityEditor.GameView", StringComparison.Ordinal))
                {
                    gameView = window;
                    break;
                }
            }
            if (gameView == null)
            {
                Assert.Ignore("No existing UnityEditor.GameView is available in this Editor layout.");
            }

            var previousFocus = EditorWindow.focusedWindow;
            try
            {
                gameView.Focus();
                gameView.Repaint();
                yield return null;
                yield return null;

                Assert.That(EditorWindowCaptureBackend.IsFocusedTarget(gameView), Is.True);
                Assert.That(EditorWindowCaptureBackend.TryProveHostViewBinding(gameView, out var bindingFailure), Is.True, bindingFailure);

                var width = Mathf.CeilToInt(gameView.position.width);
                var height = Mathf.CeilToInt(gameView.position.height);
                Assert.That(width, Is.InRange(1, 4096));
                Assert.That(height, Is.InRange(1, 4096));
                var target = RenderTexture.GetTemporary(width, height, 0, RenderTextureFormat.ARGB32);
                try
                {
                    Assert.That(EditorWindowCaptureBackend.TryCapture(gameView, target, out var captureFailure), Is.True, captureFailure);
                }
                finally
                {
                    RenderTexture.ReleaseTemporary(target);
                }
            }
            finally
            {
                if (previousFocus != null)
                    previousFocus.Focus();
            }
        }

        sealed class SyntheticCaptureWindow : EditorWindow
        {
        }

        [Serializable]
        sealed class EditorWindowScreenshotPayload
        {
            public int width;
            public int height;
            public DimensionsPayload dimensions;
            public DimensionsPayload png;
        }

        [Serializable]
        sealed class DimensionsPayload
        {
            public int width;
            public int height;
        }
    }
}
