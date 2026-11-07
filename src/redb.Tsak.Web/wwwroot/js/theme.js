// Theme persistence: stores preference in localStorage, applies on load
(function () {
    const STORAGE_KEY = 'tsak-theme';

    function getPreferred() {
        const stored = localStorage.getItem(STORAGE_KEY);
        if (stored === 'dark' || stored === 'light') return stored;
        return window.matchMedia('(prefers-color-scheme: dark)').matches ? 'dark' : 'light';
    }

    function apply(theme) {
        document.documentElement.setAttribute('data-theme', theme);
        localStorage.setItem(STORAGE_KEY, theme);
    }

    // Apply immediately to prevent flash
    apply(getPreferred());

    // Expose toggle for Blazor interop
    window.tsakTheme = {
        toggle: function () {
            const current = document.documentElement.getAttribute('data-theme');
            const next = current === 'dark' ? 'light' : 'dark';
            apply(next);
            return next;
        },
        get: function () {
            return document.documentElement.getAttribute('data-theme') || 'light';
        }
    };
})();
