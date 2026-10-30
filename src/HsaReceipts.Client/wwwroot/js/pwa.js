if ('serviceWorker' in navigator) {
    navigator.serviceWorker.register('/service-worker.js', { updateViaCache: 'none' })
        .then(registration => {
            registration.addEventListener('updatefound', () => {
                const worker = registration.installing;
                worker?.addEventListener('statechange', () => {
                    if (worker.state === 'installed' && navigator.serviceWorker.controller) {
                        window.dispatchEvent(new CustomEvent('receiptkeep:update-ready'));
                    }
                });
            });
        })
        .catch(error => console.error('Service worker registration failed.', error));
}
