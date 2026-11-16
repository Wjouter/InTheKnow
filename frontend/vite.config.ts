import { sveltekit } from '@sveltejs/kit/vite';
import { loadEnv } from 'vite';
import { defineConfig } from 'vitest/config';

export default defineConfig(({ mode }) => ({
	plugins: [sveltekit()],
	define: { __VERSION__: JSON.stringify(loadEnv(mode, '.', 'INTHEKNOW_').INTHEKNOW_VERSION || 'dev') },
	test: {
		environment: 'node',
		include: ['src/**/*.test.ts'],
		// theme.test.ts reads the token file as text (?raw), which Vitest empties unless CSS is processed.
		css: { include: [/tokens\.css/] }
	}
}));
