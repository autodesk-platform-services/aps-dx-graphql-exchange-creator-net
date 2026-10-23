const STORAGE_KEY = 'exchange-creator:sidebar';
const MIN_WIDTH = 200; // px
const COLLAPSE_THRESHOLD = 100; // px, dragging the splitter further left collapses the sidebar

// Makes the sidebar resizable (drag the splitter) and collapsible (header button, or double-click the splitter).
// The width and collapsed state are saved in the browser.
export function initSidebarLayout() {
    const splitter = document.getElementById('splitter');
    const toggle = document.getElementById('sidebar-toggle');
    const state = { width: 320, collapsed: false, ...JSON.parse(localStorage.getItem(STORAGE_KEY) || '{}') };

    function apply() {
        // The width is also capped in CSS so that the viewer keeps some room when the window shrinks
        document.documentElement.style.setProperty('--sidebar-width', `min(${state.width}px, 80vw)`);
        document.body.classList.toggle('sidebar-collapsed', state.collapsed);
        toggle.title = state.collapsed ? 'Show sidebar' : 'Hide sidebar';
        toggle.setAttribute('aria-expanded', String(!state.collapsed));
    }

    function save() {
        localStorage.setItem(STORAGE_KEY, JSON.stringify(state));
    }

    function toggleCollapsed() {
        state.collapsed = !state.collapsed;
        apply();
        save();
    }

    toggle.addEventListener('click', toggleCollapsed);
    splitter.addEventListener('dblclick', toggleCollapsed);

    splitter.addEventListener('pointerdown', function (ev) {
        if (ev.button !== 0) {
            return;
        }
        ev.preventDefault();
        splitter.setPointerCapture(ev.pointerId); // Keep receiving events when the pointer is over the viewer
        document.body.classList.add('sidebar-resizing');
        const startWidth = state.width;
        const onMove = (ev) => {
            if (ev.clientX < COLLAPSE_THRESHOLD) {
                state.collapsed = true;
                state.width = startWidth; // Expand back to the previous width

            } else {
                state.collapsed = false;
                state.width = Math.round(Math.min(Math.max(ev.clientX, MIN_WIDTH), window.innerWidth * 0.8));
            }
            apply();
        };
        const onEnd = (ev) => {
            splitter.releasePointerCapture(ev.pointerId);
            splitter.removeEventListener('pointermove', onMove);
            splitter.removeEventListener('pointerup', onEnd);
            splitter.removeEventListener('pointercancel', onEnd);
            document.body.classList.remove('sidebar-resizing');
            save();
        };
        splitter.addEventListener('pointermove', onMove);
        splitter.addEventListener('pointerup', onEnd);
        splitter.addEventListener('pointercancel', onEnd);
    });

    apply();
}
