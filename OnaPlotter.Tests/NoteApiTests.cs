using System.Net;
using OnaPlotter.Services.Api;

namespace OnaPlotter.Tests;

public class NoteApiTests
{
    [Test]
    public async Task Create_EmitsBarePositionObject()
    {
        // Pins the SignalK Note resource contract. Unlike routes and
        // waypoints, notes use a bare {title, description, position:
        // {latitude, longitude}} shape with NO Feature wrapper. This
        // is the panaaj/sk-types / Freeboard-SK spec; adding a
        // Feature object would make notes disappear in freeboard.
        string? capturedBody = null;
        string? capturedUrl = null;
        var http = ApiTestHelpers.MockClient(req =>
        {
            capturedUrl = req.RequestUri?.AbsoluteUri;
            capturedBody = req.Content?.ReadAsStringAsync().Result;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("\"note-new-42\"")
            };
        });
        var api = new NoteApi(http, ApiTestHelpers.FixedBaseUrl());

        var r = await api.CreateAsync("Kelp patch", "Watch the prop here", 47.4, 8.5);

        await Assert.That(r.Success).IsTrue();
        await Assert.That(r.Value).IsEqualTo("note-new-42");
        await Assert.That(capturedUrl).EndsWith("/resources/notes");
        await Assert.That(capturedBody).IsNotNull();
        await Assert.That(capturedBody).Contains("\"title\":\"Kelp patch\"");
        await Assert.That(capturedBody).Contains("\"description\":\"Watch the prop here\"");
        await Assert.That(capturedBody).Contains("\"position\"");
        await Assert.That(capturedBody).Contains("\"latitude\":47.4");
        await Assert.That(capturedBody).Contains("\"longitude\":8.5");
        // Guard against accidentally re-introducing the Feature wrapper.
        await Assert.That(capturedBody!.Contains("\"feature\"")).IsFalse();
        await Assert.That(capturedBody.Contains("\"geometry\"")).IsFalse();
    }

    [Test]
    public async Task GetAll_ParsesPositionAndKeyAsId()
    {
        var body = """
        {
            "abc-1": { "title": "Kelp", "description": "seaweed here",
                       "position": { "latitude": 47.4, "longitude": 8.5 } },
            "abc-2": { "title": "No position" }
        }
        """;
        var http = ApiTestHelpers.JsonClient(_ => body);
        var api = new NoteApi(http, ApiTestHelpers.FixedBaseUrl());

        var notes = await api.GetAllAsync();

        // Notes without a renderable position are dropped so the map
        // layer doesn't have to special-case them.
        await Assert.That(notes.Count).IsEqualTo(1);
        await Assert.That(notes[0].Id).IsEqualTo("abc-1");
        await Assert.That(notes[0].Title).IsEqualTo("Kelp");
        await Assert.That(notes[0].Description).IsEqualTo("seaweed here");
        var pos = notes[0].Position;
        await Assert.That(pos).IsNotNull();
        await Assert.That(pos!.Latitude).IsEqualTo(47.4);
        await Assert.That(pos.Longitude).IsEqualTo(8.5);
    }

    [Test]
    public async Task Delete_UrlEncodesId()
    {
        string? capturedUrl = null;
        HttpMethod? capturedMethod = null;
        var http = ApiTestHelpers.MockClient(req =>
        {
            capturedMethod = req.Method;
            capturedUrl = req.RequestUri?.AbsoluteUri;
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        });
        var api = new NoteApi(http, ApiTestHelpers.FixedBaseUrl());

        var r = await api.DeleteAsync("id with space");

        await Assert.That(r.Success).IsTrue();
        await Assert.That(capturedMethod).IsEqualTo(HttpMethod.Delete);
        await Assert.That(capturedUrl).EndsWith("/notes/id%20with%20space");
    }
}
