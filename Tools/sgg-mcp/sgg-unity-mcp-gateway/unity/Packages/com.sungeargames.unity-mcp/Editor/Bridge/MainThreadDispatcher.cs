using System;
using System.Collections.Generic;
using System.Threading;
using UnityEditor;
using UnityEngine;

namespace SGG.UnityMCP.Editor.Bridge
{
    public static class MainThreadDispatcher
    {
        static readonly LinkedList<Action> Pending = new LinkedList<Action>();
        static bool s_initialized;
        static int s_mainThreadId;

        public static bool IsMainThread => s_initialized && Thread.CurrentThread.ManagedThreadId == s_mainThreadId;

        public static int QueueDepth
        {
            get
            {
                lock (Pending)
                {
                    return Pending.Count;
                }
            }
        }

        public static void Initialize()
        {
            if (s_initialized)
            {
                return;
            }

            s_initialized = true;
            s_mainThreadId = Thread.CurrentThread.ManagedThreadId;
            EditorApplication.update += Drain;
        }

        public static void Enqueue(Action action)
        {
            if (action == null)
            {
                throw new ArgumentNullException(nameof(action));
            }

            lock (Pending)
            {
                Pending.AddLast(action);
            }
        }

        public static T Run<T>(Func<T> action, int timeoutMs)
        {
            if (action == null)
            {
                throw new ArgumentNullException(nameof(action));
            }

            if (IsMainThread)
            {
                return action();
            }

            T result = default;
            Exception exception = null;
            var completed = new ManualResetEventSlim(false);
            var workState = new MainThreadDispatchWorkState();

            LinkedListNode<Action> pendingNode;
            lock (Pending)
            {
                pendingNode = Pending.AddLast(() =>
                {
                    if (!workState.TryStart())
                    {
                        return;
                    }

                    try
                    {
                        result = action();
                    }
                    catch (Exception caught)
                    {
                        exception = caught;
                    }
                    finally
                    {
                        completed.Set();
                        workState.Complete();
                    }
                });
            }

            if (!completed.Wait(Math.Max(1, timeoutMs)))
            {
                if (workState.TryCancelBeforeStart())
                {
                    lock (Pending)
                    {
                        if (pendingNode.List != null)
                        {
                            Pending.Remove(pendingNode);
                        }
                    }

                    throw new MainThreadDispatchTimeoutException("cancelled_before_start", QueueDepth, false);
                }

                if (!completed.IsSet)
                {
                    throw new MainThreadDispatchTimeoutException("running", QueueDepth, true);
                }
            }

            if (exception != null)
            {
                throw exception;
            }

            return result;
        }

        static void Drain()
        {
            while (true)
            {
                Action action;
                lock (Pending)
                {
                    if (Pending.Count == 0)
                    {
                        return;
                    }

                    action = Pending.First.Value;
                    Pending.RemoveFirst();
                }

                try
                {
                    action();
                }
                catch (Exception exception)
                {
                    Debug.LogException(exception);
                }
            }
        }
    }

    internal sealed class MainThreadDispatchWorkState
    {
        const int Queued = 0;
        const int Running = 1;
        const int Completed = 2;
        const int CancelledBeforeStart = 3;
        int m_state = Queued;

        public bool IsCompleted => Volatile.Read(ref m_state) == Completed;

        public string StateName
        {
            get
            {
                switch (Volatile.Read(ref m_state))
                {
                    case Running: return "running";
                    case Completed: return "completed";
                    case CancelledBeforeStart: return "cancelled_before_start";
                    default: return "queued";
                }
            }
        }

        public bool TryStart()
        {
            return Interlocked.CompareExchange(ref m_state, Running, Queued) == Queued;
        }

        public bool TryCancelBeforeStart()
        {
            return Interlocked.CompareExchange(ref m_state, CancelledBeforeStart, Queued) == Queued;
        }

        public void Complete()
        {
            Volatile.Write(ref m_state, Completed);
        }
    }

    internal sealed class MainThreadDispatchTimeoutException : TimeoutException
    {
        public MainThreadDispatchTimeoutException(string workState, int queueDepthAtTimeout, bool lateExecutionPossible)
            : base("command_timeout")
        {
            WorkState = workState ?? "unknown";
            QueueDepthAtTimeout = Math.Max(0, queueDepthAtTimeout);
            LateExecutionPossible = lateExecutionPossible;
        }

        public string WorkState { get; private set; }
        public int QueueDepthAtTimeout { get; private set; }
        public bool LateExecutionPossible { get; private set; }
    }
}
