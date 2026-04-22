using Microsoft.JSInterop;

namespace OnaPlotter.Components.Pages;

/// <summary>
/// Shared scaffolding for the two Map-page edit flows (route +
/// polygon-region). Previously this file held both flows end-to-end
/// at ~540 lines; split into three partials so each concern can be
/// read in isolation:
///
/// <list type="bullet">
///   <item><see cref="Map"/> partial in <c>Map.RouteEditing.razor.cs</c></item>
///   <item><see cref="Map"/> partial in <c>Map.PolygonEditing.razor.cs</c></item>
///   <item>This file: bits both of them need.</item>
/// </list>
///
/// What lives here:
/// <list type="bullet">
///   <item><see cref="InstallEditNavGuard"/> / <see cref="RemoveEditNavGuard"/>:
///     block in-app navigation (switch to another page) while an
///     edit is active. Browser-level close / refresh is handled JS-
///     side by the <c>startRouteEdit</c> / <c>startPolygonEdit</c>
///     module calls.</item>
///   <item><see cref="CoordsEqual"/>: shallow double[][] comparison
///     used by both UpdateRouteStats + UpdatePolygonStats to skip
///     re-renders when the poll tick returns unchanged coords.</item>
///   <item><c>_saveInFlight</c>: re-entrancy guard on the Save /
///     Save &amp; Go buttons. Without it a double-tap posts two
///     identical routes and activates both.</item>
/// </list>
/// </summary>
public partial class Map
{
    // Re-entrancy guard on Save / Save & Go. Without this, a double-tap
    // posts two identical routes + activates both. Silent re-entry is
    // the intended behaviour; the disabled button styling in the panel
    // is the visible affordance.
    private bool _saveInFlight;

    // Registered while route-edit OR polygon-edit is active. Router
    // fires LocationChanging before committing nav; we intercept and
    // ask the helm to confirm.
    private IDisposable? editNavGuard;

    // --- unsaved-changes guard for in-app navigation -----------------
    // Registered while route-edit or polygon-edit is active so switching
    // to Dashboard / Settings / etc. prompts before discarding the edit.
    // Browser-level navigation (close tab, back) is handled JS-side via
    // beforeunload in the same `startRouteEdit` / `startPolygonEdit`
    // module calls. RegisterLocationChangingHandler returns IDisposable;
    // disposing cancels the registration.

    private void InstallEditNavGuard()
    {
        if (editNavGuard is not null) return;
        editNavGuard = Nav.RegisterLocationChangingHandler(async ctx =>
        {
            if (!routeEditMode && !polygonEditMode) return;
            bool ok;
            try
            {
                ok = await JS.InvokeAsync<bool>("confirm",
                    "You have an unsaved edit. Leave and discard it?");
            }
            catch (JSDisconnectedException) { return; }
            if (!ok) ctx.PreventNavigation();
        });
    }

    private void RemoveEditNavGuard()
    {
        editNavGuard?.Dispose();
        editNavGuard = null;
    }

    // Shallow check so we don't render the whole panel 2 times/second
    // just because the polling loop fired; a drag will almost always
    // change one row and we want to re-render then, but if nothing moved
    // we don't.
    private static bool CoordsEqual(double[][]? a, double[][]? b)
    {
        if (a is null || b is null) return a is null && b is null;
        if (a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++)
        {
            if (a[i].Length != b[i].Length) return false;
            for (int j = 0; j < a[i].Length; j++)
                if (a[i][j] != b[i][j]) return false;
        }
        return true;
    }
}
