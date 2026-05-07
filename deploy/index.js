// OnaPlotter SignalK plugin shim (deployed alongside the webapp
// bundle). The package.json carries both the `signalk-webapp` and
// `signalk-node-server-plugin` keywords:
//
//   * signalk-webapp:           mounts public/ at /<package-name>/
//                               via express.static.
//   * signalk-node-server-plugin: loads this file as a plugin so we
//                               can register additional routes.
//
// The webapp half serves static files; Blazor WASM uses
// client-side routing, so deep URLs like /signalk-onaplotter/map
// or /signalk-onaplotter/settings don't map to any file and
// return 404 on reload. The plugin half adds an SPA fallback:
// any GET under /<package-name>/ that the static mount didn't
// handle falls through to this route and we return public/index.html
// so the Blazor router takes over in the browser.
//
// Registration order: signalk-server's webapp interface mounts
// express.static BEFORE it starts plugins, so real static files
// still win and only genuine "no such file" misses reach this
// fallback. The regex excludes paths containing a '.' so a
// missing .wasm / .css / .js still returns a proper 404 instead
// of pretending to be HTML.
//
// The plugin also registers POST /log as a client-side error sink.
// Blazor's "An unhandled error has occurred" banner is opaque on
// iPad, and SSH-tailing the SignalK server log is the practical
// debug path at the helm - so the Blazor host installs window
// error listeners that POST to this endpoint. We forward each
// payload through app.error() so the entry appears in the
// standard SignalK log (journalctl -u signalk / /var/log/signalk).
// Throttled on the client to 1 Hz; the server-side is stateless.

const path = require('path');
const fs = require('fs');

module.exports = function (app) {
    // Package name is the directory we were loaded from. Using the
    // folder rather than hardcoding the name keeps the shim
    // portable across scoped / renamed installs.
    const PACKAGE_NAME = path.basename(__dirname);
    const INDEX_HTML = path.join(__dirname, 'public', 'index.html');
    const ROUTE_PREFIX = '/' + PACKAGE_NAME;

    const plugin = {
        id: PACKAGE_NAME,
        name: 'OnaPlotter (SPA fallback)',
        description:
            'Serves public/index.html for any Blazor client-routed path under ' +
            ROUTE_PREFIX + ' so reload / direct link / back button return the ' +
            'app instead of a bare 404.',

        schema: () => ({ type: 'object', properties: {} }),

        start: () => {
            if (!fs.existsSync(INDEX_HTML)) {
                app.setPluginError(
                    'public/index.html missing; SPA fallback disabled'
                );
                return;
            }

            // Error relay: POST /<pkg>/log. Accepts JSON bodies of the
            // shape { message, stack?, url?, userAgent?, ts? } and
            // forwards to the SignalK server log. We cap the payload
            // size so a misbehaving client can't flood the log with
            // a minified stack the size of the world.
            //
            // SignalK's webapp HTTP surface already applies the admin
            // auth middleware before our handlers run, so only an
            // authenticated user can reach this endpoint. No CORS
            // needed because the Blazor client is same-origin.
            const MAX_BYTES = 8 * 1024;
            app.post(ROUTE_PREFIX + '/log', (req, res) => {
                try {
                    const body = req.body || {};
                    const message = String(body.message || 'no message').slice(0, 800);
                    const stack   = String(body.stack   || '').slice(0, MAX_BYTES);
                    const url     = String(body.url     || '').slice(0, 400);
                    const ua      = String(body.userAgent || '').slice(0, 200);
                    const ts      = String(body.ts      || new Date().toISOString());
                    app.error(
                        '[onaplotter] client-error ' + ts + ' ' + ua + ' | ' +
                        message + (stack ? ' | ' + stack : '') +
                        (url ? ' | at ' + url : '')
                    );
                    res.json({ ok: true });
                } catch (e) {
                    res.status(400).json({ ok: false, error: String(e) });
                }
            });

            // Match any GET under the webapp prefix that doesn't
            // contain a '.' (i.e. not a file extension). Real assets
            // like /<pkg>/_framework/foo.wasm keep flowing through
            // express.static; only client routes (/<pkg>/map etc.)
            // land here.
            const fallback = new RegExp('^' + ROUTE_PREFIX + '/[^.]+$');
            app.get(fallback, (req, res) => {
                res.sendFile(INDEX_HTML);
            });
            app.setPluginStatus(
                'SPA fallback + error relay registered at ' + ROUTE_PREFIX + '/*'
            );
        },

        stop: () => {
            app.setPluginStatus('Stopped');
        },
    };

    return plugin;
};
