const MAX_LISTED_ELEMENTS = 100;
const STEP_ICONS = { pending: '○', inProgress: '', completed: '✓', failed: '✗' };

export class ExchangeCreatorPanel extends Autodesk.Viewing.UI.DockingPanel {
    constructor(extension, id, title, options) {
        super(extension.viewer.container, id, title, options);
        this.extension = extension;
        this.container.style.left = (options?.x || 10) + 'px';
        this.container.style.top = (options?.y || 10) + 'px';
        this.container.style.width = (options?.width || 380) + 'px';
        this.container.style.height = (options?.height || 600) + 'px';
        this.container.style.resize = 'both';
        this.container.classList.add('exchange-creator-panel');
    }

    initialize() {
        this.title = this.createTitleBar(this.titleLabel || this.container.id);
        this.initializeMoveHandlers(this.title);
        this.container.appendChild(this.title);
        this.closer = this.createCloseButton();
        this.initializeCloseHandler(this.closer);
        this.container.appendChild(this.closer);

        this.content = document.createElement('div');
        this.content.className = 'ec-content';
        this.content.innerHTML = `
            <div class="ec-section">
                <div class="ec-label">Design</div>
                <div class="ec-design">Select a Revit design version in the sidebar.</div>
            </div>
            <div class="ec-section">
                <div class="ec-label">Selection set <span class="ec-badge ec-count">0</span></div>
                <div class="ec-hint ec-viewer-selection"></div>
                <div class="ec-buttons">
                    <button class="ec-add" title="Add the elements selected in the viewer to the selection set">Add selected</button>
                    <button class="ec-remove" title="Remove the elements selected in the viewer from the selection set">Remove selected</button>
                    <button class="ec-isolate" title="Isolate the selection set in the viewer">Isolate</button>
                    <button class="ec-clear">Clear</button>
                </div>
                <ul class="ec-elements"></ul>
            </div>
            <div class="ec-section">
                <label class="ec-field">Revit view name <input class="ec-view-name" type="text"></label>
                <label class="ec-field">Exchange name <input class="ec-exchange-name" type="text"></label>
                <div class="ec-field">Save the view in</div>
                <div class="ec-save-modes">
                    <label><input type="radio" name="ec-save-mode" value="newFile"> a new file <span class="ec-hint ec-new-file-name"></span></label>
                    <label><input type="radio" name="ec-save-mode" value="newVersion"> a new version of the source file</label>
                </div>
                <div class="ec-warning ec-new-version-warning">
                    Adds a new version of the design for everyone (earlier versions stay in ACC). Exchanges already
                    created from this file will sync to it. A new view is added, and included in the view/sheet set(s)
                    of the file's publish settings so that ACC processes it.
                </div>
                <button class="ec-create ec-primary">Create exchange</button>
            </div>
            <div class="ec-section">
                <div class="ec-label">Progress</div>
                <ul class="ec-steps"><li class="ec-hint">No exchange created yet.</li></ul>
                <div class="ec-error"></div>
                <div class="ec-links"></div>
            </div>
        `;
        this.container.appendChild(this.content);

        const handle = (selector, action) => {
            this.content.querySelector(selector).onclick = async () => {
                try {
                    await action();
                } catch (err) {
                    alert(`The operation failed: ${err.message}`);
                    console.error(err);
                }
                this.update();
            };
        };
        handle('.ec-add', () => this.extension.addViewerSelection());
        handle('.ec-remove', () => this.extension.removeViewerSelection());
        handle('.ec-isolate', () => this.extension.isolateSelectionSet());
        handle('.ec-clear', () => this.extension.clearSelectionSet());
        handle('.ec-create', () => this.extension.createExchange(
            this.content.querySelector('.ec-view-name').value.trim(),
            this.content.querySelector('.ec-exchange-name').value.trim()));
        // Keep the names in the extension's session state, so they survive the viewer re-creating the extension
        for (const radio of this.content.querySelectorAll('input[name="ec-save-mode"]')) {
            radio.onchange = () => { this.extension.setInput('saveMode', radio.value); this.update(); };
        }
        for (const [selector, key] of [['.ec-view-name', 'viewName'], ['.ec-exchange-name', 'exchangeName']]) {
            this.content.querySelector(selector).oninput = (ev) => { this.extension.setInput(key, ev.target.value); this.update(); };
        }
    }

    setVisible(show) {
        super.setVisible(show);
        if (!show) {
            this.extension?.onPanelClosed();
        }
    }

    // Fills the inputs from the session state, or with defaults if there's nothing to restore
    restoreInputs() {
        const viewName = this.extension.getInput('viewName');
        if (!viewName && this.extension.design) {
            this.resetInputs();
            return;
        }
        this.content.querySelector('.ec-view-name').value = viewName;
        this.content.querySelector('.ec-exchange-name').value = this.extension.getInput('exchangeName');
    }

