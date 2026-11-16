// Small id-keyed Leaflet-layer dictionary with consistent remove/clear
// semantics. Lives in its own module so it can be unit-tested in node
// without a DOM or Leaflet; the tests pin the public shape so callers
// can't code against an API that doesn't exist on it.

/**
 * Owner of the Leaflet map, passed in so remove() can pull the layer
 * off the map too. In tests, a stub with `removeLayer(layer)` is enough.
 * @typedef {{ removeLayer(layer: any): void }} MapLike
 */

export class MarkerLayer {
    /** @param {MapLike | null} mapRef */
    constructor(mapRef) {
        // Stored as the constructor arg so tests can pass a stub. Real
        // callers pass the Leaflet map. null is fine for add-only flows
        // in test scenarios where we never remove.
        this._map = mapRef ?? null;
        this.items = {};
    }

    /** Replace the map reference (leafletInterop.js creates MarkerLayers
     *  before the map exists, then sets it later). */
    setMap(mapRef) { this._map = mapRef; }

    has(id) { return Object.prototype.hasOwnProperty.call(this.items, id); }
    get(id) { return this.items[id]; }
    keys() { return Object.keys(this.items); }
    get size() { return Object.keys(this.items).length; }

    set(id, layer) { this.items[id] = layer; }

    remove(id) {
        const layer = this.items[id];
        if (layer) {
            if (this._map) this._map.removeLayer(layer);
            delete this.items[id];
        }
    }

    clear() {
        for (const id of Object.keys(this.items)) this.remove(id);
    }

    /** Generator so callers can `for (const [id, layer] of layer.entries())`
     *  without reaching into .items, mirroring Map.prototype.entries. */
    * entries() {
        for (const id of Object.keys(this.items)) {
            yield [id, this.items[id]];
        }
    }
}
