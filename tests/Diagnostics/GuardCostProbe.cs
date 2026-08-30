using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace Chatter.Testing.Core.Diagnostics
{
    /// <summary>
    /// A warmed, single-threaded probe that measures the per-operation cost of a cheap operation — typically
    /// the off-state telemetry guard — in nanoseconds and allocated bytes, using nothing but the BCL.
    /// </summary>
    /// <remarks>
    /// THREAD AFFINITY: allocation is measured with <see cref="GC.GetAllocatedBytesForCurrentThread"/>, which
    /// reports the CALLING THREAD's allocation counter only. Every phase therefore runs SYNCHRONOUSLY on the
    /// calling thread, and the measured operation must not allocate on another thread or the reported bytes
    /// are meaningless. There is deliberately NO async overload: awaiting could resume on a different thread
    /// and silently invalidate the allocation delta.
    ///
    /// STABILITY: tiered JIT re-compiles a hot method mid-run, so the probe first runs a substantial warm-up
    /// phase and then several measured batches, reporting the MEDIAN across batches rather than the mean. One
    /// slow batch (a rejit, a GC pause, a scheduler preemption) therefore cannot fail a threshold assertion.
    /// </remarks>
    public static class GuardCostProbe
    {
        public const int DefaultWarmupIterations = 200_000;
        public const int DefaultBatchCount = 7;
        public const int DefaultIterationsPerBatch = 100_000;

        private const double NanosecondsPerSecond = 1_000_000_000d;

        /// <summary>
        /// Warms <paramref name="operation"/>, then measures it over <paramref name="batchCount"/> batches of
        /// <paramref name="iterationsPerBatch"/> invocations each.
        /// </summary>
        public static GuardCostMeasurement Measure(
            Action operation,
            int warmupIterations = DefaultWarmupIterations,
            int batchCount = DefaultBatchCount,
            int iterationsPerBatch = DefaultIterationsPerBatch)
        {
            if (operation is null)
            {
                throw new ArgumentNullException(nameof(operation));
            }

            if (warmupIterations < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(warmupIterations), warmupIterations, "Warm-up iterations cannot be negative.");
            }

            if (batchCount < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(batchCount), batchCount, "At least one measured batch is required.");
            }

            if (iterationsPerBatch < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(iterationsPerBatch), iterationsPerBatch, "At least one iteration per batch is required.");
            }

            RunIterations(operation, warmupIterations);

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            var nanosecondsPerOperation = new double[batchCount];
            var allocatedBytesPerBatch = new long[batchCount];

            for (var batch = 0; batch < batchCount; batch++)
            {
                var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
                var startedAt = Stopwatch.GetTimestamp();

                RunIterations(operation, iterationsPerBatch);

                var elapsedTicks = Stopwatch.GetTimestamp() - startedAt;
                allocatedBytesPerBatch[batch] = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
                nanosecondsPerOperation[batch] = ToNanoseconds(elapsedTicks) / iterationsPerBatch;
            }

            return new GuardCostMeasurement(
                MedianOf(nanosecondsPerOperation),
                MedianOf(allocatedBytesPerBatch),
                batchCount,
                iterationsPerBatch);
        }

        /// <summary>
        /// The value-returning form of <see cref="Measure(Action, int, int, int)"/>. The result of each
        /// invocation is stored so the call cannot be treated as dead code. Note that this overload measures
        /// one extra delegate invocation per iteration compared with the <see cref="Action"/> form.
        /// </summary>
        public static GuardCostMeasurement Measure<TResult>(
            Func<TResult> operation,
            int warmupIterations = DefaultWarmupIterations,
            int batchCount = DefaultBatchCount,
            int iterationsPerBatch = DefaultIterationsPerBatch)
        {
            if (operation is null)
            {
                throw new ArgumentNullException(nameof(operation));
            }

            var sink = default(TResult);

            // INVARIANT: the wrapper must be a statement-bodied Action held in an explicitly typed local. An
            // expression-bodied `() => sink = operation()` is value-producing, so overload resolution binds it
            // back to THIS generic overload and the call recurses until the stack overflows.
            Action invokeAndKeepResult = () => { sink = operation(); };

            return Measure(invokeAndKeepResult, warmupIterations, batchCount, iterationsPerBatch);
        }

        // Kept un-inlined so the measured loop shape is identical between the warm-up and measured phases.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void RunIterations(Action operation, int iterations)
        {
            for (var iteration = 0; iteration < iterations; iteration++)
            {
                operation();
            }
        }

        private static double ToNanoseconds(long stopwatchTicks)
            => stopwatchTicks * NanosecondsPerSecond / Stopwatch.Frequency;

        private static double MedianOf(double[] values)
        {
            var ordered = (double[])values.Clone();
            Array.Sort(ordered);

            var middle = ordered.Length / 2;

            return ordered.Length % 2 == 1
                ? ordered[middle]
                : (ordered[middle - 1] + ordered[middle]) / 2d;
        }

        private static long MedianOf(long[] values)
        {
            var ordered = (long[])values.Clone();
            Array.Sort(ordered);

            var middle = ordered.Length / 2;

            return ordered.Length % 2 == 1
                ? ordered[middle]
                : ordered[middle - 1] + ((ordered[middle] - ordered[middle - 1]) / 2);
        }
    }

    /// <summary>
    /// The outcome of a <see cref="GuardCostProbe"/> run: the median per-operation elapsed time and the
    /// median per-batch allocation delta, plus the batch shape that produced them.
    /// </summary>
    public readonly struct GuardCostMeasurement
    {
        public GuardCostMeasurement(double medianNanosecondsPerOperation, long medianAllocatedBytesPerBatch, int batchCount, int iterationsPerBatch)
        {
            MedianNanosecondsPerOperation = medianNanosecondsPerOperation;
            MedianAllocatedBytesPerBatch = medianAllocatedBytesPerBatch;
            BatchCount = batchCount;
            IterationsPerBatch = iterationsPerBatch;
        }

        /// <summary>Median across batches of the elapsed nanoseconds per single invocation.</summary>
        public double MedianNanosecondsPerOperation { get; }

        /// <summary>
        /// Median across batches of the calling thread's allocated-bytes delta for a whole batch. Zero means
        /// the operation allocated nothing over <see cref="IterationsPerBatch"/> invocations.
        /// </summary>
        public long MedianAllocatedBytesPerBatch { get; }

        public int BatchCount { get; }

        public int IterationsPerBatch { get; }

        public override string ToString()
            => $"{MedianNanosecondsPerOperation:F2} ns/op, {MedianAllocatedBytesPerBatch} bytes/batch " +
               $"({BatchCount} batch(es) of {IterationsPerBatch} iteration(s))";
    }
}
