using System.Text.Json;
using Shared.Contracts;
using Shared.Infrastructure.Messaging;
namespace Lab.Tests;

public sealed class MessagingContractTests
{
    private static string Body() => MessageRoutes.Serialize(new HoldSeats(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "owner"),
        Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UtcNow, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
    [Fact]
    public void FingerprintIgnoresDeliveryIdentityButDetectsChangedBusinessContent()
    {
        var original = MessageCodec.Parse(Body());
        Assert.Equal(MessageCodec.Fingerprint(original), MessageCodec.Fingerprint(original with { DeliveryId = Guid.NewGuid() }));
        var changed = original with { Payload = JsonSerializer.SerializeToElement(new HoldSeats(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "other"), MessageCodec.Json) };
        Assert.NotEqual(MessageCodec.Fingerprint(original), MessageCodec.Fingerprint(changed));
    }
    [Theory]
    [InlineData("{}")]
    [InlineData("not json")]
    public void MissingOrMalformedEnvelopeIsPoison(string body) => Assert.Throws<PoisonMessageException>(() => MessageCodec.Parse(body));
    [Fact]
    public void FutureVersionIsPoison()
    {
        var envelope = MessageCodec.Parse(Body());
        Assert.Throws<PoisonMessageException>(() => MessageCodec.Parse(JsonSerializer.Serialize(envelope with { Version = 2 }, MessageCodec.Json)));
    }
}