    // Suggests default view and exchange names for a newly picked design
    resetInputs() {
        const design = this.extension.design;
        const now = new Date();
        const pad = (n) => String(n).padStart(2, '0');
        const viewName = `DX Selection ${now.getFullYear()}${pad(now.getMonth() + 1)}${pad(now.getDate())}-${pad(now.getHours())}${pad(now.getMinutes())}`;
        const exchangeName = design ? `${design.name.replace(/\.rvt$/i, '')} - ${viewName}` : '';
        this.content.querySelector('.ec-view-name').value = viewName;
        this.content.querySelector('.ec-exchange-name').value = exchangeName;
        this.extension.setInput('viewName', viewName);
        this.extension.setInput('exchangeName', exchangeName);
    }

    update() {
        if (!this.extension || !this.isVisible()) {
            return;
        }
        const { design, selectionSet, job } = this.extension;
        const jobRunning = job?.status === 'inProgress';
        const busy = jobRunning || this.extension.starting;

        this.content.querySelector('.ec-design').textContent = design ? design.name : 'Select a Revit design version in the sidebar.';
        this.content.querySelector('.ec-count').textContent = selectionSet.size;
        this.content.querySelector('.ec-viewer-selection').textContent = `${this.extension.viewerSelectionCount} object(s) selected in the viewer`;
        this.content.querySelector('.ec-add').disabled = !design || this.extension.viewerSelectionCount === 0;
        this.content.querySelector('.ec-remove').disabled = !design || this.extension.viewerSelectionCount === 0;
        this.content.querySelector('.ec-isolate').disabled = selectionSet.size === 0;
        this.content.querySelector('.ec-clear').disabled = selectionSet.size === 0;
        const create = this.content.querySelector('.ec-create');
        create.disabled = !design || selectionSet.size === 0 || busy;
        create.classList.toggle('ec-busy', busy);
        create.textContent = this.extension.starting ? 'Starting...' : jobRunning ? 'Creating exchange...' : 'Create exchange';

        // Save mode: cloud workshared models can only be saved in a new file
        const cloudModel = !!design?.isCloudModel;
        const saveMode = cloudModel ? 'newFile' : this.extension.getInput('saveMode');
        for (const radio of this.content.querySelectorAll('input[name="ec-save-mode"]')) {
            radio.checked = radio.value === saveMode;
            radio.disabled = busy || (cloudModel && radio.value === 'newVersion');
            radio.parentElement.title = cloudModel && radio.value === 'newVersion'
                ? 'Cloud workshared models can only get new versions by publishing from Revit' : '';
        }
        const exchangeName = this.content.querySelector('.ec-exchange-name').value.trim();
        this.content.querySelector('.ec-new-file-name').textContent = design
            ? `(${design.name.replace(/\.rvt$/i, '')}_temp_${exchangeName.replace(/[\\/:*?"<>|]/g, '_')}.rvt)` : '';
        this.content.querySelector('.ec-new-version-warning').hidden = saveMode !== 'newVersion';

        const list = this.content.querySelector('.ec-elements');
        list.replaceChildren();
        const names = Array.from(selectionSet.values()).map(element => element.name || '(unnamed)');
        for (const name of names.slice(0, MAX_LISTED_ELEMENTS)) {
            const li = document.createElement('li');
            li.textContent = name;
            list.appendChild(li);
        }
        if (names.length > MAX_LISTED_ELEMENTS) {
            const li = document.createElement('li');
            li.className = 'ec-hint';
            li.textContent = `... and ${names.length - MAX_LISTED_ELEMENTS} more`;
            list.appendChild(li);
        }

        if (job) {
            this._renderJob(job);
        }
    }

    _renderJob(job) {
        const steps = this.content.querySelector('.ec-steps');
        steps.replaceChildren();
        for (const step of job.steps) {
            const li = document.createElement('li');
            li.className = `ec-step ec-step-${step.status}`;
            const icon = document.createElement('span');
            icon.className = 'ec-step-icon';
            icon.textContent = STEP_ICONS[step.status];
            const text = document.createElement('div');
            const title = document.createElement('div');
            title.className = 'ec-step-title';
            title.textContent = step.title + (step.status === 'inProgress' ? ' ...' : '');
            text.appendChild(title);
            if (step.details) {
                const details = document.createElement('div');
                details.className = 'ec-step-details';
                details.textContent = step.details;
                text.appendChild(details);
            }
            li.append(icon, text);
            steps.appendChild(li);
        }
        this.content.querySelector('.ec-error').textContent = job.status === 'failed' ? job.error : '';
        const links = this.content.querySelector('.ec-links');
        links.replaceChildren();
        for (const link of job.links) {
            const a = document.createElement('a');
            a.href = link.url;
            a.target = '_blank';
            a.textContent = link.label;
            links.appendChild(a);
        }
    }
}
