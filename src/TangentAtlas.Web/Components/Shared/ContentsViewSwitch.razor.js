// Remembers the person's choice of view (tiles or details) in the site's cookie, without leaving the page.
export function remember(view) {
    return fetch(`/contents-view/${encodeURIComponent(view)}`, { method: 'POST', credentials: 'same-origin' })
        .then(() => undefined, () => undefined);
}
