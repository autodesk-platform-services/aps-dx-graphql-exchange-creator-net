import './extensions/ExchangeCreatorExtension.js';

// urn -> { promise: Promise<Autodesk.Viewing.Document>, loadedAt }. Entries expire, so that a model load never reuses
// a document loaded during an earlier token's lifetime (in a page left open for a long time, that left models half
// loaded or loading forever)
const documents = new Map();
const DOCUMENT_CACHE_TTL = 5 * 60 * 1000; // ms

async function getAccessToken(callback) {
    try {
        const resp = await fetch('/api/auth/token');
        if (!resp.ok)
            throw new Error(await resp.text());
        const { access_token, expires_in } = await resp.json();
        callback(access_token, expires_in);
    } catch (err) {
        alert('Could not obtain access token. See the console for more details.');
        console.error(err);
    }
}

export function initViewer(container) {
    return new Promise(function (resolve, reject) {
        // Exchanges can only be viewed through the newer derivative service endpoints (SVF2 streaming)
        Autodesk.Viewing.FeatureFlags.set('DS_ENDPOINTS', true);
        Autodesk.Viewing.Initializer({
            env: 'AutodeskProduction2',
            api: 'streamingV2',
            getAccessToken
        }, function () {
            const config = {
                extensions: ['Autodesk.DocumentBrowser', 'ExchangeCreatorExtension']
            };
            const viewer = new Autodesk.Viewing.GuiViewer3D(container, config);
            viewer.start();
            viewer.setTheme('light-theme');
            // The viewer only tracks window resizes, and the container also changes size with the sidebar
            new ResizeObserver(() => viewer.resize()).observe(container);
            resolve(viewer);
        });
    });
}

// Derivative URNs must be URL-safe base64 (the newer derivative endpoints reject "/" and "+")
export function toUrn(versionId) {
    return Autodesk.Viewing.toUrlSafeBase64(versionId);
}

// Loads the manifest of a design once, and shares it between the sidebar (list of views) and the viewer
// A design that is still being processed (e.g., an exchange right after its creation) has an incomplete manifest.
// Note that a finished manifest can report status "failed" when only part of the processing failed (seen with a
// Revit 2024 file whose views were all viewable), so only "pending"/"inprogress" mean "not ready yet".
function isProcessed(doc) {
    const data = doc.getRoot().data || {};
    return data.progress === 'complete' || !['pending', 'inprogress'].includes(data.status);
}

function loadDocument(urn) {
    const cached = documents.get(urn);
    if (!cached || Date.now() - cached.loadedAt > DOCUMENT_CACHE_TTL) {
        const promise = new Promise(function (resolve, reject) {
            Autodesk.Viewing.Document.load('urn:' + urn, resolve, (code, message) => reject(new Error(`${message} (code ${code})`)));
        });
        // Only fully processed manifests are kept, otherwise the next attempt loads the manifest again
        // (a cached incomplete manifest left the design unviewable until the page was reloaded)
        const entry = { promise, loadedAt: Date.now() };
        const forget = () => { if (documents.get(urn) === entry) documents.delete(urn); };
        promise.then(doc => { if (!isProcessed(doc)) forget(); }, forget);
        documents.set(urn, entry);
    }
    return documents.get(urn).promise;
}

// Lists the viewables of a design (3D views first, then 2D views and sheets)
export async function getViews(urn) {
    const doc = await loadDocument(urn);
    return doc.getRoot().search({ type: 'geometry' })
        .map(node => ({ guid: node.guid(), name: node.name(), is3D: node.is3D() }))
        .sort((a, b) => b.is3D - a.is3D);
}

// Returns the loaded document, or null if it couldn't be loaded
export async function loadModel(viewer, urn, viewGuid = null) {
    try {
        const doc = await loadDocument(urn);
        if (!isProcessed(doc)) {
            const data = doc.getRoot().data || {};
            alert(`This design is still being processed (${data.progress || data.status}). Try again in a moment.`);
            return null;
        }
        const root = doc.getRoot();
        const node = (viewGuid && root.findByGuid(viewGuid)) || root.getDefaultGeometry();
        await viewer.loadDocumentNode(doc, node);
        return doc;
    } catch (err) {
        alert('Could not load model. See console for more details.');
        console.error(err);
        return null;
    }
}

// Revit version the design was saved with, recorded by the Revit extraction in the manifest
// (derivatives[].properties["Document Information"].RVTVersion), or null for other designs
export function getRevitVersion(doc) {
    const find = (data, depth) => {
        if (!data || typeof data !== 'object' || depth > 6) {
            return null;
        }
        if (data['Document Information']?.RVTVersion) {
            return data['Document Information'].RVTVersion;
        }
        for (const value of Object.values(data)) {
            const found = find(value, depth + 1);
            if (found) {
                return found;
            }
        }
        return null;
    };
    return find(doc?.getRoot().data, 0);
}
