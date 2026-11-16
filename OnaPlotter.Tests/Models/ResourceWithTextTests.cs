using OnaPlotter.Models;

namespace OnaPlotter.Tests.Models;

/// <summary>
/// Pins the rename clones the Map page uses for its optimistic update
/// after an edit: every field other than the text carries over, so the
/// redraw keeps a MOB waypoint's icon and alarm correlation.
/// </summary>
public class ResourceWithTextTests
{
    [Test]
    public async Task Waypoint_WithText_KeepsPositionFeatureAndMobFields()
    {
        var feature = new GeoJsonFeature();
        var created = new DateTime(2026, 5, 1, 10, 0, 0, DateTimeKind.Utc);
        var wp = new SignalkWaypoint
        {
            Id = "w1", Name = "Old", Description = "old text", Feature = feature,
            Latitude = 54.1, Longitude = 10.2, CreatedAt = created,
            IsMob = true, IsMobActive = true, MobAlarmId = "mob-1",
        };

        var copy = wp.WithText("New", "new text");

        await Assert.That(copy).IsNotSameReferenceAs(wp);
        await Assert.That(copy.Name).IsEqualTo("New");
        await Assert.That(copy.Description).IsEqualTo("new text");
        await Assert.That(copy.Id).IsEqualTo("w1");
        await Assert.That(copy.Latitude).IsEqualTo(54.1);
        await Assert.That(copy.Longitude).IsEqualTo(10.2);
        await Assert.That(copy.CreatedAt).IsEqualTo(created);
        await Assert.That(copy.Feature).IsSameReferenceAs(feature);
        await Assert.That(copy.IsMob).IsTrue();
        await Assert.That(copy.IsMobActive).IsTrue();
        await Assert.That(copy.MobAlarmId).IsEqualTo("mob-1");
        // The original is untouched (the page swaps the list entry).
        await Assert.That(wp.Name).IsEqualTo("Old");
    }

    [Test]
    public async Task Note_WithText_KeepsPositionAndMetadata()
    {
        var pos = new NotePosition { Latitude = 54.1, Longitude = 10.2 };
        var note = new SignalkNote
        {
            Id = "n1", Title = "Old", Description = "d", Position = pos,
            MimeType = "text/plain", Url = "https://example.invalid/x",
            CreatedAt = new DateTime(2026, 5, 1, 10, 0, 0, DateTimeKind.Utc),
        };

        var copy = note.WithText("New", "d2");

        await Assert.That(copy.Title).IsEqualTo("New");
        await Assert.That(copy.Description).IsEqualTo("d2");
        await Assert.That(copy.Position).IsSameReferenceAs(pos);
        await Assert.That(copy.MimeType).IsEqualTo("text/plain");
        await Assert.That(copy.Url).IsEqualTo("https://example.invalid/x");
        await Assert.That(copy.CreatedAt).IsEqualTo(note.CreatedAt);
        await Assert.That(note.Title).IsEqualTo("Old");
    }
}
