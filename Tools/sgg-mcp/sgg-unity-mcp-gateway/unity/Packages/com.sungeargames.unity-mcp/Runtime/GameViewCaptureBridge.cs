using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

namespace SGG.UnityMCP.Runtime
{
    public sealed class GameViewCaptureBridge : MonoBehaviour
    {
        static GameViewCaptureBridge s_instance;
        string m_requestId;
        PendingCaptureRequest m_pendingCapture;
        Coroutine m_capture;
        PendingReadback m_pendingReadback;
        bool m_runInBackgroundLeaseActive;
        bool m_previousRunInBackground;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        static void InitializeRunner()
        {
            if (s_instance != null)
            {
                return;
            }

            var bridgeObject = new GameObject("SGG Unity MCP Game View Capture Runner")
            {
                tag = "EditorOnly",
                hideFlags = HideFlags.HideInHierarchy,
            };
            var runner = bridgeObject.AddComponent<GameViewCaptureBridge>();
            DontDestroyOnLoad(bridgeObject);
            s_instance = runner;
        }

        public static bool Begin(string requestId, int settleFrames, long deadlineUtcTicks, Action<CaptureProgress> progress, Action<CaptureResult> completed)
        {
            var runner = s_instance;
            if (runner == null || !runner.isActiveAndEnabled || !string.IsNullOrEmpty(runner.m_requestId)
                || runner.m_pendingCapture != null || runner.m_capture != null || runner.m_pendingReadback != null
                || string.IsNullOrEmpty(requestId) || completed == null)
            {
                return false;
            }

            runner.m_requestId = requestId;
            runner.m_previousRunInBackground = Application.runInBackground;
            runner.m_runInBackgroundLeaseActive = true;
            try
            {
                // Unity Recorder also enables background execution so EOF callbacks advance when the Editor is not foreground.
                Application.runInBackground = true;
                runner.m_pendingCapture = new PendingCaptureRequest(requestId, Mathf.Clamp(settleFrames, 0, 120), deadlineUtcTicks, progress, completed);
                return true;
            }
            catch (Exception)
            {
                runner.RestoreRunInBackground(requestId);
                runner.m_requestId = null;
                return false;
            }
        }

        void LateUpdate()
        {
            if (m_capture != null || m_pendingCapture == null)
            {
                return;
            }

            var pending = m_pendingCapture;
            m_pendingCapture = null;
            if (!OwnsRequest(pending.RequestId))
            {
                return;
            }

            InvokeProgress(pending.Progress, new CaptureProgress(pending.RequestId, "runtime_late_update", 0, 0, 0, 0));
            if (!OwnsRequest(pending.RequestId))
            {
                return;
            }
            m_capture = StartCoroutine(Capture(pending.RequestId, pending.SettleFrames, pending.DeadlineUtcTicks, pending.Progress, pending.Completed));
        }

        public static bool Cancel(string requestId)
        {
            var runner = s_instance;
            if (runner == null || !runner.OwnsRequest(requestId))
            {
                return false;
            }

            runner.ReleaseRequest(requestId, true);
            return true;
        }

        void OnDisable()
        {
            CleanupLifecycle();
        }

        void OnDestroy()
        {
            CleanupLifecycle();
        }

