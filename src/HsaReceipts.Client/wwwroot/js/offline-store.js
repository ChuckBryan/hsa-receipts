const databaseName = 'receipt-keep';
const databaseVersion = 1;

function openDatabase() {
    return new Promise((resolve, reject) => {
        const request = indexedDB.open(databaseName, databaseVersion);
        request.onupgradeneeded = () => {
            const db = request.result;
            if (!db.objectStoreNames.contains('settings')) {
                db.createObjectStore('settings');
            }
            if (!db.objectStoreNames.contains('snapshots')) {
                db.createObjectStore('snapshots');
            }
            if (!db.objectStoreNames.contains('drafts')) {
                db.createObjectStore('drafts', { keyPath: 'key' });
            }
        };
        request.onsuccess = () => resolve(request.result);
        request.onerror = () => reject(request.error);
    });
}

function requestResult(request) {
    return new Promise((resolve, reject) => {
        request.onsuccess = () => resolve(request.result);
        request.onerror = () => reject(request.error);
    });
}

async function transaction(storeName, mode, action) {
    const db = await openDatabase();
    try {
        const tx = db.transaction(storeName, mode);
        const completion = new Promise((resolve, reject) => {
            tx.oncomplete = resolve;
            tx.onerror = () => reject(tx.error);
            tx.onabort = () => reject(tx.error || new Error('Storage transaction aborted.'));
        });
        const result = await action(tx.objectStore(storeName));
        await completion;
        return result;
    } finally {
        db.close();
    }
}

function settingKey(userKey, name) {
    return `${userKey}:${name}`;
}

export function isOnline() {
    return navigator.onLine;
}

export async function getLastUserKey() {
    return transaction('settings', 'readonly', store =>
        requestResult(store.get('lastUserKey')));
}

export async function setLastUserKey(userKey) {
    return transaction('settings', 'readwrite', store =>
        requestResult(store.put(userKey, 'lastUserKey')));
}

export async function isOfflineEnabled(userKey) {
    return (await transaction('settings', 'readonly', store =>
        requestResult(store.get(settingKey(userKey, 'offlineEnabled'))))) === true;
}

export async function setOfflineEnabled(userKey, enabled) {
    return transaction('settings', 'readwrite', store =>
        requestResult(store.put(enabled, settingKey(userKey, 'offlineEnabled'))));
}

export async function saveReceiptSnapshot(userKey, snapshot) {
    return transaction('snapshots', 'readwrite', store =>
        requestResult(store.put(snapshot, userKey)));
}

export async function getReceiptSnapshot(userKey) {
    return transaction('snapshots', 'readonly', store =>
        requestResult(store.get(userKey)));
}

export async function saveDraft(userKey, draft, streamReference) {
    const bytes = await streamReference.arrayBuffer();
    const record = {
        key: `${userKey}:${draft.draftId}`,
        userKey,
        draft,
        file: new Blob([bytes], { type: draft.contentType || 'application/octet-stream' })
    };
    return transaction('drafts', 'readwrite', store =>
        requestResult(store.put(record)));
}

export async function saveDraftMetadata(userKey, draft) {
    return transaction('drafts', 'readwrite', async store => {
        const key = `${userKey}:${draft.draftId}`;
        const existing = await requestResult(store.get(key));
        if (!existing) {
            throw new Error('The local draft file is missing.');
        }
        existing.draft = draft;
        return requestResult(store.put(existing));
    });
}

export async function getDrafts(userKey) {
    return transaction('drafts', 'readonly', async store => {
        const records = await requestResult(store.getAll());
        return records
            .filter(record => record.userKey === userKey)
            .map(record => record.draft)
            .sort((left, right) => left.serviceDate.localeCompare(right.serviceDate));
    });
}

export async function deleteDraft(userKey, draftId) {
    return transaction('drafts', 'readwrite', store =>
        requestResult(store.delete(`${userKey}:${draftId}`)));
}

export async function getAntiforgeryToken() {
    const response = await fetch('/bff/antiforgery', {
        credentials: 'include',
        cache: 'no-store'
    });
    if (response.status === 401) {
        throw new Error('Your session expired. Sign in before submitting drafts.');
    }
    if (!response.ok) {
        throw new Error(`Unable to prepare submission (${response.status}).`);
    }
    return response.json();
}

export async function submitDraft(userKey, draftId, antiforgeryToken) {
    const record = await transaction('drafts', 'readonly', store =>
        requestResult(store.get(`${userKey}:${draftId}`)));
    if (!record) {
        throw new Error('The local draft no longer exists.');
    }

    const draft = record.draft;
    const body = new FormData();
    body.append('provider', draft.provider);
    body.append('patient', draft.patient);
    body.append('serviceDate', draft.serviceDate);
    body.append('amount', String(draft.amount));
    body.append('notes', draft.notes || '');
    body.append('idempotencyKey', draft.idempotencyKey);
    body.append('upload', record.file, draft.fileName);

    const response = await fetch('/api/v1/receipts', {
        method: 'POST',
        credentials: 'include',
        headers: { 'X-XSRF-TOKEN': antiforgeryToken },
        body
    });
    if (response.status === 401 || response.status === 403) {
        throw new Error('Your session is not authorized to submit this draft.');
    }
    if (!response.ok) {
        const details = await response.text();
        throw new Error(details || `Submission failed (${response.status}).`);
    }
    return response.json();
}

export function confirmAction(message) {
    return window.confirm(message);
}

export function submitLogout(antiforgeryToken) {
    const form = document.createElement('form');
    form.method = 'post';
    form.action = '/bff/logout';
    const input = document.createElement('input');
    input.type = 'hidden';
    input.name = '__RequestVerificationToken';
    input.value = antiforgeryToken;
    form.appendChild(input);
    document.body.appendChild(form);
    form.submit();
}

export async function clearPrivateData(userKey) {
    const db = await openDatabase();
    try {
        const tx = db.transaction(['settings', 'snapshots', 'drafts'], 'readwrite');
        tx.objectStore('settings').delete(settingKey(userKey, 'offlineEnabled'));
        tx.objectStore('snapshots').delete(userKey);
        const draftStore = tx.objectStore('drafts');
        const drafts = await requestResult(draftStore.getAllKeys());
        for (const key of drafts) {
            if (String(key).startsWith(`${userKey}:`)) {
                draftStore.delete(key);
            }
        }
        await new Promise((resolve, reject) => {
            tx.oncomplete = resolve;
            tx.onerror = () => reject(tx.error);
            tx.onabort = () => reject(tx.error || new Error('Storage clear aborted.'));
        });
    } finally {
        db.close();
    }
}

export async function requestPersistentStorage() {
    if (navigator.storage?.persist) {
        await navigator.storage.persist();
    }
}
