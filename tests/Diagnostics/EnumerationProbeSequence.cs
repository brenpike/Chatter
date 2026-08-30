using System;
using System.Collections;
using System.Collections.Generic;

namespace Chatter.Testing.Core.Diagnostics
{
    /// <summary>
    /// A lazily yielded sequence that PERMITS re-enumeration and records every pass, so a test can assert
    /// exactly how many times a component walked the sequence it was handed.
    /// </summary>
    /// <remarks>
    /// This is the complement of the <c>SinglePassEventSequence</c> fixture in the Message Brokers test project,
    /// which THROWS from its second <c>GetEnumerator</c> call. That refusal pins single-pass consumption well, but
    /// it makes a component that walks a sequence TWICE structurally inexpressible: the second pass dies at the
    /// call rather than being observed. A dispatcher that counts a sequence for a capacity hint and then walks it
    /// again to send therefore cannot be described at all — even though that shape doubles per-message work such
    /// as body serialisation and telemetry counting. This type observes instead of refusing: it yields, records,
    /// and lets the test decide what the recorded shape means.
    ///
    /// Per-pass yield counts are kept separately from the total so a full second walk is distinguishable from a
    /// walk that was abandoned partway — <c>[3, 3]</c> and <c>[3, 1]</c> both total more than one pass, but only
    /// the first is a genuine double traversal.
    ///
    /// THREAD AFFINITY: recording is unsynchronised, matching the single-pass fixture. Enumerate from one thread
    /// at a time; concurrent enumerators would corrupt the recorded counts and timeline.
    /// </remarks>
    public sealed class EnumerationProbeSequence<TItem> : IEnumerable<TItem>
    {
        /// <summary>The timeline entry recorded each time an enumerator is asked for.</summary>
        public const string EnumeratorRequestedEntry = "sequence-enumerator-requested";

        /// <summary>The timeline entry prefix recorded per yielded item; the zero-based index within the pass is appended.</summary>
        public const string YieldedEntryPrefix = "sequence-yielded-";

        private readonly TItem[] _items;
        private readonly List<string> _pullTimeline;
        private readonly List<int> _yieldCountsPerPass = new List<int>();

        /// <param name="items">The items handed out, in order, on every pass.</param>
        public EnumerationProbeSequence(params TItem[] items)
            : this(items, new List<string>())
        {
        }

        /// <param name="items">The items handed out, in order, on every pass.</param>
        /// <param name="pullTimeline">
        /// A timeline the probe appends its enumerator-requested and yielded entries to. Pass a harness-owned list
        /// to interleave these entries with the harness's own, so a test can assert that a pass happened BEFORE or
        /// AFTER some other observable step rather than merely that it happened.
        /// </param>
        public EnumerationProbeSequence(IEnumerable<TItem> items, List<string> pullTimeline)
        {
            if (items is null)
            {
                throw new ArgumentNullException(nameof(items));
            }

            if (pullTimeline is null)
            {
                throw new ArgumentNullException(nameof(pullTimeline));
            }

            _items = new List<TItem>(items).ToArray();
            _pullTimeline = pullTimeline;
        }

        /// <summary>How many times an enumerator was asked for. One means nothing walked the sequence besides its intended consumer.</summary>
        public int EnumeratorRequestCount => _yieldCountsPerPass.Count;

        /// <summary>How many items were handed out across every pass.</summary>
        public int YieldedCount
        {
            get
            {
                var total = 0;

                for (var pass = 0; pass < _yieldCountsPerPass.Count; pass++)
                {
                    total += _yieldCountsPerPass[pass];
                }

                return total;
            }
        }

        /// <summary>
        /// How many items were handed out on each pass, in the order the passes were requested. A pass that
        /// requested an enumerator without pulling from it is recorded as zero.
        /// </summary>
        public IReadOnlyList<int> YieldCountsPerPass => _yieldCountsPerPass;

        /// <summary>The ordered entries this probe recorded; shares the list supplied at construction.</summary>
        public IReadOnlyList<string> PullTimeline => _pullTimeline;

        /// <summary>How many items a full pass hands out.</summary>
        public int ItemCount => _items.Length;

        public IEnumerator<TItem> GetEnumerator()
        {
            // INVARIANT: the request is recorded HERE rather than in the iterator body, so a caller that asks for
            // an enumerator and abandons it — a `Count()` over a non-collection sequence, a partial `Take` — is
            // still counted as a pass. An iterator-body recording would miss exactly the eager walks this fixture
            // exists to expose.
            _pullTimeline.Add(EnumeratorRequestedEntry);
            _yieldCountsPerPass.Add(0);

            return YieldItems(_yieldCountsPerPass.Count - 1);
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        private IEnumerator<TItem> YieldItems(int passIndex)
        {
            for (var index = 0; index < _items.Length; index++)
            {
                _pullTimeline.Add(YieldedEntryPrefix + index);
                _yieldCountsPerPass[passIndex] = index + 1;

                yield return _items[index];
            }
        }
    }
}
