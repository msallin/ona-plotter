using System.Text.Json;
using OnaPlotter.Services.Api;

namespace OnaPlotter.Tests.Services.Mob;

/// <summary>Pin the deserialiser against the actual JSON shape signalk-server
/// returns from GET /signalk/v2/api/notifications. Helm pasted live data
/// twice in a row showing the MOB + position + createdAt arriving on the
/// wire but the chart marker still missing - this test reproduces that
/// exact response and asserts every field round-trips correctly.</summary>
public class MobNotificationDeserializationTest
{
    private static readonly JsonSerializerOptions s_options = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    [Test]
    public async Task LiveSignalkResponse_DeserializesMobWithPositionAndCreatedAt()
    {
        const string json = """
        {
          "d5c9bae3-b8cd-4aad-8f01-d60f82eac174": {
            "context": "vessels.urn:mrn:imo:mmsi:261006533",
            "path": "notifications.buddy.urn:mrn:imo:mmsi:368194520",
            "value": {
              "state": "alert",
              "method": [],
              "message": "Your buddy SPISEA is near",
              "id": "d5c9bae3-b8cd-4aad-8f01-d60f82eac174",
              "status": {
                "silenced": false,
                "acknowledged": true,
                "canSilence": true,
                "canAcknowledge": true,
                "canClear": false
              }
            }
          },
          "2ece6243-45ca-4381-89a8-e6f6308686eb": {
            "context": "",
            "path": "notifications.mob.2ece6243-45ca-4381-89a8-e6f6308686eb",
            "value": {
              "state": "emergency",
              "method": ["visual", "sound"],
              "message": "Person Overboard!",
              "status": {
                "silenced": false,
                "acknowledged": false,
                "canSilence": false,
                "canAcknowledge": true,
                "canClear": true
              },
              "position": {
                "latitude": 25.536005,
                "longitude": -76.76157666666667
              },
              "createdAt": "2026-05-03T23:13:16.126Z",
              "id": "2ece6243-45ca-4381-89a8-e6f6308686eb"
            }
          }
        }
        """;

        var dict = JsonSerializer.Deserialize<Dictionary<string, ServerNotificationEnvelope>>(json, s_options);

        await Assert.That(dict).IsNotNull();
        await Assert.That(dict!.Count).IsEqualTo(2);

        var mob = dict["2ece6243-45ca-4381-89a8-e6f6308686eb"];
        await Assert.That(mob).IsNotNull();
        await Assert.That(mob.Path).IsEqualTo("notifications.mob.2ece6243-45ca-4381-89a8-e6f6308686eb");

        var dto = mob.Value;
        await Assert.That(dto).IsNotNull();
        await Assert.That(dto!.State).IsEqualTo("emergency");
        await Assert.That(dto.Id).IsEqualTo("2ece6243-45ca-4381-89a8-e6f6308686eb");
        await Assert.That(dto.Message).IsEqualTo("Person Overboard!");
        await Assert.That(dto.Position).IsNotNull();
        await Assert.That(dto.Position!.Latitude).IsEqualTo(25.536005);
        await Assert.That(dto.Position.Longitude).IsEqualTo(-76.76157666666667);
        await Assert.That(dto.CreatedAt).IsNotNull();
    }
}
