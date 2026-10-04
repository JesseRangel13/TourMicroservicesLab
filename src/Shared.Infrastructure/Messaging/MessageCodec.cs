using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Shared.Contracts;
namespace Shared.Infrastructure.Messaging;

public sealed class PoisonMessageException(string code) : Exception(code);
public static class MessageCodec
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { RespectRequiredConstructorParameters = true };
    public static IntegrationEnvelope<JsonElement> Parse(string body)
    {
        try
        {
            var envelope = JsonSerializer.Deserialize<IntegrationEnvelope<JsonElement>>(body, Json) ?? throw new PoisonMessageException("EmptyEnvelope");
            if (envelope.MessageId == Guid.Empty || envelope.DeliveryId == Guid.Empty || envelope.CorrelationId == Guid.Empty
                || envelope.CausationId == Guid.Empty || envelope.Version != 1 || string.IsNullOrWhiteSpace(envelope.Type)
                || envelope.OccurredAtUtc == default || envelope.OccurredAtUtc.Offset != TimeSpan.Zero
                || envelope.Payload.ValueKind != JsonValueKind.Object || (envelope.Type != "TourSessionChanged" && (envelope.SagaId is null || envelope.SagaId == Guid.Empty)))
                throw new PoisonMessageException("InvalidEnvelope");
            if (envelope.Traceparent is not null && !ActivityContext.TryParse(envelope.Traceparent, null, out _)) throw new PoisonMessageException("InvalidTraceparent");
            return envelope;
        }
        catch (JsonException) { throw new PoisonMessageException("InvalidJson"); }
    }
    public static T Payload<T>(IntegrationEnvelope<JsonElement> envelope)
    {
        if (envelope.Type != typeof(T).Name) throw new PoisonMessageException("WrongMessageType");
        try { return envelope.Payload.Deserialize<T>(Json) ?? throw new PoisonMessageException("EmptyPayload"); }
        catch (JsonException) { throw new PoisonMessageException("InvalidPayload"); }
    }
    public static string Fingerprint(IntegrationEnvelope<JsonElement> envelope)
    {
        var normalized = JsonSerializer.SerializeToElement(envelope with { DeliveryId = Guid.Empty, Traceparent = null, Tracestate = null }, Json);
        using var stream = new MemoryStream(); using (var writer = new Utf8JsonWriter(stream)) WriteCanonical(writer, normalized);
        return Convert.ToHexString(SHA256.HashData(stream.ToArray()));
    }
    private static void WriteCanonical(Utf8JsonWriter writer, JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        { writer.WriteStartObject(); foreach (var p in value.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal)) { writer.WritePropertyName(p.Name); WriteCanonical(writer, p.Value); } writer.WriteEndObject(); }
        else if (value.ValueKind == JsonValueKind.Array) { writer.WriteStartArray(); foreach (var item in value.EnumerateArray()) WriteCanonical(writer, item); writer.WriteEndArray(); }
        else value.WriteTo(writer);
    }
}
public static class MessageRoutes
{
    public static string[] Destinations(string type) => type switch
    {
        "HoldSeats" or "ConfirmSeats" or "ReleaseSeats" => ["tourlab-catalog"],
        "SeatsHeld" or "SeatsHoldRejected" or "SeatsConfirmed" or "SeatsConfirmationRejected" or "SeatsHoldExpired" or "SeatsReleased" or "TourSessionChanged" => ["tourlab-reservations"],
        "ProcessPayment" or "ReconcilePayment" or "RefundPayment" => ["tourlab-payments"],
        "PaymentSucceeded" or "PaymentDeclined" or "PaymentOutcomeUnknown" or "PaymentRefunded" or "RefundNeedsReview" => ["tourlab-reservations"],
        "ReservationConfirmed" or "ReservationFailed" or "ReservationCancelled" => ["tourlab-notifications"],
        _ => throw new PoisonMessageException("UnknownRoute")
    };
    public static string Serialize<T>(T payload, Guid messageId, Guid deliveryId, DateTimeOffset now, Guid? sagaId, Guid correlationId, Guid causationId) =>
        JsonSerializer.Serialize(new IntegrationEnvelope<T>(messageId, deliveryId, typeof(T).Name, 1, now, sagaId, correlationId, causationId, Activity.Current?.Id, payload) { Tracestate = Activity.Current?.TraceStateString }, MessageCodec.Json);
}
