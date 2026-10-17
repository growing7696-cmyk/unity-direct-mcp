using System;
using System.Collections.Concurrent;
using System.Threading;
using UnityEditor;

namespace DirectMcp
{
    /// <summary>
    /// Runs work on Unity's main thread from HTTP worker threads.
    /// Pending work is cancelled (not dropped) when scripts reload, so callers
    /// always get an answer instead of a dead socket.
    /// </summary>
    public static class MainThread
    {
        sealed class Job
        {
            public Func<object> Work;
            public object Result;
            public Exception Error;
            public bool Cancelled;
            public readonly ManualResetEventSlim Done = new ManualResetEventSlim(false);
        }

        static readonly ConcurrentQueue<Job> Queue = new ConcurrentQueue<Job>();
        static int _mainThreadId;
        static volatile bool _reloading;

        public static bool IsReloading => _reloading;

        [InitializeOnLoadMethod]
        static void Init()
        {
            _mainThreadId = Thread.CurrentThread.ManagedThreadId;
            _reloading = false;
            EditorApplication.update += Drain;
        }

        public static bool IsMainThread => Thread.CurrentThread.ManagedThreadId == _mainThreadId;

        /// <summary>Blocks the calling worker thread until the main thread has run <paramref name="work"/>.</summary>
        public static T Invoke<T>(Func<T> work, int timeoutMs = 60000)
        {
            if (IsMainThread) return work();
            if (_reloading) throw new ReloadingException();

            var job = new Job { Work = () => work() };
            Queue.Enqueue(job);
            if (!job.Done.Wait(timeoutMs))
                throw new TimeoutException("Unity main thread did not respond in time. The Editor may be busy or showing a modal dialog.");
            if (job.Cancelled) throw new ReloadingException();
            if (job.Error != null) throw job.Error;
            return (T)job.Result;
        }

        static void Drain()
        {
            while (Queue.TryDequeue(out var job))
            {
                try { job.Result = job.Work(); }
                catch (Exception e) { job.Error = e; }
                finally { job.Done.Set(); }
            }
        }

        /// <summary>Called right before a domain reload: every waiting caller gets a ReloadingException.</summary>
        public static void CancelPending()
        {
            _reloading = true;
            while (Queue.TryDequeue(out var job))
            {
                job.Cancelled = true;
                job.Done.Set();
            }
        }
    }

    public sealed class ReloadingException : Exception
    {
        public ReloadingException() : base("Unity is reloading scripts (compile finished or play mode changed). Wait 3-5 seconds, then call unity_status.") { }
    }
}
