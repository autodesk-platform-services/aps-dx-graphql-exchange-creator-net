import { BaseExtension } from './BaseExtension.js';
import { ExchangeCreatorPanel } from './ExchangeCreatorPanel.js';

const POLLING_INTERVAL = 5000; // ms

// Whenever the viewer loads another model or view while one is already loaded, it unloads all extensions and
// creates new instances (see Viewer3D.loadDocumentNode: tearDown() + setUp()). What must survive this is kept
// here at module level; selection sets and job IDs are also saved in localStorage and restored in onModelLoaded.
const session = {
    design: null, // { hubId, projectId, itemId, versionId, viewGuid, name }
    viewName: '',
    exchangeName: '',
    saveMode: 'newFile', // or 'newVersion' (save the view as a new version of the source file)
    panelVisible: false
};

// Lets the user build a selection set of Revit elements, and turns it into a Data Exchange:
// the selection set is sent with the source design to Design Automation, which creates a Revit view
// with only these elements and saves it as <design>_temp.rvt, from which the exchange is then created.
class ExchangeCreatorExtension extends BaseExtension {
    constructor(viewer, options) {
        super(viewer, options);
        this._button = null;
        this._panel = null;
        this._unloaded = false;
        this._selectionSet = new Map(); // externalId -> { dbId, name }
        this._job = null;
        this._starting = false; // A job creation request is on its way
        this._pollingTimer = null;
    }

    load() {
        super.load();
        console.log('ExchangeCreatorExtension loaded.');
        return true;
    }

    unload() {
        super.unload();
        this._unloaded = true;
        this._stopPolling();
        if (this._button) {
            this.removeToolbarButton(this._button);
            this._button = null;
        }
        if (this._panel) {
            this._panel.setVisible(false);
            this._panel.uninitialize();
            this._panel = null;
        }
        console.log('ExchangeCreatorExtension unloaded.');
        return true;
    }

    onToolbarCreated() {
        this._panel = new ExchangeCreatorPanel(this, 'exchange-creator-panel', 'Exchange Creator');
        this._button = this.createToolbarButton('exchange-creator-button',
            'https://raw.githubusercontent.com/primer/octicons/main/icons/stack-24.svg',
            'Create an exchange from a selection set');
        this._button.onClick = () => this._setPanelVisible(!this._panel.isVisible());
        this._panel.restoreInputs();
        if (session.panelVisible) {
            this._setPanelVisible(true);
        }
    }

    _setPanelVisible(visible) {
        session.panelVisible = visible;
        this._panel.setVisible(visible);
        this._button.setState(visible ? Autodesk.Viewing.UI.Button.State.ACTIVE : Autodesk.Viewing.UI.Button.State.INACTIVE);
        this._panel.update();
    }

    // Called by the panel when it's closed with its own close button
    onPanelClosed() {
        if (this._unloaded) {
            return; // Hidden because the viewer is replacing this instance, not by the user
        }
        session.panelVisible = false;
        this._button?.setState(Autodesk.Viewing.UI.Button.State.INACTIVE);
    }

    get design() { return session.design; }
    get selectionSet() { return this._selectionSet; }
    get job() { return this._job; }
    get starting() { return this._starting; }
    get viewerSelectionCount() { return this.viewer.getSelection().length; }

    getInput(key) { return session[key]; }
    setInput(key, value) { session[key] = value; }

    // Called when a design version (or one of its views) is picked in the sidebar.
    // Note that this instance is usually replaced right after, when the viewer loads the new model.
    setDesign(design) {
        const sameVersion = session.design?.versionId === design.versionId;
        session.design = design;
        if (!sameVersion) {
            this._stopPolling();
            this._selectionSet = new Map();
            this._job = null;
            this._panel?.resetInputs();
        }
        this._panel?.update();
    }

    async onModelLoaded(model) {
        super.onModelLoaded(model);
        if (!session.design) {
            return;
        }
        // Restore the selection set saved for this design version, if any
        const saved = JSON.parse(localStorage.getItem(this._storageKey('selection')) || '[]');
        if (saved.length > 0) {
            const mapping = await new Promise((resolve, reject) => model.getExternalIdMapping(resolve, reject));
            for (const { externalId, name } of saved) {
                this._selectionSet.set(externalId, { dbId: mapping[externalId], name });
            }
        }
        // Resume tracking of the last job started for this design version, if any
        const jobId = localStorage.getItem(this._storageKey('job'));
        if (jobId) {
            this._pollJob(jobId);
        }
        this._panel?.update();
    }

