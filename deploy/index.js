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
                'SPA fallback registered at ' + ROUTE_PREFIX + '/*'
            );
        },

        stop: () => {
            app.setPluginStatus('Stopped');
        },
    };

    return plugin;
};