        IEnumerator Capture(string requestId, int settleFrames, long deadlineUtcTicks, Action<CaptureProgress> progress, Action<CaptureResult> completed)
        {
            var observed = 0;
            var startFrame = Time.frameCount;
            var startRenderedFrame = Time.renderedFrameCount;
            var lastFrame = startFrame;
            var lastRenderedFrame = startRenderedFrame;
            var requiredEndOfFrames = Math.Max(1, settleFrames);
            var completedEndOfFrames = 0;
            var waitForEndOfFrame = new WaitForEndOfFrame();
            while (completedEndOfFrames < requiredEndOfFrames)
            {
                yield return waitForEndOfFrame;
                if (!OwnsRequest(requestId)) yield break;
                var frame = Time.frameCount;
                var renderedFrame = Time.renderedFrameCount;
                if (frame == lastFrame || renderedFrame == lastRenderedFrame)
                {
                    continue;
                }

                lastFrame = frame;
                lastRenderedFrame = renderedFrame;
                completedEndOfFrames++;
                if (settleFrames > 0) observed++;
                InvokeProgress(progress, new CaptureProgress(requestId, "settling", observed, frame, renderedFrame, 0));
                if (!OwnsRequest(requestId)) yield break;
            }

            var width = Screen.width;
            var height = Screen.height;
            var submittedFrame = Time.frameCount;
            var submittedUtcTicks = DateTime.UtcNow.Ticks;
            RenderTexture target = null;
            var previousActive = RenderTexture.active;
            try
            {
                if (width < 1 || height < 1)
                {
                    throw new InvalidOperationException("The Game View screen dimensions are not available.");
                }

                target = RenderTexture.GetTemporary(width, height, 0, RenderTextureFormat.ARGB32);
                RenderTexture.active = target;
                GL.Clear(true, true, new Color(1f, 0f, 1f, 0f));
                RenderTexture.active = previousActive;
                ScreenCapture.CaptureScreenshotIntoRenderTexture(target);
            }
            catch (Exception)
            {
                if (target != null) RenderTexture.ReleaseTemporary(target);
                RenderTexture.active = previousActive;
                m_capture = null;
                ReleaseRequest(requestId, false);
                InvokeCompleted(completed, CaptureResult.Failure(
                    requestId,
                    width,
                    height,
                    startFrame,
                    submittedFrame,
                    Time.frameCount,
                    submittedUtcTicks,
                    DateTime.UtcNow.Ticks,
                    "runtime_capture_failed",
                    "none",
                    false));
                yield break;
            }
            var useAsyncReadback = SupportsAsyncReadback();
            var readbackBackend = useAsyncReadback ? "async_gpu_readback" : "synchronous_readpixels_unsupported_async";
            InvokeProgress(progress, new CaptureProgress(requestId, "submitted", observed, lastFrame, lastRenderedFrame, submittedFrame, width, height, readbackBackend));
            if (!OwnsRequest(requestId))
            {
                RenderTexture.ReleaseTemporary(target);
                yield break;
            }
            if (DateTime.UtcNow.Ticks >= deadlineUtcTicks)
            {
                RenderTexture.ReleaseTemporary(target);
                m_capture = null;
                ReleaseRequest(requestId, false);
                InvokeCompleted(completed, CaptureResult.Failure(requestId, width, height, startFrame, submittedFrame, Time.frameCount, submittedUtcTicks, DateTime.UtcNow.Ticks, "capture_timeout", readbackBackend, true));
                yield break;
            }

            if (useAsyncReadback)
            {
                var pending = new PendingReadback(this, requestId, target, width, height, startFrame, submittedFrame, submittedUtcTicks, deadlineUtcTicks, completed);
                m_pendingReadback = pending;
                m_capture = null;
                try
                {
                    AsyncGPUReadback.Request(target, 0, TextureFormat.RGBA32, request => CompleteAsyncReadback(pending, request));
                }
                catch (Exception)
                {
                    pending.Abandoned = true;
                    RenderTexture.ReleaseTemporary(target);
                    m_pendingReadback = null;
                    ReleaseRequest(requestId, false);
                    InvokeCompleted(completed, CaptureResult.Failure(requestId, width, height, startFrame, submittedFrame, Time.frameCount, submittedUtcTicks, DateTime.UtcNow.Ticks, "async_gpu_readback_failed", readbackBackend, false));
                }
                yield break;
            }

            CaptureResult result;
            try
            {
                result = CompleteSynchronousReadback(requestId, target, width, height, startFrame, submittedFrame, submittedUtcTicks, deadlineUtcTicks);
            }
            catch (Exception)
            {
                result = CaptureResult.Failure(requestId, width, height, startFrame, submittedFrame, Time.frameCount, submittedUtcTicks, DateTime.UtcNow.Ticks, "synchronous_readback_failed", readbackBackend, false);
            }
            finally
            {
                try
                {
                    RenderTexture.ReleaseTemporary(target);
                }
                catch (Exception)
                {
                    result = CaptureResult.Failure(requestId, width, height, startFrame, submittedFrame, Time.frameCount, submittedUtcTicks, DateTime.UtcNow.Ticks, "synchronous_readback_failed", readbackBackend, false);
                }
                m_capture = null;
                ReleaseRequest(requestId, false);
            }
            InvokeCompleted(completed, result);
        }

