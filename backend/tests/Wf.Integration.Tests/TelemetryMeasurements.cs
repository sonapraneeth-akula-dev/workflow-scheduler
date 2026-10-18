using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using Wf.Engine.Core.Metrics;

namespace Wf.Integration.Tests;

internal sealed class TelemetryMeasurements : IDisposable
{
    private readonly MeterListener listener = new();
    private readonly ConcurrentQueue<(string Name, double Value)> values = new();

    public TelemetryMeasurements(string workflow)
    {
        listener.InstrumentPublished = (instrument, owner) =>
        {
            if (instrument.Meter.Name == WorkflowTelemetry.MeterName) owner.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) => Record(instrument.Name, value, tags));
        listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) => Record(instrument.Name, value, tags));
        listener.Start();

        void Record(string name, double value, ReadOnlySpan<KeyValuePair<string, object?>> tags)
        {
            foreach (var tag in tags)
                if (tag.Key == "workflow" && tag.Value is string identity && identity == workflow)
                {
                    values.Enqueue((name, value));
                    return;
                }
        }
    }

    public double[] Values(string name) => values.Where(value => value.Name == name).Select(value => value.Value).ToArray();
    public void Dispose() => listener.Dispose();
}
