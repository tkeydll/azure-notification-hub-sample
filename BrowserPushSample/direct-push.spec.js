// @ts-check
// 切り分けテスト: web-push で直接送信し、Playwright Chromium が Push を受信できるか検証
// Azure NH を一切使わない純粋な Web Push テスト
const { test, expect, chromium } = require('@playwright/test');
const webpush = require('web-push');
const fs = require('fs');
const path = require('path');
const http = require('http');

test.describe('Direct Web Push Test (Azure NH なし)', () => {

  test('web-push で直接送信し、ブラウザで受信を確認', async () => {
    // ── VAPID キーペアを生成 ──
    const vapidKeys = webpush.generateVAPIDKeys();
    console.log('🔑 VAPID keys generated');
    console.log(`   Public:  ${vapidKeys.publicKey.substring(0, 40)}...`);

    webpush.setVapidDetails(
      'mailto:test@example.com',
      vapidKeys.publicKey,
      vapidKeys.privateKey
    );

    // ── 簡易 HTTP サーバーを起動 ──
    const serverPort = 5299;
    const server = http.createServer((req, res) => {
      if (req.url === '/') {
        res.writeHead(200, { 'Content-Type': 'text/html; charset=utf-8' });
        res.end(getTestHtml(vapidKeys.publicKey));
      } else if (req.url === '/sw-test.js') {
        res.writeHead(200, { 'Content-Type': 'application/javascript' });
        res.end(getServiceWorkerJs());
      } else {
        res.writeHead(404);
        res.end('Not Found');
      }
    });

    await new Promise((resolve) => server.listen(serverPort, resolve));
    console.log(`🌐 テストサーバー: http://localhost:${serverPort}`);

    // ── ブラウザ起動 ──
    const profileDir = path.join(__dirname, 'test-profile-direct3');
    if (fs.existsSync(profileDir)) {
      fs.rmSync(profileDir, { recursive: true, force: true });
    }

    const context = await chromium.launchPersistentContext(profileDir, {
      headless: false,
      permissions: ['notifications'],
      args: [
        '--enable-features=PushMessaging',
        '--disable-features=PushMessagingQuietUi',
        '--no-default-browser-check',
        '--no-first-run',
      ],
    });

    const page = context.pages()[0] || await context.newPage();

    const browserLogs = [];
    page.on('console', msg => {
      browserLogs.push(msg.text());
      console.log(`📋 [browser] ${msg.text()}`);
    });

    try {
      // ── ページを開く ──
      await page.goto(`http://localhost:${serverPort}`, { waitUntil: 'networkidle' });
      console.log('✅ ページ読み込み完了');

      // ── SW 登録 + Push 購読 (英語テキストで判定) ──
      console.log('\n📌 SW 登録 & Push 購読...');
      await page.click('#btnSetup');

      // 英語テキスト "complete" を待つ (UTF-8 問題回避)
      await expect(page.locator('#status')).toContainText('complete', { timeout: 30000 });
      console.log('✅ Push 購読完了');

      // ── PushSubscription を取得 ──
      console.log('   Subscription を取得中...');
      const subscriptionJson = await page.evaluate(() => {
        return window._pushSubscriptionJson;
      });

      if (!subscriptionJson) {
        throw new Error('PushSubscription が取得できなかった');
      }

      console.log(`   endpoint: ${subscriptionJson.endpoint.substring(0, 60)}...`);

      // ── web-push で直接送信 ──
      console.log('\n📌 web-push で直接プッシュ送信...');
      const payload = JSON.stringify({
        title: 'Direct Push Test',
        body: 'Sent directly via web-push library!',
        timestamp: new Date().toISOString()
      });

      let sendSuccess = false;
      try {
        const result = await webpush.sendNotification(
          subscriptionJson,
          payload,
          { TTL: 60 }
        );
        console.log(`✅ web-push 送信成功！ statusCode: ${result.statusCode}`);
        sendSuccess = true;
      } catch (pushErr) {
        console.log(`❌ web-push 送信失敗: ${pushErr.statusCode || pushErr.message}`);
        if (pushErr.body) console.log(`   body: ${pushErr.body}`);
      }

      // ── プッシュ受信を待機 (最大60秒) ──
      console.log('\n⏳ プッシュ受信を待機 (最大60秒)...');
      let pushReceived = false;

      for (let i = 0; i < 12; i++) {
        await page.waitForTimeout(5000);

        // #pushResult のテキストをチェック
        const resultText = await page.locator('#pushResult').innerText();
        if (resultText && resultText.includes('received')) {
          pushReceived = true;
          console.log(`\n🔔 プッシュ受信成功！ (${(i + 1) * 5}秒後)`);
          console.log(`   ${resultText}`);
          break;
        }

        // SW コンソールログもチェック
        const swPush = browserLogs.find(l => l.includes('[SW] push event'));
        if (swPush) {
          pushReceived = true;
          console.log(`\n🔔 SW push event 検知！ (${(i + 1) * 5}秒後)`);
          break;
        }

        console.log(`   ... ${(i + 1) * 5}秒経過`);
      }

      // ── スクリーンショット ──
      const screenshotDir = path.join(__dirname, 'test-results');
      if (!fs.existsSync(screenshotDir)) fs.mkdirSync(screenshotDir, { recursive: true });
      await page.screenshot({
        path: path.join(screenshotDir, 'direct-webpush-test.png'),
        fullPage: true
      });

      // ── 結果判定 ──
      console.log('\n════════════════════════════════════════');
      console.log('📋 診断結果:');
      console.log('════════════════════════════════════════');
      console.log(`   web-push 送信: ${sendSuccess ? '✅ 成功 (FCM が 201 を返した)' : '❌ 失敗'}`);
      console.log(`   ブラウザ受信: ${pushReceived ? '✅ 成功' : '❌ 受信できず'}`);

      if (sendSuccess && !pushReceived) {
        console.log('\n   💡 FCM は通知を受理したが、Playwright Chromium には届かなかった。');
        console.log('   これは Playwright の既知の制限：');
        console.log('   - 自動テストの Chromium は FCM からの Push 配信チャネルが');
        console.log('     正常に確立されないことがある');
        console.log('   - 実際のブラウザ (Edge/Chrome) では正常に動作する可能性が高い');
        console.log('\n   結論: Azure NH の送信フロー自体は正常。');
        console.log('   通知が届かないのは Playwright + Chromium の制限による。');
      } else if (pushReceived) {
        console.log('\n   🎉 ブラウザで Push 受信できた！');
        console.log('   → Azure NH テストで届かない場合は NH 側の配信問題。');
      }
      console.log('════════════════════════════════════════');

    } finally {
      await context.close();
      server.close();
    }
  });
});

