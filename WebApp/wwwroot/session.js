// Keeps the sign-in alive while a page is open. The server refreshes the tokens when less than 5 minutes are left, but only
// when a request arrives; a page that is left open (a viewer on a model, /watchers waiting) would meet the end of the token
// on its next click. Asking every few minutes (the window before expiry is 5 minutes) renews the cookies in time, and when
// the session cannot be renewed (e.g., the refresh token expired) the page says so instead of failing with API errors.
const PERIOD = 4 * 60 * 1000; // ms, shorter than the 5-minute window in which the server refreshes

export function keepSessionAlive() {
    let timer = null;

    async function ping() {
        try {
            const resp = await fetch('/api/auth/session');
            if (resp.status === 401) {
                clearInterval(timer);
                showExpired(resp.headers.get('X-Auth-Error'));
            }
        } catch (err) {
            // Server briefly unavailable: the next ping tries again
        }
    }

    timer = setInterval(ping, PERIOD);
    // Timers are slowed down in background tabs: check as soon as the tab is shown again
    document.addEventListener('visibilitychange', () => {
        if (document.visibilityState === 'visible') {
            ping();
        }
    });
}

function showExpired(reason) {
    if (document.getElementById('session-expired')) {
        return;
    }
    const banner = document.createElement('div');
    banner.id = 'session-expired';
    banner.className = 'banner';
    banner.setAttribute('role', 'alert');
    banner.append('Your session expired. ');
    const login = document.createElement('a');
    login.href = '/api/auth/login';
    login.textContent = 'Log in again';
    banner.append(login);
    if (reason) {
        // Why the tokens could not be refreshed, as reported by APS
        banner.append(Object.assign(document.createElement('div'), { className: 'hint', textContent: reason }));
    }
    document.body.append(banner);
}
