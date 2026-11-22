// The household's tree on the dashboard: portfolios start closed; what this browser expanded opens again on the next visit.
// Each portfolio is a <details data-tree-id="…">.  The open attribute is the browser's (the page never sets it), so Blazor's
// re-renders leave it alone; a portfolio drawn afresh (a new page, a reconnect) is reopened here if it was open before.
(() => {
    const key = 'tangent-atlas/tree-open';
    const load = () => {
        try {
            return new Set(JSON.parse(localStorage.getItem(key) ?? '[]'));
        } catch {
            return new Set();
        }
    };
    const save = (open) => {
        try {
            localStorage.setItem(key, JSON.stringify([...open]));
        } catch {
            // Storage refused (a private window, a full store): the tree simply starts closed next time.
        }
    };
    const reopen = (root) => {
        const open = load();
        const found = root.matches?.('details[data-tree-id]') ? [root] : [...(root.querySelectorAll?.('details[data-tree-id]') ?? [])];
        for (const details of found) {
            if (open.has(details.dataset.treeId) && !details.open) {
                details.open = true;
            }
        }
    };

    // toggle doesn't bubble: listen while it is captured on its way down.
    document.addEventListener('toggle', (event) => {
        const details = event.target;
        if (!(details instanceof HTMLDetailsElement) || !details.dataset.treeId) {
            return;
        }

        const open = load();
        if (details.open) {
            open.add(details.dataset.treeId);
        } else {
            open.delete(details.dataset.treeId);
        }

        save(open);
    }, true);

    new MutationObserver((mutations) => {
        for (const mutation of mutations) {
            for (const node of mutation.addedNodes) {
                if (node.nodeType === Node.ELEMENT_NODE) {
                    reopen(node);
                }
            }
        }
    }).observe(document.documentElement, { childList: true, subtree: true });

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', () => reopen(document.documentElement));
    } else {
        reopen(document.documentElement);
    }
})();