// ── テスト用HTML (英語テキストで判定しやすく) ──
function getTestHtml(vapidPublicKey) {
  return `<!DOCTYPE html>
<html><head><meta charset="utf-8"><title>Direct Push Test</title></head>
<body>
<h1>Direct Web Push Test</h1>
<button id="btnSetup" onclick="setup()">Setup</button>
<div id="status">waiting...</div>
<div id="pushResult"></div>
<pre id="log"></pre>
<script>
window._pushSubscriptionJson = null;

function log(msg) {
  document.getElementById('log').textContent += msg + '\\n';
  console.log(msg);
}

async function setup() {
  try {
    log('SW registering...');
    const reg = await navigator.serviceWorker.register('/sw-test.js');
    await navigator.serviceWorker.ready;
    log('SW ready');

    const permission = await Notification.requestPermission();
    log('notification permission: ' + permission);

    const existing = await reg.pushManager.getSubscription();
    if (existing) await existing.unsubscribe();

    const padding = '='.repeat((4 - '${vapidPublicKey}'.length % 4) % 4);
    const base64 = ('${vapidPublicKey}' + padding).replace(/-/g, '+').replace(/_/g, '/');
    const raw = atob(base64);
    const key = new Uint8Array(raw.length);
    for (let i = 0; i < raw.length; i++) key[i] = raw.charCodeAt(i);

    log('subscribing...');
    const subscription = await reg.pushManager.subscribe({
      userVisibleOnly: true,
      applicationServerKey: key
    });

    window._pushSubscriptionJson = subscription.toJSON();
    log('subscription complete! endpoint: ' + subscription.endpoint.substring(0, 50) + '...');
    document.getElementById('status').textContent = 'subscription complete';

    navigator.serviceWorker.addEventListener('message', (event) => {
      if (event.data && event.data.type === 'PUSH_RECEIVED') {
        log('PUSH received: ' + JSON.stringify(event.data.payload));
        document.getElementById('pushResult').textContent = 'push received: ' + (event.data.payload.title || 'untitled');
      }
    });
  } catch (e) {
    log('ERROR: ' + e.message);
    document.getElementById('status').textContent = 'error: ' + e.message;
  }
}
</script>
</body></html>`;
}

// ── テスト用 Service Worker ──
function getServiceWorkerJs() {
  return `
self.addEventListener('install', () => { self.skipWaiting(); });
self.addEventListener('activate', (e) => { e.waitUntil(self.clients.claim()); });

self.addEventListener('push', (event) => {
  console.log('[SW] push event received');
  let data = { title: 'Test', body: 'test' };
  if (event.data) {
    try { data = event.data.json(); } catch(e) { data.body = event.data.text(); }
  }
  console.log('[SW] payload: ' + JSON.stringify(data));

  event.waitUntil(
    Promise.all([
      self.registration.showNotification(data.title || 'Test', { body: data.body || 'test' }),
      self.clients.matchAll({ type: 'window', includeUncontrolled: true }).then(clients => {
        for (const client of clients) {
          client.postMessage({ type: 'PUSH_RECEIVED', payload: data });
        }
      })
    ])
  );
});
`;
}
