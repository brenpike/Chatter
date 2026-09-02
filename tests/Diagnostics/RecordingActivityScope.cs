using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Chatter.Testing.Core.Diagnostics
{
    // Process-global diagnostics state (Activity.Current, the set of attached .NET ActivityListener /
    // MeterListener instances) leaks across tests unless every scope is disposed deterministically, so
    // every type in this file is a `using`-scoped IDisposable that restores what it changed.
    //
    // Deliberately NOT here: an xunit [CollectionDefinition] serializing the diagnostics tests. xunit v2
    // discovers collection definitions only in the TEST ASSEMBLY UNDER RUN, so a definition placed in this
    // shared support library would silently never be discovered and the tests would still run in parallel.
    // Each module's own *.Tests project declares its own definition. Do not "helpfully" move one here.

    /// <summary>
    /// Attaches a .NET <see cref="ActivityListener"/> to the named <see cref="ActivitySource"/>s and records
    /// every <see cref="Activity"/> started and stopped while the scope is open. Sampling is forced to
    /// <see cref="ActivitySamplingResult.AllDataAndRecorded"/> so activities are actually created and report
    /// <see cref="Activity.IsAllDataRequested"/> as true.
    /// </summary>
    /// <remarks>
    /// Scopes are safe to nest: an <see cref="ActivitySource"/> supports any number of attached .NET
    /// listeners, and each scope detaches only its own. <see cref="Activity.Current"/> is captured at
    /// construction and restored on <see cref="Dispose"/> so one test cannot leak ambient state into the next.
    /// </remarks>
    public sealed class RecordingActivityScope : IDisposable
    {
        private readonly HashSet<string> _sourceNames;
        private readonly ActivityListener _netActivityListener;
        private readonly List<Activity> _startedActivities = new List<Activity>();
        private readonly List<Activity> _stoppedActivities = new List<Activity>();
        private readonly object _sync = new object();
        private readonly Activity _priorActivity;
        private bool _disposed;

        /// <param name="sourceNames">
        /// The <see cref="ActivitySource.Name"/>s to listen to. Names are matched exactly; passing none
        /// records nothing.
        /// </param>
        public RecordingActivityScope(params string[] sourceNames)
        {
            if (sourceNames is null)
            {
                throw new ArgumentNullException(nameof(sourceNames));
            }

            _sourceNames = new HashSet<string>(sourceNames, StringComparer.Ordinal);
            _priorActivity = Activity.Current;

            _netActivityListener = new ActivityListener
            {
                ShouldListenTo = ListensTo,
                Sample = SampleAllData,
                SampleUsingParentId = SampleAllDataFromParentId,
                ActivityStarted = RecordStarted,
                ActivityStopped = RecordStopped,
            };

            ActivitySource.AddActivityListener(_netActivityListener);
        }

        /// <summary>Every <see cref="Activity"/> started on the listened-to sources, in start order.</summary>
        public IReadOnlyList<Activity> StartedActivities
        {
            get
            {
                lock (_sync)
                {
                    return _startedActivities.ToArray();
                }
            }
        }

        /// <summary>Every <see cref="Activity"/> stopped on the listened-to sources, in stop order.</summary>
        public IReadOnlyList<Activity> StoppedActivities
        {
            get
            {
                lock (_sync)
                {
                    return _stoppedActivities.ToArray();
                }
            }
        }

        /// <summary>The stopped activities whose <see cref="Activity.OperationName"/> matches exactly.</summary>
        public IReadOnlyList<Activity> StoppedNamed(string operationName)
        {
            lock (_sync)
            {
                return _stoppedActivities.FindAll(activity => activity.OperationName == operationName);
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _netActivityListener.Dispose();
            Activity.Current = _priorActivity;
        }

        private bool ListensTo(ActivitySource source) => _sourceNames.Contains(source.Name);

        private static ActivitySamplingResult SampleAllData(ref ActivityCreationOptions<ActivityContext> options)
            => ActivitySamplingResult.AllDataAndRecorded;

        private static ActivitySamplingResult SampleAllDataFromParentId(ref ActivityCreationOptions<string> options)
            => ActivitySamplingResult.AllDataAndRecorded;

        private void RecordStarted(Activity activity)
        {
            lock (_sync)
            {
                _startedActivities.Add(activity);
            }
        }

        private void RecordStopped(Activity activity)
        {
            lock (_sync)
            {
                _stoppedActivities.Add(activity);
            }
        }
    }

    /// <summary>
    /// Attaches a .NET <see cref="MeterListener"/> to the named <see cref="Meter"/>s and records every
    /// measurement published while the scope is open. Safe to nest; a <see cref="Meter"/> supports any number
    /// of attached .NET listeners and each scope detaches only its own.
    /// </summary>
    /// <remarks>
    /// <see cref="MeterListener.Dispose"/> alone does NOT unsubscribe the listener from the instruments it
    /// enabled via <see cref="MeterListener.EnableMeasurementEvents(Instrument, object)"/> — the instrument
    /// stays <see cref="Instrument.Enabled"/> and keeps delivering measurements to this scope after disposal.
    /// This type therefore records every instrument it enables and calls
    /// <see cref="MeterListener.DisableMeasurementEvents(Instrument)"/> for each one before disposing the
    /// underlying .NET <see cref="MeterListener"/>.
    /// </remarks>
    public sealed class RecordingMeterScope : IDisposable
    {
        private readonly HashSet<string> _meterNames;
        private readonly MeterListener _netMeterListener;
        private readonly List<RecordedMeasurement> _measurements = new List<RecordedMeasurement>();
        private readonly List<Instrument> _enabledInstruments = new List<Instrument>();
        private readonly List<Instrument> _measuredInstruments = new List<Instrument>();
        private readonly object _sync = new object();
        private bool _disposed;

        /// <param name="meterNames">
        /// The <see cref="Meter.Name"/>s to listen to. Names are matched exactly; passing none records nothing.
        /// </param>
        public RecordingMeterScope(params string[] meterNames)
        {
            if (meterNames is null)
            {
                throw new ArgumentNullException(nameof(meterNames));
            }

            _meterNames = new HashSet<string>(meterNames, StringComparer.Ordinal);

            _netMeterListener = new MeterListener
            {
                InstrumentPublished = EnableWhenMeterMatches,
            };

            _netMeterListener.SetMeasurementEventCallback<int>(RecordInt);
            _netMeterListener.SetMeasurementEventCallback<long>(RecordLong);
            _netMeterListener.SetMeasurementEventCallback<double>(RecordDouble);
            _netMeterListener.Start();
        }

        /// <summary>Every measurement published by the listened-to meters, in publication order.</summary>
        public IReadOnlyList<RecordedMeasurement> Measurements
        {
            get
            {
                lock (_sync)
                {
                    return _measurements.ToArray();
                }
            }
        }

        /// <summary>The recorded measurements whose <see cref="Instrument.Name"/> matches exactly.</summary>
        public IReadOnlyList<RecordedMeasurement> MeasurementsFor(string instrumentName)
        {
            lock (_sync)
            {
                return _measurements.FindAll(measurement => measurement.InstrumentName == instrumentName);
            }
        }

        /// <summary>
        /// The <see cref="Instrument"/> this scope observed under <paramref name="instrumentName"/>, so a test can
        /// assert on the published instrument itself rather than only on its measurements.
        /// </summary>
        /// <remarks>
        /// An instrument published BEFORE this scope opened may never reach
        /// <see cref="MeterListener.InstrumentPublished"/>, so an instrument handed to a measurement callback is
        /// resolvable here too: drive one real operation, then look the instrument up by name.
        /// </remarks>
        public bool TryGetInstrument(string instrumentName, out Instrument instrument)
        {
            lock (_sync)
            {
                instrument = _enabledInstruments.Find(candidate => candidate.Name == instrumentName)
                    ?? _measuredInstruments.Find(candidate => candidate.Name == instrumentName);
            }

            return instrument != null;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            Instrument[] enabledInstruments;
            lock (_sync)
            {
                enabledInstruments = _enabledInstruments.ToArray();
            }

            foreach (var instrument in enabledInstruments)
            {
                _netMeterListener.DisableMeasurementEvents(instrument);
            }

            _netMeterListener.Dispose();
        }

        private void EnableWhenMeterMatches(Instrument instrument, MeterListener netMeterListener)
        {
            if (_meterNames.Contains(instrument.Meter.Name))
            {
                netMeterListener.EnableMeasurementEvents(instrument);

                lock (_sync)
                {
                    _enabledInstruments.Add(instrument);
                }
            }
        }

        private void RecordInt(Instrument instrument, int measurement, ReadOnlySpan<KeyValuePair<string, object>> tags, object state)
            => Record(instrument, measurement, tags);

        private void RecordLong(Instrument instrument, long measurement, ReadOnlySpan<KeyValuePair<string, object>> tags, object state)
            => Record(instrument, measurement, tags);

        private void RecordDouble(Instrument instrument, double measurement, ReadOnlySpan<KeyValuePair<string, object>> tags, object state)
            => Record(instrument, measurement, tags);

        private void Record(Instrument instrument, double value, ReadOnlySpan<KeyValuePair<string, object>> tags)
        {
            var capturedTags = new KeyValuePair<string, object>[tags.Length];
            tags.CopyTo(capturedTags);

            var recorded = new RecordedMeasurement(instrument.Meter.Name, instrument.Name, value, capturedTags);

            lock (_sync)
            {
                _measurements.Add(recorded);

                if (!_measuredInstruments.Contains(instrument))
                {
                    _measuredInstruments.Add(instrument);
                }
            }
        }
    }

    /// <summary>
    /// One measurement captured by a <see cref="RecordingMeterScope"/>. Values of every supported instrument
    /// generic argument are widened to <see cref="double"/> so assertions need only one shape.
    /// </summary>
    public sealed class RecordedMeasurement
    {
        public RecordedMeasurement(string meterName, string instrumentName, double value, IReadOnlyList<KeyValuePair<string, object>> tags)
        {
            MeterName = meterName;
            InstrumentName = instrumentName;
            Value = value;
            Tags = tags;
        }

        public string MeterName { get; }

        public string InstrumentName { get; }

        public double Value { get; }

        public IReadOnlyList<KeyValuePair<string, object>> Tags { get; }

        public bool TryGetTag(string tagName, out object tagValue)
        {
            for (var index = 0; index < Tags.Count; index++)
            {
                if (Tags[index].Key == tagName)
                {
                    tagValue = Tags[index].Value;
                    return true;
                }
            }

            tagValue = null;
            return false;
        }

        public override string ToString() => $"{MeterName}/{InstrumentName}={Value} ({Tags.Count} tag(s))";
    }

    /// <summary>
    /// Attaches a .NET <see cref="ActivityListener"/> that listens to the named <see cref="ActivitySource"/> but
    /// samples every activity OUT, so <see cref="ActivitySource.HasListeners"/> is true while
    /// <see cref="ActivitySource.StartActivity"/> returns <c>null</c>.
    /// </summary>
    /// <remarks>
    /// This is the head-sampling condition of ADR-0010 D9: Chatter tracing IS opted into, so every guard keyed on
    /// <see cref="ActivitySource.HasListeners"/> passes, yet no <see cref="Activity"/> exists. It is the shape in
    /// which the propagation fallback and the ambient-activity handling must still behave. The prior
    /// <see cref="Activity.Current"/> is captured at construction and restored on <see cref="Dispose"/>.
    /// </remarks>
    public sealed class SampledOutActivityScope : IDisposable
    {
        private readonly ActivityListener _netActivityListener;
        private readonly List<Activity> _startedActivities = new List<Activity>();
        private readonly Activity _priorActivity;
        private bool _disposed;

        public SampledOutActivityScope(string sourceName)
        {
            if (string.IsNullOrWhiteSpace(sourceName))
            {
                throw new ArgumentException("A source name is required.", nameof(sourceName));
            }

            _priorActivity = Activity.Current;

            _netActivityListener = new ActivityListener
            {
                ShouldListenTo = source => source.Name == sourceName,
                Sample = SampleNone,
                SampleUsingParentId = SampleNoneFromParentId,
                ActivityStarted = _startedActivities.Add,
            };

            ActivitySource.AddActivityListener(_netActivityListener);
        }

        /// <summary>Every activity that was nonetheless started; empty is the point of this scope.</summary>
        public IReadOnlyList<Activity> StartedActivities => _startedActivities.ToArray();

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _netActivityListener.Dispose();
            Activity.Current = _priorActivity;
        }

        private static ActivitySamplingResult SampleNone(ref ActivityCreationOptions<ActivityContext> options)
            => ActivitySamplingResult.None;

        private static ActivitySamplingResult SampleNoneFromParentId(ref ActivityCreationOptions<string> options)
            => ActivitySamplingResult.None;
    }

    /// <summary>
    /// Establishes the "foreign instrumentation" condition: an <see cref="ActivitySource"/> whose name is
    /// unrelated to Chatter is listened to by its own .NET <see cref="ActivityListener"/> and an
    /// <see cref="Activity"/> is started on it, so <see cref="Activity.Current"/> is NON-NULL for the life of
    /// the scope while no Chatter source has a listener.
    /// </summary>
    /// <remarks>
    /// This is the scenario that proves guarding telemetry on <c>Activity.Current is null</c> would be wrong:
    /// an unrelated library's tracing makes <see cref="Activity.Current"/> non-null, so the off-state guard
    /// must key on Chatter's OWN <see cref="ActivitySource.HasListeners"/> instead. The prior
    /// <see cref="Activity.Current"/> is captured at construction and restored on <see cref="Dispose"/>.
    /// </remarks>
    public sealed class ForeignInstrumentationScope : IDisposable
    {
        /// <summary>An <see cref="ActivitySource"/> name deliberately unrelated to any Chatter source.</summary>
        public const string DefaultForeignSourceName = "Contoso.Unrelated.Instrumentation";

        private readonly ActivitySource _foreignSource;
        private readonly ActivityListener _netActivityListener;
        private readonly Activity _priorActivity;
        private bool _disposed;

        public ForeignInstrumentationScope(string foreignSourceName = DefaultForeignSourceName, string foreignOperationName = "foreign.work")
        {
            if (string.IsNullOrWhiteSpace(foreignSourceName))
            {
                throw new ArgumentException("A foreign source name is required.", nameof(foreignSourceName));
            }

            _priorActivity = Activity.Current;
            _foreignSource = new ActivitySource(foreignSourceName);

            _netActivityListener = new ActivityListener
            {
                ShouldListenTo = source => source.Name == foreignSourceName,
                Sample = SampleAllData,
                SampleUsingParentId = SampleAllDataFromParentId,
            };

            ActivitySource.AddActivityListener(_netActivityListener);

            ForeignActivity = _foreignSource.StartActivity(foreignOperationName);

            if (ForeignActivity is null)
            {
                Dispose();
                throw new InvalidOperationException(
                    $"The foreign ActivitySource '{foreignSourceName}' did not produce an Activity, so Activity.Current cannot be made non-null.");
            }
        }

        /// <summary>The started foreign <see cref="Activity"/>; equal to <see cref="Activity.Current"/> on entry.</summary>
        public Activity ForeignActivity { get; }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            ForeignActivity?.Stop();
            _netActivityListener.Dispose();
            _foreignSource.Dispose();
            Activity.Current = _priorActivity;
        }

        private static ActivitySamplingResult SampleAllData(ref ActivityCreationOptions<ActivityContext> options)
            => ActivitySamplingResult.AllDataAndRecorded;

        private static ActivitySamplingResult SampleAllDataFromParentId(ref ActivityCreationOptions<string> options)
            => ActivitySamplingResult.AllDataAndRecorded;
    }
}