        static bool SupportsAsyncReadback()
        {
            if (!SystemInfo.supportsAsyncGPUReadback)
            {
                return false;
            }

            var format = GraphicsFormatUtility.GetGraphicsFormat(TextureFormat.RGBA32, false);
#if UNITY_2023_2_OR_NEWER
            return SystemInfo.IsFormatSupported(format, GraphicsFormatUsage.ReadPixels);
#else
            return SystemInfo.IsFormatSupported(format, FormatUsage.ReadPixels);
#endif
        }

        static CaptureResult CompleteSynchronousReadback(string requestId, RenderTexture target, int width, int height, int startFrame, int submittedFrame, long submittedUtcTicks, long deadlineUtcTicks)
        {
            if (DateTime.UtcNow.Ticks >= deadlineUtcTicks)
            {
                return CaptureResult.Failure(requestId, width, height, startFrame, submittedFrame, Time.frameCount, submittedUtcTicks, DateTime.UtcNow.Ticks, "capture_timeout", "synchronous_readpixels_unsupported_async", true);
            }
            var previousActive = RenderTexture.active;
            Texture2D texture = null;
            try
            {
                texture = new Texture2D(width, height, TextureFormat.RGBA32, false);
                RenderTexture.active = target;
                texture.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                texture.Apply(false, false);
                var pixels = OrientGameViewPixels(texture, width, height);
                var metrics = SampleMetrics.FromPixels(pixels, width, height);
                var png = texture.EncodeToPNG();
                var completionTicks = DateTime.UtcNow.Ticks;
                return completionTicks >= deadlineUtcTicks
                    ? CaptureResult.Failure(requestId, width, height, startFrame, submittedFrame, Time.frameCount, submittedUtcTicks, completionTicks, "capture_timeout", "synchronous_readpixels_unsupported_async", true)
                    : CaptureResult.Success(requestId, width, height, startFrame, submittedFrame, Time.frameCount, submittedUtcTicks, completionTicks, png, metrics, "synchronous_readpixels_unsupported_async");
            }
            catch (Exception)
            {
                return CaptureResult.Failure(requestId, width, height, startFrame, submittedFrame, Time.frameCount, submittedUtcTicks, DateTime.UtcNow.Ticks, "synchronous_readback_failed", "synchronous_readpixels_unsupported_async", false);
            }
            finally
            {
                RenderTexture.active = previousActive;
                if (texture != null) Destroy(texture);
            }
        }

