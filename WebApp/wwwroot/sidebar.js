import { getViews, toUrn } from './viewer.js';

async function getJSON(url) {
    const resp = await fetch(url);
    if (!resp.ok) {
        alert('Could not load tree data. See console for more details.');
        console.error(await resp.text());
        return [];
    }
    return resp.json();
}

// Types reported by Data Management, used to describe the loaded design: item ID -> extension type, version ID -> file type
const itemTypes = new Map();
const versionTypes = new Map();

function createTreeNode(id, text, icon, children = false) {
    return { id, text, children, itree: { icon } };
}

async function getHubs() {
    const hubs = await getJSON('/api/hubs');
    return hubs.map(hub => createTreeNode(`hub|${hub.id}`, hub.name, 'icon-hub', true));
}

async function getProjects(hubId) {
    const projects = await getJSON(`/api/hubs/${hubId}/projects`);
    return projects.map(project => createTreeNode(`project|${hubId}|${project.id}`, project.name, 'icon-project', true));
}

async function getContents(hubId, projectId, folderId = null) {
    const contents = await getJSON(`/api/hubs/${hubId}/projects/${projectId}/contents` + (folderId ? `?folder_id=${folderId}` : ''));
    return contents.map(item => {
        if (item.folder) {
            return createTreeNode(`folder|${hubId}|${projectId}|${item.id}`, item.name, 'icon-my-folder', true);
        } else {
            itemTypes.set(item.id, item.type);
            return createTreeNode(`item|${hubId}|${projectId}|${item.id}`, item.name, 'icon-item', true);
        }
    });
}

async function getVersions(hubId, projectId, itemId, isRevit) {
    const versions = await getJSON(`/api/hubs/${hubId}/projects/${projectId}/contents/${itemId}/versions`);
    // Revit versions can be expanded to list their views
    versions.forEach(version => versionTypes.set(version.id, version.type));
    return versions.map(version => createTreeNode(`version|${hubId}|${projectId}|${itemId}|${version.id}`, version.name, 'icon-version', isRevit));
}

async function getVersionViews(hubId, projectId, itemId, versionId) {
    try {
        const views = await getViews(toUrn(versionId));
        if (views.length === 0) {
            return [createTreeNode(`info|${versionId}`, 'No views available', 'icon-info')];
        }
        return views.map(view => createTreeNode(`view|${hubId}|${projectId}|${itemId}|${versionId}|${view.guid}`, view.name, view.is3D ? 'icon-view-3d' : 'icon-view-2d'));
    } catch (err) {
        console.error(err);
        return [createTreeNode(`info|${versionId}`, 'Views not available (not translated yet?)', 'icon-info')];
    }
}

function getTypes(itemId, versionId) {
    return {
        isExchange: /:FDX$/i.test(itemTypes.get(itemId) || ''), // items:autodesk.bim360:FDX
        isCloudModel: /:C4RModel$/i.test(itemTypes.get(itemId) || ''), // items:autodesk.bim360:C4RModel (cloud workshared)
        fileType: versionTypes.get(versionId) || null
    };
}

export function initTree(selector, onSelectionChanged) {
    // See http://inspire-tree.com
    const tree = new InspireTree({
        data: function (node) {
            if (!node || !node.id) {
                return getHubs();
            } else {
                const tokens = node.id.split('|');
                switch (tokens[0]) {
                    case 'hub': return getProjects(tokens[1]);
                    case 'project': return getContents(tokens[1], tokens[2]);
                    case 'folder': return getContents(tokens[1], tokens[2], tokens[3]);
                    case 'item': return getVersions(tokens[1], tokens[2], tokens[3], /\.rvt$/i.test(node.text));
                    case 'version': return getVersionViews(tokens[1], tokens[2], tokens[3], tokens[4]);
                    default: return [];
                }
            }
        }
    });
    tree.on('node.click', function (event, node) {
        event.preventTreeDefault();
        const tokens = node.id.split('|');
        if (tokens[0] === 'version') {
            const [, hubId, projectId, itemId, versionId] = tokens;
            node.select();
            onSelectionChanged({ hubId, projectId, itemId, versionId, name: node.getParent().text, ...getTypes(itemId, versionId) });
        } else if (tokens[0] === 'view') {
            const [, hubId, projectId, itemId, versionId, viewGuid] = tokens;
            node.select();
            onSelectionChanged({ hubId, projectId, itemId, versionId, viewGuid, name: node.getParent().getParent().text, ...getTypes(itemId, versionId) });
        }
    });
    new InspireTreeDOM(tree, { target: selector });

    // Refreshing reloads the whole tree, then expands again the nodes that were expanded (each expansion loads
    // the node's children, which triggers the expansion of the next level) and selects the selected node again
    const restore = { expanded: new Set(), selected: null };
    const restoreState = (nodes) => {
        nodes.forEach(node => {
            if (node.id === restore.selected) {
                node.select();
            }
            if (restore.expanded.delete(node.id)) {
                node.expand();
            }
        });
    };
    tree.on('model.loaded', restoreState);
    tree.on('children.loaded', (node) => restoreState(node.children));

    return {
        refresh() {
            restore.expanded = new Set(tree.expanded().map(node => node.id));
            restore.selected = tree.selected()[0]?.id ?? null;
            return tree.reload();
        }
    };
}
