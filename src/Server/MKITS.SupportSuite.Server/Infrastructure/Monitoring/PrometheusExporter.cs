// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) MK-ITSolutions and contributors

using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Globalization;
using System.Text;

namespace MKITS.SupportSuite.Server.Infrastructure.Monitoring;

/// <summary>
/// A small Prometheus text-format exporter (no extra dependency): listens to the server's own meter and the .NET
/// runtime meter, sums counters, keeps the last value of observable instruments, and renders them on scrape.
/// Histograms are not exported.
/// </summary>
public sealed class PrometheusExporter : IDisposable
{
    private static readonly string[] Meters = [MkitsMetrics.MeterName, "System.Runtime"];

    private readonly MeterListener _listener = new();
    private readonly ConcurrentDictionary<(Instrument Instrument, string Labels), double> _values = new();
    private readonly Lock _collectGate = new();

    public PrometheusExporter()
    {
        _listener.InstrumentPublished = (instrument, listener) =>
        {
            if (Meters.Contains(instrument.Meter.Name, StringComparer.Ordinal) && Kind(instrument) is not null)
            {
                listener.EnableMeasurementEvents(instrument);
            }
        };
        _listener.MeasurementsCompleted = (instrument, _) =>
        {
            foreach (var key in _values.Keys.Where(k => ReferenceEquals(k.Instrument, instrument)).ToList())
            {
                _values.TryRemove(key, out _); // its meter was disposed
            }
        };
        _listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) => Record(instrument, value, tags));
        _listener.SetMeasurementEventCallback<int>((instrument, value, tags, _) => Record(instrument, value, tags));
        _listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) => Record(instrument, value, tags));
        _listener.Start();
    }

    /// <summary>The current values in the Prometheus text exposition format 0.0.4.</summary>
    public string Render()
    {
        lock (_collectGate)
        {
            _listener.RecordObservableInstruments();
        }

        // Grouped by exported name: instruments of the same name (several meters with one name) are merged.
        var text = new StringBuilder();
        var metrics = _values
            .Select(v => (Instrument: v.Key.Instrument, Type: Kind(v.Key.Instrument)!, v.Key.Labels, v.Value))
            .GroupBy(v => MetricName(v.Instrument, v.Type), StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal);
        foreach (var metric in metrics)
        {
            var first = metric.First();
            if (!string.IsNullOrEmpty(first.Instrument.Description))
            {
                text.Append("# HELP ").Append(metric.Key).Append(' ').Append(first.Instrument.Description.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal)).Append('\n');
            }

            text.Append("# TYPE ").Append(metric.Key).Append(' ').Append(first.Type).Append('\n');
            foreach (var series in metric.GroupBy(v => v.Labels, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal))
            {
                text.Append(metric.Key).Append(series.Key).Append(' ').Append(series.Sum(v => v.Value).ToString("R", CultureInfo.InvariantCulture)).Append('\n');
            }
        }

        return text.ToString();
    }

    public void Dispose() => _listener.Dispose();

    /// <summary><c>mkits.relay.bytes</c> (counter) → <c>mkits_relay_bytes_total</c>.</summary>
    internal static string MetricName(Instrument instrument, string type)
    {
        var name = new StringBuilder(instrument.Name.Length + 6);
        foreach (var c in instrument.Name)
        {
            name.Append(char.IsAsciiLetterOrDigit(c) ? c : '_');
        }

        if (name.Length == 0 || char.IsAsciiDigit(name[0]))
        {
            name.Insert(0, '_');
        }

        return type == "counter" ? name.Append("_total").ToString() : name.ToString();
    }

    internal static string Labels(ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        if (tags.IsEmpty)
        {
            return string.Empty;
        }

        var pairs = new List<string>(tags.Length);
        foreach (var (key, value) in tags)
        {
            var label = new string(key.Select(c => char.IsAsciiLetterOrDigit(c) ? c : '_').ToArray());
            var text = Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
            text = text.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal);
            pairs.Add($"{label}=\"{text}\"");
        }

        pairs.Sort(StringComparer.Ordinal);
        return "{" + string.Join(',', pairs) + "}";
    }

    /// <summary>Prometheus type, or <c>null</c> for instruments that are not exported (histograms).</summary>
    private static string? Kind(Instrument instrument)
    {
        var type = instrument.GetType().IsGenericType ? instrument.GetType().GetGenericTypeDefinition() : null;
        return type == typeof(Counter<>) || type == typeof(ObservableCounter<>) ? "counter"
            : type == typeof(UpDownCounter<>) || type == typeof(ObservableUpDownCounter<>) || type == typeof(ObservableGauge<>) || type == typeof(Gauge<>) ? "gauge"
            : null;
    }

    private void Record(Instrument instrument, double value, ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        var key = (instrument, Labels(tags));
        if (instrument.IsObservable || instrument.GetType().GetGenericTypeDefinition() == typeof(Gauge<>))
        {
            _values[key] = value; // observable instruments report their current (cumulative) value
        }
        else
        {
            _values.AddOrUpdate(key, value, (_, current) => current + value);
        }
    }
}