        static void CompleteAsyncReadback(PendingReadback pending, AsyncGPUReadbackRequest request)
        {
            CaptureResult result;
            try
            {
                if (pending.Abandoned)
                {
                    result = CaptureResult.AbandonedResult(pending.RequestId, pending.Width, pending.Height, pending.StartFrame, pending.SubmissionFrame, pending.SubmissionUtcTicks);
                }
                else if (DateTime.UtcNow.Ticks >= pending.DeadlineUtcTicks)
                {
                    result = CaptureResult.Failure(pending.RequestId, pending.Width, pending.Height, pending.StartFrame, pending.SubmissionFrame, Time.frameCount, pending.SubmissionUtcTicks, DateTime.UtcNow.Ticks, "capture_timeout", "async_gpu_readback", true);
                }
                else if (request.hasError)
                {
                    result = CaptureResult.Failure(pending.RequestId, pending.Width, pending.Height, pending.StartFrame, pending.SubmissionFrame, Time.frameCount, pending.SubmissionUtcTicks, DateTime.UtcNow.Ticks, "async_gpu_readback_failed", "async_gpu_readback", false);
                }
                else
                {
                    var data = request.GetData<byte>();
                    var raw = new byte[data.Length];
                    data.CopyTo(raw);
                    var texture = new Texture2D(pending.Width, pending.Height, TextureFormat.RGBA32, false);
                    try
                    {
                        texture.LoadRawTextureData(raw);
                        texture.Apply(false, false);
                        var pixels = OrientGameViewPixels(texture, pending.Width, pending.Height);
                        var metrics = SampleMetrics.FromPixels(pixels, pending.Width, pending.Height);
                        var png = texture.EncodeToPNG();
                        var completionTicks = DateTime.UtcNow.Ticks;
                        result = completionTicks >= pending.DeadlineUtcTicks
                            ? CaptureResult.Failure(pending.RequestId, pending.Width, pending.Height, pending.StartFrame, pending.SubmissionFrame, Time.frameCount, pending.SubmissionUtcTicks, completionTicks, "capture_timeout", "async_gpu_readback", true)
                            : CaptureResult.Success(pending.RequestId, pending.Width, pending.Height, pending.StartFrame, pending.SubmissionFrame, Time.frameCount, pending.SubmissionUtcTicks, completionTicks, png, metrics, "async_gpu_readback");
                    }
                    finally
                    {
                        Destroy(texture);
                    }
                }
            }
            catch (Exception)
            {
                result = CaptureResult.Failure(pending.RequestId, pending.Width, pending.Height, pending.StartFrame, pending.SubmissionFrame, Time.frameCount, pending.SubmissionUtcTicks, DateTime.UtcNow.Ticks, "async_gpu_readback_failed", "async_gpu_readback", false);
            }
            finally
            {
                RenderTexture.ReleaseTemporary(pending.Target);
                if (pending.Owner.OwnsPendingReadback(pending))
                {
                    pending.Owner.m_pendingReadback = null;
                    pending.Owner.ReleaseRequest(pending.RequestId, false);
                }
            }
            if (!pending.SuppressCompletion) InvokeCompleted(pending.Completed, result);
        }

        static Color32[] OrientGameViewPixels(Texture2D texture, int width, int height)
        {
            var pixels = texture.GetPixels32();
            if (!SystemInfo.graphicsUVStartsAtTop)
            {
                return pixels;
            }

            for (var y = 0; y < height / 2; y++)
            {
                var oppositeY = height - y - 1;
                for (var x = 0; x < width; x++)
                {
                    var top = (y * width) + x;
                    var bottom = (oppositeY * width) + x;
                    var pixel = pixels[top];
                    pixels[top] = pixels[bottom];
                    pixels[bottom] = pixel;
                }
            }
            texture.SetPixels32(pixels);
            texture.Apply(false, false);
            return pixels;
        }

        bool OwnsRequest(string requestId)
        {
            return ReferenceEquals(s_instance, this)
                && !string.IsNullOrEmpty(requestId)
                && string.Equals(m_requestId, requestId, StringComparison.Ordinal);
        }

        bool OwnsPendingReadback(PendingReadback pending)
        {
            return pending != null
                && ReferenceEquals(s_instance, this)
                && ReferenceEquals(m_pendingReadback, pending)
                && string.Equals(m_requestId, pending.RequestId, StringComparison.Ordinal);
        }

        void ReleaseRequest(string requestId, bool suppressReadbackCompletion)
        {
            if (!OwnsRequest(requestId))
            {
                return;
            }

            m_pendingCapture = null;
            if (m_pendingReadback != null && string.Equals(m_pendingReadback.RequestId, requestId, StringComparison.Ordinal))
            {
                if (suppressReadbackCompletion)
                {
                    m_pendingReadback.Abandoned = true;
                    m_pendingReadback.SuppressCompletion = true;
                }
                m_pendingReadback = null;
            }
            if (m_capture != null)
            {
                var capture = m_capture;
                m_capture = null;
                StopCoroutine(capture);
            }
            RestoreRunInBackground(requestId);
            m_requestId = null;
        }

        void RestoreRunInBackground(string requestId)
        {
            if (!m_runInBackgroundLeaseActive || !OwnsRequest(requestId))
            {
                return;
            }

            var previous = m_previousRunInBackground;
            m_runInBackgroundLeaseActive = false;
            Application.runInBackground = previous;
        }

