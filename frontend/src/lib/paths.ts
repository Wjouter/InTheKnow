/** Paths a visitor may open without signing in (research R6). */
export const isPublic = (path: string) =>
	path === '/' ||
	path === '/sign-in' ||
	path === '/sign-in/code' ||
	path === '/account' ||
	path.startsWith('/account/') ||
	path === '/language' ||
	path.startsWith('/_app/');
