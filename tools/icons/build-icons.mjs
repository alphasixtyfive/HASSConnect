import { readFile, writeFile } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';
import { Resvg } from '@resvg/resvg-js';

const assets = new URL('../../src/HassConnect.App/Assets/', import.meta.url);
const icon = await readFile(new URL('hass-connect-icon.svg', assets), 'utf8');
const sizes = [16, 20, 24, 32, 40, 48, 64, 128, 256];

function render(svg, size) {
    return new Resvg(svg, { fitTo: { mode: 'width', value: size } }).render().asPng();
}

function ico(svg, dimensions) {
    const frames = dimensions.map(size => render(svg, size));
    const header = Buffer.alloc(6 + dimensions.length * 16);
    header.writeUInt16LE(1, 2);
    header.writeUInt16LE(dimensions.length, 4);
    let offset = header.length;
    dimensions.forEach((size, index) => {
        const entry = 6 + index * 16;
        header[entry] = size === 256 ? 0 : size;
        header[entry + 1] = size === 256 ? 0 : size;
        header.writeUInt16LE(1, entry + 4);
        header.writeUInt16LE(32, entry + 6);
        header.writeUInt32LE(frames[index].length, entry + 8);
        header.writeUInt32LE(offset, entry + 12);
        offset += frames[index].length;
    });
    return Buffer.concat([header, ...frames]);
}

await writeFile(new URL('hass-connect.png', assets), render(icon, 256));
await writeFile(new URL('app.ico', assets), ico(icon, sizes));

console.log(`Icons written to ${fileURLToPath(assets)}`);