        void CleanupLifecycle()
        {
            m_pendingCapture = null;
            if (m_pendingReadback != null)
            {
                // The GPU request callback remains the sole owner allowed to release its temporary target.
                m_pendingReadback.Abandoned = true;
                m_pendingReadback.SuppressCompletion = true;
                m_pendingReadback = null;
            }
            if (m_runInBackgroundLeaseActive && !string.IsNullOrEmpty(m_requestId))
            {
                RestoreRunInBackground(m_requestId);
            }
            m_requestId = null;
            if (ReferenceEquals(s_instance, this))
            {
                s_instance = null;
            }
            if (m_capture != null)
            {
                var capture = m_capture;
                m_capture = null;
                StopCoroutine(capture);
            }
        }

        sealed class PendingCaptureRequest
        {
            public PendingCaptureRequest(string requestId, int settleFrames, long deadlineUtcTicks, Action<CaptureProgress> progress, Action<CaptureResult> completed)
            {
                RequestId = requestId;
                SettleFrames = settleFrames;
                DeadlineUtcTicks = deadlineUtcTicks;
                Progress = progress;
                Completed = completed;
            }

            public readonly string RequestId;
            public readonly int SettleFrames;
            public readonly long DeadlineUtcTicks;
            public readonly Action<CaptureProgress> Progress;
            public readonly Action<CaptureResult> Completed;
        }

        static void InvokeProgress(Action<CaptureProgress> callback, CaptureProgress progress)
        {
            if (callback == null) return;
            try { callback(progress); }
            catch (Exception) { }
        }

        static void InvokeCompleted(Action<CaptureResult> callback, CaptureResult result)
        {
            try { callback(result); }
            catch (Exception) { }
        }

        public readonly struct CaptureProgress
        {
            public CaptureProgress(string requestId, string stage, int settledFrames, int lastSettledFrame, int lastRenderedFrame, int submissionFrame, int width = 0, int height = 0, string readbackBackend = "pending")
            {
                RequestId = requestId;
                Stage = stage;
                SettledFrames = settledFrames;
                LastSettledFrame = lastSettledFrame;
                LastRenderedFrame = lastRenderedFrame;
                SubmissionFrame = submissionFrame;
                Width = width;
                Height = height;
                ReadbackBackend = readbackBackend;
            }

            public string RequestId { get; }
            public string Stage { get; }
            public int SettledFrames { get; }
            public int LastSettledFrame { get; }
            public int LastRenderedFrame { get; }
            public int SubmissionFrame { get; }
            public int Width { get; }
            public int Height { get; }
            public string ReadbackBackend { get; }
        }

        public sealed class CaptureResult
        {
            public string RequestId;
            public bool Succeeded;
            public string Error;
            public int Width;
            public int Height;
            public int StartFrame;
            public int SubmissionFrame;
            public int CompletionFrame;
            public long SubmissionUtcTicks;
            public long CompletionUtcTicks;
            public byte[] Png;
            public SampleMetrics Metrics;
            public string ReadbackBackend;
            public bool Abandoned;
            public bool DeadlineExceeded;

            public static CaptureResult Success(string requestId, int width, int height, int startFrame, int submissionFrame, int completionFrame, long submissionUtcTicks, long completionUtcTicks, byte[] png, SampleMetrics metrics, string readbackBackend)
            {
                return new CaptureResult
                {
                    RequestId = requestId,
                    Succeeded = true,
                    Width = width,
                    Height = height,
                    StartFrame = startFrame,
                    SubmissionFrame = submissionFrame,
                    CompletionFrame = completionFrame,
                    SubmissionUtcTicks = submissionUtcTicks,
                    CompletionUtcTicks = completionUtcTicks,
                    Png = png,
                    Metrics = metrics,
                    ReadbackBackend = readbackBackend,
                };
            }

            public static CaptureResult Failure(string requestId, int width, int height, int startFrame, int submissionFrame, int completionFrame, long submissionUtcTicks, long completionUtcTicks, string error, string readbackBackend, bool deadlineExceeded)
            {
                return new CaptureResult
                {
                    RequestId = requestId,
                    Error = error ?? string.Empty,
                    Width = width,
                    Height = height,
                    StartFrame = startFrame,
                    SubmissionFrame = submissionFrame,
                    CompletionFrame = completionFrame,
                    SubmissionUtcTicks = submissionUtcTicks,
                    CompletionUtcTicks = completionUtcTicks,
                    ReadbackBackend = readbackBackend,
                    DeadlineExceeded = deadlineExceeded,
                };
            }

