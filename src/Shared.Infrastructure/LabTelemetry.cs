using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Trace;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;

namespace Shared.Infrastructure;

public static class LabTelemetry
{
    public const string Name = "TourMicroservicesLab";
    public static readonly ActivitySource Activities = new(Name);
    private static readonly Meter Meter = new(Name);
    private static readonly Counter<long> Events = Meter.CreateCounter<long>("lab.events");
    private static readonly Histogram<double> Snapshots = Meter.CreateHistogram<double>("lab.snapshot");
    private static long duplicates;
    public static long IgnoredDuplicates => Interlocked.Read(ref duplicates);
    public static void Count(string kind, string outcome)
    {
        if (kind == "inbox" && outcome == "duplicate") Interlocked.Increment(ref duplicates);
        Events.Add(1, new KeyValuePair<string, object?>("kind", kind), new("outcome", outcome));
        Console.WriteLine(JsonSerializer.Serialize(new { service=AppDomain.CurrentDomain.FriendlyName, @event="metric.delta",kind,outcome,value=1,
            TraceId=Activity.Current?.TraceId.ToString(),SpanId=Activity.Current?.SpanId.ToString() }));
    }
    public static void Measure(string name, double value)
    {
        Snapshots.Record(value, new KeyValuePair<string, object?>("measurement", name));
        Console.WriteLine(JsonSerializer.Serialize(new {service=AppDomain.CurrentDomain.FriendlyName,@event="metric.snapshot",measurement=name,value}));
    }
    public static void AddLabTelemetry(this WebApplicationBuilder builder)
    {
        var service = builder.Environment.ApplicationName;
        var endpoint = builder.Configuration["Telemetry:Endpoint"];
        var sampling = builder.Configuration.GetValue("Telemetry:Sampling", 1.0);
        if (sampling is < 0 or > 1) throw new InvalidOperationException("Sampling must be between zero and one.");
        var otel = builder.Services.AddOpenTelemetry().ConfigureResource(r => r.AddService(service));
        otel.WithTracing(t =>
        {
            t.SetSampler(new ParentBasedSampler(new TraceIdRatioBasedSampler(sampling)))
                .AddSource(Name).AddAspNetCoreInstrumentation(o => o.RecordException = false)
                .AddHttpClientInstrumentation(o => o.RecordException = false)
                .AddProcessor(new SimpleActivityExportProcessor(new JsonSpanExporter(service)));
            if (!string.IsNullOrWhiteSpace(endpoint)) t.AddOtlpExporter(o => o.Endpoint = new Uri(endpoint));
        });
        otel.WithMetrics(m =>
        {
            m.AddMeter(Name).AddAspNetCoreInstrumentation().AddHttpClientInstrumentation();
            if (!string.IsNullOrWhiteSpace(endpoint)) m.AddOtlpExporter(o => o.Endpoint = new Uri(endpoint));
        });
        builder.Logging.Configure(o => o.ActivityTrackingOptions = ActivityTrackingOptions.TraceId | ActivityTrackingOptions.SpanId);
    }
    public static Dictionary<string, object?> MessageScope(Shared.Contracts.IntegrationEnvelope<JsonElement> envelope) => new()
    {
        ["event"] = envelope.Type, ["MessageId"] = envelope.MessageId, ["SagaId"] = envelope.SagaId,
        ["ReservationId"] = Id(envelope.Payload, "reservationId"),
        ["OperationId"] = Id(envelope.Payload, "refundOperationId") ?? Id(envelope.Payload, "paymentOperationId") ?? Id(envelope.Payload, "holdId"),
        ["TraceId"] = Activity.Current?.TraceId.ToString(), ["SpanId"] = Activity.Current?.SpanId.ToString()
    };
    private static Guid? Id(JsonElement payload, string field) => payload.TryGetProperty(field, out var p) && p.ValueKind == JsonValueKind.String && p.TryGetGuid(out var id) ? id : null;
}

// Export only an allowlist. URLs/query strings, headers, payloads and exception messages are never exported.
public sealed class JsonSpanExporter(string service) : BaseExporter<Activity>
{
    public override ExportResult Export(in Batch<Activity> batch)
    {
        foreach (var span in batch)
            Console.WriteLine(JsonSerializer.Serialize(new { service, @event = "span", TraceId = span.TraceId.ToString(), SpanId = span.SpanId.ToString(),
                ParentSpanId = span.ParentSpanId.ToString(), operation = span.OperationName, durationMs = span.Duration.TotalMilliseconds,
                identifiers = span.TagObjects.Where(p => p.Key is "MessageId" or "SagaId" or "ReservationId" or "OperationId").ToDictionary(p => p.Key, p => p.Value) }));
        return ExportResult.Success;
    }
}
