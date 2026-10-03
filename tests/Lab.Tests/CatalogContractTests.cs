using System.Text.Json;
using Shared.Contracts;
namespace Lab.Tests;

public sealed class CatalogContractTests
{
    [Fact]
    public void ProjectionEnvelopePreservesIdsCamelCaseAndIntegerMoneyBeyondDoublePrecision()
    {
        var payload = new TourSessionChanged(Guid.NewGuid(), "Sample", true, 3, 9007199254740993, "MXN");
        var source = new IntegrationEnvelope<TourSessionChanged>(Guid.NewGuid(), Guid.NewGuid(), "TourSessionChanged", 1,
            DateTimeOffset.UtcNow, null, Guid.NewGuid(), Guid.NewGuid(), null, payload);
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var json = JsonSerializer.Serialize(source, options); var parsed = JsonDocument.Parse(json);
        Assert.Equal(source.MessageId, parsed.RootElement.GetProperty("messageId").GetGuid());
        Assert.Equal(1, parsed.RootElement.GetProperty("version").GetInt32());
        Assert.Equal(payload.UnitAmountMinor, parsed.RootElement.GetProperty("payload").GetProperty("unitAmountMinor").GetInt64());
        Assert.Equal(source, JsonSerializer.Deserialize<IntegrationEnvelope<TourSessionChanged>>(json, options));
    }
}