            public static CaptureResult AbandonedResult(string requestId, int width, int height, int startFrame, int submissionFrame, long submissionUtcTicks)
            {
                return new CaptureResult
                {
                    RequestId = requestId,
                    Error = "readback_abandoned",
                    Width = width,
                    Height = height,
                    StartFrame = startFrame,
                    SubmissionFrame = submissionFrame,
                    CompletionFrame = Time.frameCount,
                    SubmissionUtcTicks = submissionUtcTicks,
                    CompletionUtcTicks = DateTime.UtcNow.Ticks,
                    ReadbackBackend = "async_gpu_readback",
                    Abandoned = true,
                };
            }
        }

        sealed class PendingReadback
        {
            public PendingReadback(GameViewCaptureBridge owner, string requestId, RenderTexture target, int width, int height, int startFrame, int submissionFrame, long submissionUtcTicks, long deadlineUtcTicks, Action<CaptureResult> completed)
            {
                Owner = owner;
                RequestId = requestId;
                Target = target;
                Width = width;
                Height = height;
                StartFrame = startFrame;
                SubmissionFrame = submissionFrame;
                SubmissionUtcTicks = submissionUtcTicks;
                DeadlineUtcTicks = deadlineUtcTicks;
                Completed = completed;
            }

            public readonly GameViewCaptureBridge Owner;
            public readonly string RequestId;
            public readonly RenderTexture Target;
            public readonly int Width;
            public readonly int Height;
            public readonly int StartFrame;
            public readonly int SubmissionFrame;
            public readonly long SubmissionUtcTicks;
            public readonly long DeadlineUtcTicks;
            public readonly Action<CaptureResult> Completed;
            public bool Abandoned;
            public bool SuppressCompletion;
        }

        public readonly struct SampleMetrics
        {
            public SampleMetrics(int count, int zero, int nearBlack, int transparent, int uniform, int unique, int sentinel)
            {
                Count = count;
                Zero = zero;
                NearBlack = nearBlack;
                Transparent = transparent;
                Uniform = uniform;
                Unique = unique;
                Sentinel = sentinel;
            }

            public int Count { get; }
            public int Zero { get; }
            public int NearBlack { get; }
            public int Transparent { get; }
            public int Uniform { get; }
            public int Unique { get; }
            public int Sentinel { get; }

            public static SampleMetrics FromPixels(Color32[] pixels, int width, int height)
            {
                if (pixels == null || pixels.Length == 0 || width < 1 || height < 1)
                {
                    return new SampleMetrics(0, 0, 0, 0, 0, 0, 0);
                }

                var columns = Math.Min(64, width);
                var rows = Math.Min(64, height);
                var zero = 0;
                var nearBlack = 0;
                var transparent = 0;
                var uniform = 0;
                var count = 0;
                var sentinel = 0;
                var unique = new HashSet<uint>();
                var first = pixels[0];
                for (var row = 0; row < rows; row++)
                {
                    var y = rows == 1 ? 0 : row * (height - 1) / (rows - 1);
                    for (var column = 0; column < columns; column++)
                    {
                        var x = columns == 1 ? 0 : column * (width - 1) / (columns - 1);
                        var pixel = pixels[(y * width) + x];
                        count++;
                        if (pixel.r == 0 && pixel.g == 0 && pixel.b == 0 && pixel.a == 0) zero++;
                        if (pixel.r <= 3 && pixel.g <= 3 && pixel.b <= 3) nearBlack++;
                        if (pixel.a == 0) transparent++;
                        if (pixel.r == 255 && pixel.g == 0 && pixel.b == 255 && pixel.a == 0) sentinel++;
                        if (pixel.r == first.r && pixel.g == first.g && pixel.b == first.b && pixel.a == first.a) uniform++;
                        unique.Add((uint)(pixel.r | (pixel.g << 8) | (pixel.b << 16) | (pixel.a << 24)));
                    }
                }

                return new SampleMetrics(count, zero, nearBlack, transparent, uniform, unique.Count, sentinel);
            }
        }
    }
}
