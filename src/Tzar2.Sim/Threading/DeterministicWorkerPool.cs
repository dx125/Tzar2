using System;
using System.Runtime.ExceptionServices;
using System.Threading;

namespace Tzar2.Sim.Threading;

/// <summary>
/// A unit of fork/join work. <see cref="Execute"/> runs once per job index, on any thread, in any order. A job may
/// write only to state owned by its index (its own entity range, its own output buffer); the caller merges outputs
/// in ascending job index after <see cref="DeterministicWorkerPool.Run"/> returns (TDD §7.3).
/// </summary>
public interface IParallelJob
{
    /// <summary>Runs job <paramref name="jobIndex"/>.</summary>
    void Execute(int jobIndex);
}

/// <summary>
/// Deterministic fork/join worker pool (TDD §7.3, ADR 0017). The number and boundaries of jobs are chosen by the
/// caller from the data alone, never from <see cref="WorkerCount"/>; workers only decide which thread runs which job.
/// With jobs that touch only their own outputs and a merge in job order, results are identical for any worker
/// count, including 1 (everything runs inline on the calling thread).
/// </summary>
/// <remarks>
/// <see cref="Run"/> allocates nothing. If jobs throw, every job still runs and the exception of the lowest failing
/// job index is rethrown, so the outcome does not depend on scheduling either.
/// </remarks>
public sealed class DeterministicWorkerPool : IDisposable
{
    /// <summary>Upper bound for <see cref="WorkerCount"/>.</summary>
    public const int MaxWorkers = 64;

    private readonly Thread[] _threads;
    private readonly SemaphoreSlim _start;
    private readonly ManualResetEventSlim _finished = new(false);
    private readonly object _errorLock = new();

    private IParallelJob? _job;
    private int _jobCount;
    private int _nextJob;
    private int _workersRunning;
    private int _failedJob;
    private Exception? _failure;
    private volatile bool _disposed;
    private int _running;

    /// <summary>Creates a pool that runs jobs on <paramref name="workerCount"/> threads, the caller's included.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="workerCount"/> is not in [1, <see cref="MaxWorkers"/>].</exception>
    public DeterministicWorkerPool(int workerCount)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(workerCount, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(workerCount, MaxWorkers);
        WorkerCount = workerCount;
        _start = new SemaphoreSlim(0, MaxWorkers);
        _threads = new Thread[workerCount - 1];
        for (int i = 0; i < _threads.Length; i++)
        {
            _threads[i] = new Thread(WorkerLoop) { IsBackground = true, Name = $"Tzar2.Sim worker {i + 1}" };
            _threads[i].Start();
        }
    }

    /// <summary>Threads that execute jobs, including the calling thread.</summary>
    public int WorkerCount { get; }

    /// <summary>Runs <paramref name="job"/> for job indices 0..<paramref name="jobCount"/>-1 and waits for all of them.</summary>
    /// <exception cref="ObjectDisposedException">The pool is disposed.</exception>
    /// <exception cref="InvalidOperationException">Called from inside a job or concurrently (nested fork/join would deadlock).</exception>
    public void Run(IParallelJob job, int jobCount)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentOutOfRangeException.ThrowIfNegative(jobCount);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0)
        {
            throw new InvalidOperationException("DeterministicWorkerPool.Run is not re-entrant: do not fork from inside a job.");
        }

        try
        {
            RunCore(job, jobCount);
        }
        finally
        {
            Volatile.Write(ref _running, 0);
        }
    }

    private void RunCore(IParallelJob job, int jobCount)
    {
        if (jobCount == 0)
        {
            return;
        }

        _job = job;
        _jobCount = jobCount;
        _nextJob = 0;
        _failure = null;
        _failedJob = int.MaxValue;
        int helpers = Math.Min(_threads.Length, jobCount - 1);
        if (helpers > 0)
        {
            _finished.Reset();
            Volatile.Write(ref _workersRunning, helpers);
            _start.Release(helpers);
        }

        Drain();
        if (helpers > 0)
        {
            _finished.Wait();
        }

        _job = null;
        if (_failure is not null)
        {
            Exception failure = _failure;
            _failure = null;
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_threads.Length > 0)
        {
            _start.Release(_threads.Length);
            foreach (Thread thread in _threads)
            {
                thread.Join();
            }
        }

        _start.Dispose();
        _finished.Dispose();
    }

    private void WorkerLoop()
    {
        while (true)
        {
            _start.Wait();
            if (_disposed)
            {
                return;
            }

            Drain();
            if (Interlocked.Decrement(ref _workersRunning) == 0)
            {
                _finished.Set();
            }
        }
    }

    private void Drain()
    {
        IParallelJob job = _job!;
        int count = _jobCount;
        while (true)
        {
            int index = Interlocked.Increment(ref _nextJob) - 1;
            if (index >= count)
            {
                return;
            }

            try
            {
                job.Execute(index);
            }
            catch (Exception ex)
            {
                // Every exception, OutOfMemoryException included, is captured: an unhandled exception on a helper thread
                // would kill the process, and the caller must not return while helpers are still running.
                lock (_errorLock)
                {
                    if (index < _failedJob)
                    {
                        _failedJob = index;
                        _failure = ex;
                    }
                }
            }
        }
    }
}
