import { describe, expect, it } from 'vitest';
import { pickTheme } from './theme';
import tokens from './styles/tokens.css?raw';

describe('pickTheme', () => {
	it('keeps light and dark', () => {
		expect(pickTheme('light')).toBe('light');
		expect(pickTheme('dark')).toBe('dark');
	});

	it.each([undefined, '', 'device', 'purple', 'DARK'])('treats %s as no choice', (v) => {
		expect(pickTheme(v)).toBeNull();
	});
});

// The dark token set (011 research R1, R8), read as text: Vitest runs without a browser to compute styles.
const css = tokens.replace(/\/\*[\s\S]*?\*\//g, '');
const declarations = (body: string) =>
	body
		.split(';')
		.map((d) => d.trim())
		.filter(Boolean);
const darkBlock = (selector: RegExp) => {
	const m = selector.exec(css);
	if (!m) throw new Error(`no block for ${selector}`);
	return declarations(m[1]);
};
const byDevice = darkBlock(/@media screen and \(prefers-color-scheme: dark\)\s*\{\s*:root:not\(\[data-theme='light'\]\)\s*\{([^}]*)\}\s*\}/);
const chosen = darkBlock(/@media screen\s*\{\s*:root\[data-theme='dark'\]\s*\{([^}]*)\}\s*\}/);

describe('dark tokens', () => {
	it('has the same declarations for "device is dark" and "Dark chosen"', () => {
		expect(byDevice.length).toBeGreaterThan(30);
		expect(chosen).toEqual(byDevice);
	});
});

// Tokens of the plain :root blocks, then the dark block over them.
const toMap = (decls: string[]) =>
	new Map(
		decls
			.filter((d) => d.startsWith('--'))
			.map((d) => {
				const i = d.indexOf(':');
				return [d.slice(0, i).trim(), d.slice(i + 1).trim()] as const;
			})
	);
const light = toMap([...css.replace(/@media[^{]*\{\s*:root[^{]*\{[^}]*\}\s*\}/g, '').matchAll(/:root\s*\{([^}]*)\}/g)].flatMap((m) => declarations(m[1])));
const dark = new Map([...light, ...toMap(byDevice)]);

function hex(value: string): string {
	let v = value;
	for (let i = 0; i < 10 && v.startsWith('var('); i++) {
		const name = /^var\((--[\w-]+)\)$/.exec(v)?.[1];
		v = (name && dark.get(name)) ?? v;
	}
	if (!/^#[0-9a-f]{6}$/i.test(v)) throw new Error(`${value} is not a colour: ${v}`);
	return v;
}
function luminance(h: string) {
	const [r, g, b] = [1, 3, 5].map((i) => {
		const c = parseInt(h.slice(i, i + 2), 16) / 255;
		return c <= 0.04045 ? c / 12.92 : ((c + 0.055) / 1.055) ** 2.4;
	});
	return 0.2126 * r + 0.7152 * g + 0.0722 * b;
}
/** WCAG 2 contrast ratio of two colours: tokens (`--x`) or fixed hex values. */
function contrast(a: string, b: string) {
	const [x, y] = [a, b].map((c) => luminance(hex(c.startsWith('--') ? `var(${c})` : c))).sort((p, q) => q - p);
	return (x + 0.05) / (y + 0.05);
}

// FR-004: text, icons that carry meaning 4.5:1; borders, control outlines and the focus ring 3:1.
const pages = ['--surface-page', '--white', '--surface-subtle'];
const pairs: [string, string, number][] = [
	...['--text-strong', '--text-body', '--text-muted', '--text-link'].flatMap((fg) => pages.map((bg): [string, string, number] => [fg, bg, 4.5])),
	...['cancelled', 'moved', 'new', 'info'].map((s): [string, string, number] => [`--status-${s}`, `--status-${s}-soft`, 4.5]),
	['--amber-700', '--amber-100', 4.5],
	['--green-700', '--green-100', 4.5],
	['--text-inverse', '--brand', 4.5],
	['--text-inverse', '--surface-dark', 4.5],
	['--text-inverse', '--surface-dark-hover', 4.5],
	['--text-inverse', '--brand-hover', 4.5],
	['--text-inverse', '--brand-press', 4.5],
	['--brand-on-page', '--surface-page', 4.5],
	['--text-link-hover', '--surface-page', 4.5],
	['--text-brand', '--brand-soft', 4.5],
	['--text-brand', '--surface-page', 4.5],
	['--text-brand', '--white', 4.5],
	['--text-brand-on-section', '--surface-section', 4.5],
	['--status-cancelled', '--white', 4.5],
	// The toast: its text and its fixed light-green icon.
	['--text-inverse', '--surface-dark', 4.5],
	['#e3f4ea', '--surface-dark', 4.5],
	...['--grey-500', '--border-strong', '--border-focus'].flatMap((fg) =>
		['--surface-page', '--white'].map((bg): [string, string, number] => [fg, bg, 3])
	),
	// The switch knob on its off and on track.
	['--text-inverse', '--grey-500', 3],
	['--text-inverse', '--brand', 3]
];

describe('dark contrast', () => {
	it.each(pairs)('%s on %s reaches %s:1', (fg, bg, min) => {
		expect(contrast(fg, bg)).toBeGreaterThanOrEqual(min);
	});

	it('keeps light as it was', () => {
		expect(light.get('--surface-page')).toBe('var(--white)');
		expect(light.get('--white')).toBe('#ffffff');
	});
});
