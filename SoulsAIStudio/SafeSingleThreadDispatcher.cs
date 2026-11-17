using System;
using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;
using System.Threading;

namespace DSAnimStudio
{
    /// <summary>
    /// Runs actions on one dedicated thread (FMOD must be called from a single thread) and waits for each to finish.
    /// <para>[SAS] Internals rewritten, same public API. The original kept ONE shared action slot and one pair of
    /// AutoResetEvents. The UI thread is STA, so while it waited in Invoke Windows could dispatch sent messages on it
    /// (e.g. Alt+Tab → Main_Deactivated → SoundManager.StopAllSounds → Invoke again). The nested call overwrote the
    /// pending action and consumed the outer call's signal: the app froze (a wait whose signal never came) and then
    /// crashed with Exception("?????") when the worker woke up with no action. Now every call has its own completion
    /// signal and actions run in FIFO order, so nested / concurrent calls can't steal each other's work or signals.</para>
    /// </summary>
    public class SafeSingleThreadDispatcher : IDisposable
    {
        private sealed class WorkItem
        {
            public Action Action;
            public readonly ManualResetEventSlim Done = new ManualResetEventSlim(false);
            public ExceptionDispatchInfo Error;
        }

        private readonly BlockingCollection<WorkItem> queue = new BlockingCollection<WorkItem>();
        private readonly Thread thread;
        private volatile bool stopRequested = false;

        public SafeSingleThreadDispatcher()
        {
            thread = new Thread(ThreadProc) { IsBackground = true, Name = "SafeSingleThreadDispatcher" };
            thread.Start();
        }

        public void RequestStop()
        {
            if (stopRequested)
                return;
            stopRequested = true;
            queue.CompleteAdding(); // the worker finishes what is queued, then exits
            if (Thread.CurrentThread != thread)
                thread.Join();
        }

        public void Invoke(Action action)
        {
            if (action == null || stopRequested)
                return;

            // Called from inside an action (same thread): run it now, queueing it would wait on ourselves forever.
            if (Thread.CurrentThread == thread)
            {
                action();
                return;
            }

            var item = new WorkItem { Action = action };
            try
            {
                queue.Add(item);
            }
            catch (InvalidOperationException)
            {
                return; // stopped meanwhile (same as the original: calls after a stop are skipped)
            }

            item.Done.Wait();
            item.Done.Dispose();
            item.Error?.Throw(); // an exception in the action surfaces on the caller instead of killing the process
        }

        private void ThreadProc()
        {
            foreach (var item in queue.GetConsumingEnumerable())
            {
                try
                {
                    item.Action();
                }
                catch (Exception ex)
                {
                    item.Error = ExceptionDispatchInfo.Capture(ex);
                }
                finally
                {
                    item.Done.Set();
                }
            }
        }

        public void Dispose()
        {
            RequestStop();
        }
    }
}
