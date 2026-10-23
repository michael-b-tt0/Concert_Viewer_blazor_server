export function setOpen(dialog, open) {
    if (!dialog.dataset.initialized) {
        dialog.addEventListener('cancel', event => event.preventDefault());
        dialog.dataset.initialized = 'true';
    }
    if (open && !dialog.open) dialog.showModal();
    if (!open && dialog.open) dialog.close();
}

// Keep navigation between the concerts and scraper pages from printing entries twice.
const loggedEntries = new Set();
export function logDiagnostics(entries) {
    for (const entry of entries) {
        const key = `${entry.jobId}:${entry.sequence}`;
        if (loggedEntries.has(key)) continue;
        loggedEntries.add(key);
        if (loggedEntries.size > 1024) loggedEntries.delete(loggedEntries.values().next().value);
        const write = entry.level === 'error' ? console.error : entry.level === 'warn' ? console.warn : console.info;
        write.call(console, `[AI corrections] ${entry.timestamp} | event ${entry.eventId} | ${entry.message}`,
            { jobId: entry.jobId, eventId: entry.eventId });
    }
}
