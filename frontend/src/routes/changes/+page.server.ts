import { loadFeed } from '$lib/server/changes';

export const load = async (event) => {
	const q = event.url.searchParams;
	const before = /^[1-9]\d{0,17}$/.test(q.get('before') ?? '') ? Number(q.get('before')) : undefined;
	const showHidden = q.get('showHidden') === '1';
	const { groups, next } = await loadFeed(event, { before, showHidden });
	return { groups, next, before: before ?? null, showHidden };
};
