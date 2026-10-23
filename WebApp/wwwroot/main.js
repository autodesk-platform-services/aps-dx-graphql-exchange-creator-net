import { initViewer, loadModel, toUrn, getRevitVersion } from './viewer.js';
import { initTree } from './sidebar.js';
import { initSidebarLayout } from './layout.js';
import { keepSessionAlive } from './session.js';

initSidebarLayout();

const login = document.getElementById('login');
try {
    const resp = await fetch('/api/auth/profile');
    if (resp.ok) {
        keepSessionAlive();
        const user = await resp.json();
        document.getElementById('user').textContent = user.name;
        login.innerText = 'Logout';
        login.onclick = () => {
            const iframe = document.createElement('iframe');
            iframe.style.visibility = 'hidden';
            iframe.src = 'https://accounts.autodesk.com/Authentication/LogOut';
            document.body.appendChild(iframe);
            iframe.onload = () => {
                window.location.replace('/api/auth/logout');
                document.body.removeChild(iframe);
            };
        }
        const viewer = await initViewer(document.getElementById('preview'));
        let loading = 0; // Ignore the result of a load if another design was picked in the meantime
        showPlaceholder('Select a design version or one of its views in the tree to open it here.');
        const tree = initTree('#tree', async (design) => {
            hidePlaceholder();
            const current = ++loading;
            showDesignInfo(null);
            const loaded = loadModel(viewer, toUrn(design.versionId), design.viewGuid);
            viewer.getExtension('ExchangeCreatorExtension').setDesign(design);
            const doc = await loaded;
            if (current === loading) {
                showDesignInfo(design, doc);
            }
        });
        const refresh = document.getElementById('tree-refresh');
        refresh.onclick = async () => {
            refresh.disabled = true;
            try {
                await tree.refresh(); // e.g., to see a newly created exchange
            } finally {
                refresh.disabled = false;
            }
        };
        refresh.hidden = false;
    } else {
        document.getElementById('user').textContent = 'nobody';
        login.innerText = 'Login';
        showPlaceholder('Log in to browse your hubs and open a design.');
        login.onclick = () => window.location.replace('/api/auth/login');
    }
    login.style.visibility = 'visible';
} catch (err) {
    alert('Could not initialize the application. See console for more details.');
    console.error(err);
}

// Describes the loaded design above the viewer, e.g., "Revit file: 2023", "Data Exchange", "IFC file"
function showDesignInfo(design, doc = null) {
    const info = document.getElementById('design-info');
    info.replaceChildren();
    if (!design || !doc) {
        info.hidden = true;
        return;
    }
    const bold = (text) => Object.assign(document.createElement('b'), { textContent: text });
    const revitVersion = getRevitVersion(doc);
    if (design.isExchange) {
        info.append(bold('Data Exchange'));
    } else if (revitVersion || design.fileType === 'rvt') {
        info.append('Revit file', ...(revitVersion ? [': ', bold(revitVersion)] : []));
    } else if (design.fileType) {
        info.append(bold(design.fileType.toUpperCase()), ' file');
    } else {
        info.hidden = true;
        return;
    }
    info.hidden = false;
}

// Shown over the viewer until a design is picked, so that the empty viewer doesn't look like it's loading
function showPlaceholder(text) {
    const placeholder = document.getElementById('viewer-placeholder');
    placeholder.querySelector('.placeholder-text').textContent = text;
    placeholder.hidden = false;
}

function hidePlaceholder() {
    document.getElementById('viewer-placeholder').hidden = true;
}
