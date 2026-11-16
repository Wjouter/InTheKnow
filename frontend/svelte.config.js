import adapter from '@sveltejs/adapter-node';

const botCheckScripts = ['https://challenges.cloudflare.com', 'https://www.google.com', 'https://www.gstatic.com'];

/** @type {import('@sveltejs/kit').Config} */
export default {
	kit: {
		adapter: adapter(),
		// research R16. Kit adds the nonce for its own inline scripts.
		csp: {
			mode: 'auto',
			directives: {
				'default-src': ['self'],
				'script-src': ['self', ...botCheckScripts],
				'frame-src': ['https://challenges.cloudflare.com', 'https://www.google.com'],
				'style-src': ['self', 'unsafe-inline'],
				'img-src': ['self', 'data:'],
				'font-src': ['self'],
				'connect-src': ['self'],
				'form-action': ['self'],
				'frame-ancestors': ['none'],
				'base-uri': ['none'],
				'object-src': ['none']
			}
		},
		// Kit's Origin check would refuse our own form posts: no-referrer makes browsers send `Origin: null`.
		// hooks.server.ts checks Sec-Fetch-Site instead (research R4). `checkOrigin: false` is deprecated for this.
		csrf: { trustedOrigins: ['*'] },
		// The browser tests' fixtures use the app's types, so they are checked with it (011 T002).
		typescript: {
			config: (c) => {
				c.include.push('../e2e/**/*.ts');
				// Plain Node code, and the project has no Node types.
				c.exclude.push('../e2e/api-stub.ts');
			}
		}
	}
};