    onSelectionChanged(model, dbids) {
        super.onSelectionChanged(model, dbids);
        this._panel?.update();
    }

    async addViewerSelection() {
        for (const element of await this._getSelectedElements()) {
            this._selectionSet.set(element.externalId, { dbId: element.dbId, name: element.name });
        }
        this._saveSelectionSet();
    }

    async removeViewerSelection() {
        for (const element of await this._getSelectedElements()) {
            this._selectionSet.delete(element.externalId);
        }
        this._saveSelectionSet();
    }

    clearSelectionSet() {
        this._selectionSet.clear();
        this._saveSelectionSet();
    }

    isolateSelectionSet() {
        const dbids = Array.from(this._selectionSet.values()).map(element => element.dbId).filter(dbid => dbid !== undefined);
        this.viewer.isolate(dbids);
        this.viewer.fitToView(dbids);
    }

    async createExchange(viewName, exchangeName) {
        if (this._starting || this._job?.status === 'inProgress') {
            return; // Ignore repeated clicks
        }
        this._starting = true;
        this._panel?.update(); // Show the busy state before the request even leaves
        try {
            const resp = await fetch('/api/jobs', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({
                    hubId: session.design.hubId,
                    projectId: session.design.projectId,
                    versionId: session.design.versionId,
                    viewName,
                    exchangeName,
                    externalIds: Array.from(this._selectionSet.keys()),
                    // Cloud workshared models can't get new versions through the API
                    saveMode: session.design.isCloudModel ? 'newFile' : session.saveMode
                })
            });
            if (!resp.ok) {
                throw new Error(await resp.text());
            }
            this._job = await resp.json();
            localStorage.setItem(this._storageKey('job'), this._job.id);
            // The first steps run right after the job is created: check back soon rather than at the usual interval
            this._schedulePolling(this._job.id, 1000);
        } finally {
            this._starting = false;
            this._panel?.update();
        }
    }

    // Collects the external IDs of the selected objects and of their children (e.g., curtain wall panels)
    async _getSelectedElements() {
        const model = this.viewer.model;
        const tree = model.getInstanceTree();
        const dbids = new Set();
        for (const dbid of this.viewer.getSelection()) {
            dbids.add(dbid);
            tree.enumNodeChildren(dbid, child => dbids.add(child), true);
        }
        const results = await new Promise((resolve, reject) => {
            model.getBulkProperties(Array.from(dbids), { propFilter: ['externalId', 'name'] }, resolve, reject);
        });
        return results
            .filter(result => result.externalId)
            .map(result => ({ dbId: result.dbId, externalId: result.externalId, name: result.name }));
    }

    _saveSelectionSet() {
        const entries = Array.from(this._selectionSet.entries()).map(([externalId, { name }]) => ({ externalId, name }));
        localStorage.setItem(this._storageKey('selection'), JSON.stringify(entries));
        this._panel?.update();
    }

    _storageKey(kind) {
        return `exchange-creator:${kind}:${session.design.versionId}`;
    }

    async _pollJob(jobId) {
        const versionId = session.design.versionId;
        try {
            const resp = await fetch(`/api/jobs/${jobId}`);
            if (this._unloaded || versionId !== session.design?.versionId) {
                return; // This instance was replaced, or another design was picked in the meantime
            }
            if (resp.status === 404) {
                // The server was restarted, and does not know about this job anymore
                localStorage.removeItem(this._storageKey('job'));
                return;
            }
            if (!resp.ok) {
                throw new Error(await resp.text());
            }
            this._job = await resp.json();
            this._panel?.update();
            if (this._job.status === 'inProgress') {
                this._schedulePolling(jobId);
            }
        } catch (err) {
            console.error('Could not get the job status, retrying.', err);
            this._schedulePolling(jobId);
        }
    }

    _schedulePolling(jobId, delay = POLLING_INTERVAL) {
        this._stopPolling();
        if (this._unloaded) {
            return;
        }
        this._pollingTimer = setTimeout(() => this._pollJob(jobId), delay);
    }

    _stopPolling() {
        if (this._pollingTimer) {
            clearTimeout(this._pollingTimer);
            this._pollingTimer = null;
        }
    }
}

Autodesk.Viewing.theExtensionManager.registerExtension('ExchangeCreatorExtension', ExchangeCreatorExtension);
