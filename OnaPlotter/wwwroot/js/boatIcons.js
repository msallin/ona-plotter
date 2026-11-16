/**
 * SVG templates for the vessel chevrons (own boat and AIS targets) and
 * the radar-target triangle. Pure string builders so node can test them.
 *
 * Contrast against the chart comes from a dark halo drawn underneath
 * the shape inside the SVG, not a CSS drop-shadow filter: a filter is
 * an extra offscreen pass per marker (hundreds in a busy harbour), its
 * offset shadow rotates with the heading, and an inline style can't be
 * stripped by the slow-client CSS. The halo is centred, so it reads
 * the same at any heading.
 */

// Halo under every vessel shape: dark, semi-transparent, wider than the
// shape's own outline so a sliver shows around it on light charts.
const HALO = 'stroke="rgba(0,0,0,0.4)" stroke-linejoin="round" fill="none"';

/**
 * Chevron for a vessel. `category` is the C# ship-type bucket
 * (AisPalette.ShipTypeCategory) and adds a small glyph so the type is
 * readable without colour; own boat and unknown types get none.
 */
export function makeBoatSvg(fill, size, isOwn, category) {
    const s = size || 28;
    const h = s / 2;
    // Sleek arrow: pointed bow, tapered stern with a notch.
    const points = `0,-${h - 2} ${h - 6},${h - 4} 0,${h - 8} -${h - 6},${h - 4}`;
    const outline = isOwn
        ? `stroke="#fff" stroke-width="1.2" stroke-linejoin="round"`
        : `stroke="rgba(255,255,255,0.5)" stroke-width="0.8" stroke-linejoin="round"`;
    return `<svg width="${s}" height="${s}" viewBox="-${h} -${h} ${s} ${s}">`
         + `<polygon points="${points}" ${HALO} stroke-width="${isOwn ? 3.4 : 2.8}"/>`
         + `<polygon points="${points}" fill="${fill}" ${outline} opacity="${isOwn ? 1 : 0.9}"/>`
         + shipTypeGlyph(category)
         + `</svg>`;
}

/**
 * Small glyph near the centre of the chevron, 3-4 px so it never hides
 * the outline; a fixed dark stroke reads on any chevron colour.
 *   sail: diamond, fish: crossed nets, commercial: solid dot,
 *   service: plus.
 */
export function shipTypeGlyph(category) {
    const stroke = `stroke="rgba(0,0,0,0.7)" stroke-width="0.9" stroke-linecap="round"`;
    switch (category) {
        case 'sail':
            return `<polygon points="0,-3 2.5,0 0,3 -2.5,0" fill="rgba(255,255,255,0.85)" ${stroke}/>`;
        case 'fish':
            return `<line x1="-2.5" y1="-2" x2="2.5" y2="2" ${stroke}/>`
                 + `<line x1="-2.5" y1="2"  x2="2.5" y2="-2" ${stroke}/>`;
        case 'commercial':
            return `<circle cx="0" cy="0.5" r="1.8" fill="rgba(0,0,0,0.75)"/>`;
        case 'service':
            return `<line x1="-2.5" y1="0" x2="2.5" y2="0" ${stroke}/>`
                 + `<line x1="0" y1="-2.5" x2="0" y2="2.5" ${stroke}/>`;
        default:
            return '';
    }
}

/** Open triangle with a centre dot for a radar (ARPA / MARPA) target. */
export function makeRadarSvg(fill, size) {
    const s = size || 22;
    const h = s / 2;
    const points = `0,-${h - 3} ${h - 4},${h - 5} -${h - 4},${h - 5}`;
    return `<svg width="${s}" height="${s}" viewBox="-${h} -${h} ${s} ${s}">`
         + `<polygon points="${points}" ${HALO} stroke-width="3.4"/>`
         + `<polygon points="${points}" fill="none" stroke="${fill}" stroke-width="1.6" stroke-linejoin="round" opacity="0.95"/>`
         + `<circle cx="0" cy="0" r="2.4" fill="rgba(0,0,0,0.4)"/>`
         + `<circle cx="0" cy="0" r="1.6" fill="${fill}"/></svg>`;
}
