import { defineConfig, devices } from '@playwright/test';

// Browser tests (011 research R8): the built frontend against a stub API with fixed answers.
const stubPort = 5199;
const appPort = 4173;

export default defineConfig({
	testDir: 'e2e',
	fullyParallel: true,
	forbidOnly: !!process.env.CI,
	reporter: process.env.CI ? 'github' : 'list',
	use: { baseURL: `http://localhost:${appPort}`, ...devices['Desktop Chrome'] },
	projects: [{ name: 'chromium' }],
	webServer: [
		{
			command: 'node e2e/api-stub.ts',
			port: stubPort,
			env: { STUB_PORT: String(stubPort) },
			reuseExistingServer: !process.env.CI,
			stdout: 'pipe'
		},
		{
			command: `npm run build && npm run preview -- --port ${appPort} --strictPort`,
			port: appPort,
			env: { API_URL: `http://localhost:${stubPort}` },
			reuseExistingServer: !process.env.CI,
			timeout: 180_000
		}
	]
});
