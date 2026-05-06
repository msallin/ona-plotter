using Microsoft.Extensions.Logging.Abstractions;
using OnaPlotter.Services.Places;

namespace OnaPlotter.Tests.Services.Places;

/// <summary>
/// Pins the Photon-feature -> PlaceResult mapping. The HTTP path is
/// not exercised here (would need a fake HttpClient) -- those are
/// covered by the contract tests on the IPlaceSearchService interface
/// when we add a fake-server harness later. The interesting logic
/// today is in the static helpers, which are the tightest contract
/// to lock in.
/// </summary>
public class PhotonPlaceSearchServiceTests
{
    [Test]
    public async Task TryMapFeature_Returns_Result_For_Well_Formed_Feature()
    {
        var feature = new PhotonFeature(
            new PhotonGeometry([13.388860, 52.517037]),  // [lon, lat]
            new PhotonProperties(
                Name: "Berlin",
                City: null,
                State: "Berlin",
                Country: "Germany",
                CountryCode: "DE",
                Type: "city"));

        var result = PhotonPlaceSearchService.TryMapFeature(feature);

        await Assert.That(result).IsNotNull();
        await Assert.That(result!.Name).IsEqualTo("Berlin");
        await Assert.That(result.Lat).IsEqualTo(52.517037);
        await Assert.That(result.Lon).IsEqualTo(13.388860);
        await Assert.That(result.Source).IsEqualTo("photon");
    }

    [Test]
    public async Task TryMapFeature_Null_When_Coords_Missing_Or_Short()
    {
        var noCoords = new PhotonFeature(
            new PhotonGeometry(null),
            new PhotonProperties(Name: "X", null, null, null, null, null));
        await Assert.That(PhotonPlaceSearchService.TryMapFeature(noCoords)).IsNull();

        var oneCoord = new PhotonFeature(
            new PhotonGeometry([12.0]),
            new PhotonProperties(Name: "X", null, null, null, null, null));
        await Assert.That(PhotonPlaceSearchService.TryMapFeature(oneCoord)).IsNull();
    }

    [Test]
    public async Task TryMapFeature_Null_When_Name_Missing()
    {
        var noName = new PhotonFeature(
            new PhotonGeometry([1.0, 1.0]),
            new PhotonProperties(Name: null, null, null, null, null, null));
        await Assert.That(PhotonPlaceSearchService.TryMapFeature(noName)).IsNull();

        var blankName = new PhotonFeature(
            new PhotonGeometry([1.0, 1.0]),
            new PhotonProperties(Name: "   ", null, null, null, null, null));
        await Assert.That(PhotonPlaceSearchService.TryMapFeature(blankName)).IsNull();
    }

    [Test]
    public async Task TryMapFeature_Null_On_Out_Of_Range_Coords()
    {
        // Photon shouldn't ship these but the SK chart-resource path
        // taught us not to trust upstream coordinate sanity.
        var badLat = new PhotonFeature(
            new PhotonGeometry([0.0, 91.0]),
            new PhotonProperties(Name: "ImpossibleNorth", null, null, null, null, null));
        await Assert.That(PhotonPlaceSearchService.TryMapFeature(badLat)).IsNull();

        var nanLon = new PhotonFeature(
            new PhotonGeometry([double.NaN, 0.0]),
            new PhotonProperties(Name: "Nope", null, null, null, null, null));
        await Assert.That(PhotonPlaceSearchService.TryMapFeature(nanLon)).IsNull();
    }

    [Test]
    public async Task BuildDisplayLabel_Combines_Available_Pieces()
    {
        var props = new PhotonProperties(
            Name: "Fowl Cay",
            City: "Exuma",
            State: null,
            Country: "Bahamas",
            CountryCode: "BS",
            Type: "island");
        await Assert.That(PhotonPlaceSearchService.BuildDisplayLabel("Fowl Cay", props))
            .IsEqualTo("Fowl Cay -- Exuma, Bahamas");
    }

    [Test]
    public async Task BuildDisplayLabel_Falls_Back_To_State_When_No_City()
    {
        var props = new PhotonProperties(
            Name: "Berlin",
            City: null,
            State: "Berlin",
            Country: "Germany",
            CountryCode: "DE",
            Type: "city");
        await Assert.That(PhotonPlaceSearchService.BuildDisplayLabel("Berlin", props))
            .IsEqualTo("Berlin -- Berlin, Germany");
    }

    [Test]
    public async Task BuildDisplayLabel_Country_Only_When_No_Locality()
    {
        var props = new PhotonProperties(
            Name: "Greenland Sea",
            City: null,
            State: null,
            Country: "International",
            CountryCode: null,
            Type: "sea");
        await Assert.That(PhotonPlaceSearchService.BuildDisplayLabel("Greenland Sea", props))
            .IsEqualTo("Greenland Sea -- International");
    }

    [Test]
    public async Task BuildDisplayLabel_Just_Name_When_No_Decorations()
    {
        await Assert.That(PhotonPlaceSearchService.BuildDisplayLabel("Solo", null))
            .IsEqualTo("Solo");
    }

    [Test]
    public async Task SearchAsync_Empty_Query_Returns_Empty()
    {
        // No HTTP call should fire on whitespace input. Using a
        // throw-on-call HttpClient asserts the short-circuit.
        var http = new HttpClient(new ThrowingHandler());
        var svc = new PhotonPlaceSearchService(http, NullLogger<PhotonPlaceSearchService>.Instance);
        var results = await svc.SearchAsync("   ");
        await Assert.That(results.Count).IsEqualTo(0);
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => throw new InvalidOperationException("HttpClient should not have been called");
    }
}
