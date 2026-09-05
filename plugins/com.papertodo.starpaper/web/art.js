/* Original, deterministic SVG illustrations. No remote images, fonts, AI calls or dependencies. */
(function (root, factory) {
  const api = factory();
  if (typeof module === 'object' && module.exports) module.exports = api;
  else root.StarArt = api;
})(globalThis, function () {
  'use strict';
  const palettes = {
    orbit: ['#f0eaf8', '#756091', '#b09cc9', '#f8f5fc'],
    read: ['#e9eee8', '#547161', '#9bb49c', '#fcfaf3'],
    code: ['#e7eef5', '#4f718d', '#99b8cb', '#f5f9fc'],
    grow: ['#e9f0df', '#627c49', '#a6ba78', '#faf8ee'],
    travel: ['#f6eadb', '#ac7850', '#d6b38a', '#fff9eb'],
    health: ['#f6e5e1', '#ac6d66', '#d7a29a', '#fff5ed'],
    create: ['#f5e9ef', '#976780', '#c7a0b6', '#fff6f3'],
    focus: ['#e5eeed', '#537978', '#99b9b2', '#f6faf4']
  };
  function palette(scene) { return palettes[scene] || palettes.orbit; }
  function svg(scene, seed = 0) {
    const [bg, ink, soft, paper] = palette(scene);
    const shapes = {
      orbit: `<ellipse cx="160" cy="102" rx="99" ry="31" transform="rotate(-24 160 102)"/><circle cx="160" cy="99" r="44" fill="${soft}" stroke="none"/><path d="M72 142q104-9 161-79"/><circle cx="231" cy="65" r="9" fill="${paper}"/><path d="M104 43v16m-8-8h16M231 135v12m-6-6h12"/>`,
      read: `<path d="M69 60q47-17 89 5v91q-41-26-89-8z" fill="${paper}"/><path d="M158 65q47-22 92-5v88q-47-9-92 8z" fill="${paper}"/><path d="M158 68v83M85 81q28-5 54 4m-54 12q28-5 54 4m-54 12q28-5 42 1m57-28q25-12 48-7m-48 23q25-12 48-7m-48 23q25-12 36-9" stroke="${soft}"/><path d="M206 44v56l9-8 9 3V42" fill="${soft}" stroke="none"/>`,
      code: `<rect x="63" y="46" width="192" height="116" rx="11" fill="${paper}"/><path d="M63 70h192" stroke="${soft}"/><circle cx="79" cy="58" r="3" fill="${soft}" stroke="none"/><circle cx="92" cy="58" r="3" fill="${soft}" stroke="none"/><path d="m127 91-24 21 24 20m65-41 24 21-24 20m-19-46-20 54"/><path d="M108 177h108m-81-15-5 15m49-15 5 15"/>`,
      grow: `<path d="M133 124h56l-9 48h-38z" fill="${paper}"/><path d="M160 125V56"/><path d="M161 109q-49 0-49-34 46-1 49 34M161 90q0-36 39-40 1 38-39 40M160 70q-29-8-24-35 28 5 24 35" fill="${soft}"/><path d="M100 173h123" stroke="${soft}"/><circle cx="221" cy="93" r="17" stroke-dasharray="2 8"/>`,
      travel: `<path d="M37 158 111 71l51 58 35-45 88 74z" fill="${paper}"/><path d="m88 98 23-27 23 26-22-8z" fill="${soft}" stroke="none"/><circle cx="219" cy="53" r="22" fill="${soft}" stroke="none"/><path d="M43 169h238M62 180h154" stroke="${soft}"/><path d="m170 50 42-22-17 43-7-20z" fill="${paper}"/>`,
      health: `<path d="M112 113c-68-40-21-93 14-48l34 38 34-38c35-45 82 8 14 48l-48 44z" fill="${paper}"/><path d="M87 105h40l14-25 20 52 18-34 12 7h47"/><path d="M121 174h78" stroke="${soft}"/>`,
      create: `<path d="M92 166q-31-83 36-111 66-24 93 36 12 37-25 24-25-10-22 18 4 35-38 36z" fill="${paper}"/><circle cx="118" cy="88" r="10" fill="${soft}" stroke="none"/><circle cx="151" cy="72" r="9" fill="${ink}" stroke="none"/><circle cx="186" cy="87" r="9" fill="${soft}" stroke="none"/><path d="m173 164 43-92 11 5-42 93z" fill="${soft}"/><path d="M173 164q-17 5-13 18 16 7 25-12" fill="${ink}"/>`,
      focus: `<path d="M113 41h96m-96 128h96M124 43c0 36 0 42 36 61-36 19-36 27-36 63m73-124c0 36 0 42-36 61 36 19 36 27 36 63"/><path d="m131 65 30 33 29-33zM130 160l31-37 31 37z" fill="${soft}" stroke="none"/><path d="M82 76v15m-7-7h14m145 44v15m-7-7h14" stroke="${soft}"/>`
    };
    const dot = 20 + (Number(seed) >>> 0) % 22;
    return `<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 320 210"><rect width="320" height="210" fill="${bg}"/><circle cx="160" cy="104" r="85" fill="${paper}" opacity=".28"/><g fill="none" stroke="${ink}" stroke-width="2.5" stroke-linecap="round" stroke-linejoin="round">${shapes[scene] || shapes.orbit}</g><g fill="${soft}" opacity=".8"><circle cx="${dot}" cy="53" r="2"/><circle cx="281" cy="118" r="2.5"/><circle cx="59" cy="182" r="1.5"/></g></svg>`;
  }
  function data(scene, seed) { return `data:image/svg+xml;charset=utf-8,${encodeURIComponent(svg(scene, seed))}`; }
  return Object.freeze({ palette, svg, data });
});
