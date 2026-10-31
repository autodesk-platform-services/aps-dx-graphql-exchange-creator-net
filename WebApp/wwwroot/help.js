// Help dialog of the header's "?" button: the client ID to provision in ACC (from the server's APS_CLIENT_ID) and links

export function initHelp() {
    const dialog = document.getElementById('help-dialog');
    const input = document.getElementById('help-client-id');
    const copy = document.getElementById('help-copy');
    let loaded = false;

    document.getElementById('help').onclick = async () => {
        dialog.showModal();
        if (!loaded) {
            try {
                const resp = await fetch('/api/auth/client');
                if (!resp.ok) {
                    throw new Error(await resp.text());
                }
                const { clientId } = await resp.json();
                showClientId(clientId);
                loaded = true;
            } catch (err) {
                showClientId('Could not read the client ID');
                console.error(err);
            }
        }
    };
    dialog.querySelector('.help-close').onclick = () => dialog.close();
    // A click on the backdrop lands on the dialog itself, outside its box
    dialog.addEventListener('click', (ev) => {
        const box = dialog.getBoundingClientRect();
        const inside = ev.clientX >= box.left && ev.clientX <= box.right && ev.clientY >= box.top && ev.clientY <= box.bottom;
        if (ev.target === dialog && !inside) {
            dialog.close();
        }
    });
    copy.onclick = async () => {
        await navigator.clipboard.writeText(input.value);
        copy.textContent = 'Copied';
        setTimeout(() => copy.textContent = 'Copy', 1500);
    };

    function showClientId(text) {
        input.value = text;
        input.style.width = `${text.length + 1}ch`; // Monospace font: one ch per character
    }
}
