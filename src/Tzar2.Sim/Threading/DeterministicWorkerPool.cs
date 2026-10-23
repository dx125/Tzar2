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
/// <para>
/// <see cref="Run"/> waits for the <b>jobs</b>, not for the helpers it woke (ADR 0214): a helper that the OS schedules
/// only after the other threads finished every job no longer holds the tick up. Each fork has a generation; jobs are
/// claimed with a compare-and-swap on one 64-bit word holding the generation and the next job index, so a helper that
/// wakes late can never take a job of a later fork.
/// </para>
/// </remarks>
public sealed class DeterministicWorkerPool : IDisposable
{
    /// <summary>Upper bound for <see cref="WorkerCount"/>.</summary>
    public const int MaxWorkers = 64;

    private readonly Thread[] _threads;
    private readonly object _gate = new();
    private readonly ManualResetEventSlim _finished = new(false);
    private readonly object _errorLock = new();

    // Written by Run under _gate before the generation is published; read by helpers under _gate.
    private IParallelJob? _job;
    private int _jobCount;
    private int _generation;

    // High 32 bits: the generation of the fork the claims belong to; low 32 bits: the next job index.
    private long _claims;
    private int _done;
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

        _failure = null;
        _failedJob = int.MaxValue;
        int generation;
        bool helpers = _threads.Length > 0 && jobCount > 1;
        lock (_gate)
        {
            generation = unchecked(_generation + 1);
            _generation = generation;
            _job = job;
            _jobCount = jobCount;
            Volatile.Write(ref _done, 0);
            _finished.Reset();
            Volatile.Write(ref _claims, (long)generation << 32);
            if (helpers)
            {
                Monitor.PulseAll(_gate);
            }
        }

        Drain(job, jobCount, generation);

        // Every job claimed and finished: the last one to finish set the event. Helpers that wake later find no job
        // of this generation and go back to sleep; nothing waits for them.
        _finished.Wait();
        lock (_gate)
        {
            _job = null;
        }

        if (_failure is not null)
        {
            Exception failure = _failure;
            _failure = null;
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        lock (_gate)
        {
            _disposed = true;
            Monitor.PulseAll(_gate);
        }

        foreach (Thread thread in _threads)
        {
            thread.Join();
        }

        _finished.Dispose();
    }

    private void WorkerLoop()
    {
        int seen = 0;
        while (true)
        {
            IParallelJob? job;
            int count, generation;
            lock (_gate)
            {
                while (!_disposed && (_generation == seen || _job is null))
                {
                    Monitor.Wait(_gate);
                }

                if (_disposed)
                {
                    return;
                }

                seen = _generation;
                generation = _generation;
                job = _job;
                count = _jobCount;
            }

            Drain(job!, count, generation);
        }
    }

    // Claims and runs jobs of `generation` until none is left; the thread that finishes the last job sets _finished.
    private void Drain(IParallelJob job, int count, int generation)
    {
        while (true)
        {
            long claims = Volatile.Read(ref _claims);
            int index = (int)(claims & 0xFFFF_FFFFL);
            if ((int)(claims >> 32) != generation || index >= count)
            {
                return;
            }

            if (Interlocked.CompareExchange(ref _claims, claims + 1, claims) != claims)
            {
                continue;
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

            if (Interlocked.Increment(ref _done) == count)
            {
                _finished.Set();
            }
        }
    }
}
