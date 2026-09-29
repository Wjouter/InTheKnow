import { expect, it } from 'vitest';

// FR-010, SC-010: nothing renders raw HTML. Vite reads the sources, so the test needs no Node types.
const sources = import.meta.glob(['/src/**/*.svelte', '/src/**/*.ts', '!/src/no-raw-html.test.ts'], {
	query: '?raw',
	import: 'default',
	eager: true
}) as Record<string, string>;

it('has no raw HTML tag anywhere in src/', () => {
	expect(Object.keys(sources).length).toBeGreaterThan(10);
	const needle = ['{', '@html'].join('');
	expect(Object.keys(sources).filter((path) => sources[path].includes(needle))).toEqual([]);
});
