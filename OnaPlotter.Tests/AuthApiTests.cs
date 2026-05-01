using System.Net;
using OnaPlotter.Models;
using OnaPlotter.Services.Api;

namespace OnaPlotter.Tests;

/// <summary>
/// Pins <see cref="AuthApi"/>'s contract with
/// <c>/skServer/loginStatus</c>: parses the documented JSON shape,
/// surfaces null on absent endpoint / transport failure, and the
/// derived <see cref="LoginStatus.ShouldShowLoginWarning"/> rule
/// matches helm intent (warn only when SK has security enabled and
/// the session can't write).
/// </summary>
public class AuthApiTests
{
    private static AuthApi Api(string body, HttpStatusCode status = HttpStatusCode.OK,
        Action<HttpRequestMessage>? onRequest = null)
    {
        var http = ApiTestHelpers.MockClient(req =>
        {
            onRequest?.Invoke(req);
            var path = req.RequestUri?.AbsolutePath ?? "";
            if (path.Contains("/skServer/loginStatus", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(status)
                {
                    Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
                };
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });
        return new AuthApi(http, ApiTestHelpers.FixedBaseUrl());
    }

    [Test]
    public async Task NotLoggedIn_Body_Parses_AndFlagsTriggerBanner()
    {
        // The exact response openplotter.local returned during
        // implementation -- pin so a future SK-server tweak that
        // renames a field surfaces as a parser break.
        string body = """
        {"status":"notLoggedIn","readOnlyAccess":true,"authenticationRequired":true,"allowNewUserRegistration":false,"allowDeviceAccessRequests":true,"securityWasEnabled":false}
        """;
        var s = await Api(body).GetLoginStatusAsync();

        await Assert.That(s).IsNotNull();
        await Assert.That(s!.Status).IsEqualTo("notLoggedIn");
        await Assert.That(s.ReadOnlyAccess).IsTrue();
        await Assert.That(s.AuthenticationRequired).IsTrue();
        await Assert.That(s.ShouldShowLoginWarning).IsTrue();
    }

    [Test]
    public async Task LoggedIn_Hides_Banner()
    {
        // Logged-in session: status flips, readOnlyAccess goes false,
        // username carries through. Banner stays hidden.
        string body = """
        {"status":"loggedIn","username":"helm","readOnlyAccess":false,"authenticationRequired":true}
        """;
        var s = await Api(body).GetLoginStatusAsync();

        await Assert.That(s).IsNotNull();
        await Assert.That(s!.Username).IsEqualTo("helm");
        await Assert.That(s.ShouldShowLoginWarning).IsFalse();
    }

    [Test]
    public async Task AuthDisabled_Hides_Banner_Even_If_NotLoggedIn()
    {
        // Open homelab / dev server: authenticationRequired=false.
        // The banner stays hidden because there's no login to opt
        // into; warning the helm "not logged in" on a server that
        // doesn't HAVE auth would be a false alarm.
        string body = """
        {"status":"notLoggedIn","readOnlyAccess":false,"authenticationRequired":false}
        """;
        var s = await Api(body).GetLoginStatusAsync();

        await Assert.That(s).IsNotNull();
        await Assert.That(s!.AuthenticationRequired).IsFalse();
        await Assert.That(s.ShouldShowLoginWarning).IsFalse();
    }

    [Test]
    public async Task EndpointMissing_404_Returns_Null()
    {
        // Third-party SK server that doesn't ship /skServer/loginStatus
        // returns 404. The chip stays hidden -- "we can't tell" is
        // the honest UX. Pinning here so a future change to
        // ResourceHttp's error handling can't accidentally start
        // throwing on 404 (which would crash the page).
        var s = await Api("", HttpStatusCode.NotFound).GetLoginStatusAsync();
        await Assert.That(s).IsNull();
    }

    [Test]
    public async Task TransportFailure_Returns_Null()
    {
        // DNS fail / network drop. Same null-degradation as 404.
        var http = ApiTestHelpers.MockClient(_ =>
            throw new HttpRequestException("dns fail"));
        var sut = new AuthApi(http, ApiTestHelpers.FixedBaseUrl());
        var s = await sut.GetLoginStatusAsync();
        await Assert.That(s).IsNull();
    }

    [Test]
    public async Task MalformedJson_Returns_Null()
    {
        // Some proxy returned an HTML error page. Don't crash;
        // surface null so the chip stays hidden until the next
        // poll picks up a clean response.
        var s = await Api("<html>error</html>").GetLoginStatusAsync();
        await Assert.That(s).IsNull();
    }

    [Test]
    public async Task UnexpectedStatus_String_StillReadsAsNotLoggedIn()
    {
        // Future SK-server build ships a third status string ("guest",
        // "locked", whatever). Helm-side classifier treats anything
        // that isn't exactly "loggedIn" (case-insensitive) as not
        // logged in -- belt-and-braces against a server-side rename
        // that would otherwise default-to-allow.
        string body = """
        {"status":"guest","readOnlyAccess":true,"authenticationRequired":true}
        """;
        var s = await Api(body).GetLoginStatusAsync();
        await Assert.That(s!.ShouldShowLoginWarning).IsTrue();
    }

    [Test]
    public async Task Hits_Correct_Endpoint_Path()
    {
        // The endpoint is server-implementation-specific; pin the
        // path so a future refactor that points it at /signalk/...
        // (and silently breaks against signalk-server) goes red here.
        string? capturedPath = null;
        string body = """{"status":"notLoggedIn","readOnlyAccess":true,"authenticationRequired":true}""";
        var sut = Api(body, onRequest: req => { capturedPath = req.RequestUri?.AbsolutePath; });

        await sut.GetLoginStatusAsync();

        await Assert.That(capturedPath).IsEqualTo("/skServer/loginStatus");
    }
}
