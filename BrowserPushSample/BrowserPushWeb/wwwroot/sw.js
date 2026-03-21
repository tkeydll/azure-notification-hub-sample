// Service Worker for Browser Push Notifications
// Azure Notification Hubs から配信されたプッシュ通知を受信・表示する

self.addEventListener('install', (event) => {
    console.log('[SW] install');
    self.skipWaiting();
});

self.addEventListener('activate', (event) => {
    console.log('[SW] activate');
    event.waitUntil(self.clients.claim());
});

// プッシュ通知受信イベント
self.addEventListener('push', (event) => {
    console.log('[SW] push event received');

    let data = { title: 'Notification', body: 'New notification' };

    if (event.data) {
        try {
            data = event.data.json();
            console.log('[SW] payload:', JSON.stringify(data));
        } catch (e) {
            data.body = event.data.text();
            console.log('[SW] payload (text):', data.body);
        }
    }

    const options = {
        body: data.body || data.message || 'No message',
        icon: data.icon || 'https://azure.microsoft.com/favicon.ico',
        badge: data.badge || undefined,
        tag: data.tag || `azure-nh-${Date.now()}`,
        data: data.data || {},
        requireInteraction: false,
        actions: [
            { action: 'open', title: '開く' },
            { action: 'close', title: '閉じる' }
        ]
    };

    // ページにプッシュ受信を通知する
    event.waitUntil(
        Promise.all([
            self.registration.showNotification(data.title || 'Azure Notification Hubs', options),
            self.clients.matchAll({ type: 'window', includeUncontrolled: true }).then(clients => {
                for (const client of clients) {
                    client.postMessage({
                        type: 'PUSH_RECEIVED',
                        payload: data,
                        timestamp: new Date().toISOString()
                    });
                }
            })
        ])
    );
});

// 通知クリックイベント
self.addEventListener('notificationclick', (event) => {
    console.log('[SW] notification click:', event.action);
    event.notification.close();

    if (event.action === 'close') return;

    const url = event.notification.data?.url || '/';
    event.waitUntil(
        clients.matchAll({ type: 'window', includeUncontrolled: true }).then(clientList => {
            for (const client of clientList) {
                if (client.url.includes(self.location.origin) && 'focus' in client) {
                    return client.focus();
                }
            }
            return clients.openWindow(url);
        })
    );
});
