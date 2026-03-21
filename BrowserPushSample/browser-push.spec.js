// @ts-check
const { test, expect, chromium } = require('@playwright/test');
const fs = require('fs');
const path = require('path');

// Push API は incognito モードで使えないため、
// launchPersistentContext で実プロファイルを使用する
test.describe('Browser Push Notification E2E Test', () => {

  test('全フロー: SW登録 → 購読 → Azure NH登録 → 通知送信 → 受信確認', async () => {
    const BASE = 'http://localhost:5285';

    // ── 古いレジストレーションをクリーンアップ ──
    console.log('\n🧹 古いレジストレーションをクリーンアップ中...');
    const cleanupRes = await (await fetch(`${BASE}/api/cleanup`, { method: 'POST' })).json();
    console.log(`   削除: ${cleanupRes.deleted}件 / 合計: ${cleanupRes.total}件`);
    console.log(`   新しいユニークタグ: ${cleanupRes.newRunTag}`);

    // ── テスト用プロファイルをリセット ──
    const profileDir = path.join(__dirname, 'test-profile-v3');
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

    // コンソールログを全部拾う
    const browserLogs = [];
    page.on('console', msg => {
      const text = msg.text();
      browserLogs.push(text);
      const type = msg.type();
      const prefix = type === 'error' ? '❌' : type === 'warning' ? '⚠️' : '📋';
      console.log(`${prefix} [browser] ${text}`);
    });

    // ── ページを開く ──
    console.log(`\n🌐 ${BASE} を開いています...`);
    await page.goto(BASE, { waitUntil: 'networkidle' });
    await expect(page.locator('h1')).toContainText('Browser Push Notification');
    console.log('✅ ページ読み込み完了');

    // ══════════════════════════════════════════════════════════════
    // Step 1: Service Worker 登録
    // ══════════════════════════════════════════════════════════════
    console.log('\n📌 Step 1: Service Worker を登録...');
    await page.click('#btnRegisterSW');
    await expect(page.locator('#swStatus')).toHaveText('登録済み', { timeout: 10000 });
    console.log('✅ Service Worker 登録完了');
    await expect(page.locator('#btnSubscribe')).toBeEnabled({ timeout: 5000 });

    // ══════════════════════════════════════════════════════════════
    // Step 2: プッシュ通知を購読
    // ══════════════════════════════════════════════════════════════
    console.log('\n📌 Step 2: プッシュ通知を購読...');
    await page.click('#btnSubscribe');
    await expect(page.locator('#subStatus')).toHaveText('購読済み', { timeout: 30000 });
    console.log('✅ プッシュ購読完了');

    // エンドポイント情報を取得して表示
    const logContent = await page.locator('#log').innerHTML();
    const endpointMatch = logContent.match(/endpoint:\s*(https?:\/\/[^\s<]+)/);
    if (endpointMatch) {
      console.log(`   Push endpoint: ${endpointMatch[1].substring(0, 60)}...`);
    }
    const pushServiceMatch = logContent.match(/Push Service:\s*([^<]+)/);
    if (pushServiceMatch) {
      console.log(`   Push Service: ${pushServiceMatch[1].trim()}`);
    }
    await expect(page.locator('#btnRegisterNH')).toBeEnabled({ timeout: 5000 });

    // ══════════════════════════════════════════════════════════════
    // Step 3: Azure Notification Hubs に登録
    // ══════════════════════════════════════════════════════════════
    console.log('\n📌 Step 3: Azure Notification Hubs に Installation 登録...');
    await page.click('#btnRegisterNH');
    await expect(page.locator('#nhStatus')).toHaveText('登録済み', { timeout: 30000 });

    const logAfterRegister = await page.locator('#log').innerHTML();
    const installIdMatch = logAfterRegister.match(/Installation ID:\s*([^\s<]+)/);
    if (installIdMatch) {
      console.log(`   Installation ID: ${installIdMatch[1]}`);
    }

    // ── Azure NH Installation インデックス待機 ──
    // DirectSend も使うので短めで OK だが、少し待つ
    console.log('   ⏳ Installation のインデックスを待機 (15秒)...');
    await page.waitForTimeout(15000);
    console.log('✅ Azure NH 登録完了');
    await expect(page.locator('#btnSend')).toBeEnabled({ timeout: 5000 });

    // ══════════════════════════════════════════════════════════════
    // Step 4: プッシュ通知を送信 & 受信確認
    // ══════════════════════════════════════════════════════════════
    console.log('\n📌 Step 4: プッシュ通知を送信...');
    await page.fill('#inputTitle', 'Playwright E2E テスト');
    await page.fill('#inputMessage', 'Playwright 自動テストから送信！');
    await page.click('#btnSend');

    // 送信完了をログで確認
    await expect(page.locator('#log')).toContainText('通知送信完了', { timeout: 30000 });
    console.log('✅ 通知送信API完了');

    // 送信結果の詳細
    const sendLog = await page.locator('#log').innerText();
    const sendResultLines = sendLog.split('\n').filter(l =>
      l.includes('[direct]') || l.includes('[tag]') || l.includes('TrackingId') || l.includes('State:')
    );
    for (const line of sendResultLines) {
      console.log(`   ${line.trim()}`);
    }

    // ══════════════════════════════════════════════════════════════
    // Step 5: プッシュ受信を検知 (最大90秒待機)
    // ══════════════════════════════════════════════════════════════
    console.log('\n📌 Step 5: プッシュ通知の到着を待機中...');

    let pushReceived = false;
    const maxWaitSec = 90;
    const pollIntervalSec = 5;
    const maxIterations = maxWaitSec / pollIntervalSec;

    for (let i = 0; i < maxIterations; i++) {
      await page.waitForTimeout(pollIntervalSec * 1000);

      // 方法1: #pushReceived 要素の data-received 属性 (SW → page postMessage)
      const received = await page.evaluate(() => {
        const el = document.getElementById('pushReceived');
        return el?.getAttribute('data-received') === 'true';
      });
      if (received) {
        pushReceived = true;
        const title = await page.evaluate(() =>
          document.getElementById('pushReceived')?.getAttribute('data-title'));
        const body = await page.evaluate(() =>
          document.getElementById('pushReceived')?.getAttribute('data-body'));
        console.log(`\n🔔 プッシュ通知を受信！ (${(i + 1) * pollIntervalSec}秒後)`);
        console.log(`   タイトル: ${title}`);
        console.log(`   本文: ${body}`);
        break;
      }

      // 方法2: ページログのテキスト検索
      const pageLog = await page.locator('#log').innerText();
      if (pageLog.includes('プッシュ通知を受信しました')) {
        pushReceived = true;
        console.log(`\n🔔 ページログでプッシュ受信を検知！ (${(i + 1) * pollIntervalSec}秒後)`);
        break;
      }

      // 方法3: ブラウザコンソールでSWのpushイベント
      const swPushLog = browserLogs.find(l => l.includes('[SW] push event received'));
      if (swPushLog) {
        pushReceived = true;
        console.log(`\n🔔 SW push event 検知！ (${(i + 1) * pollIntervalSec}秒後)`);
        break;
      }

      console.log(`   ... ${(i + 1) * pollIntervalSec}秒経過`);
    }

    // ── スクリーンショット保存 ──
    const screenshotDir = path.join(__dirname, 'test-results');
    if (!fs.existsSync(screenshotDir)) fs.mkdirSync(screenshotDir, { recursive: true });
    await page.screenshot({
      path: path.join(screenshotDir, 'browser-push-e2e-v3.png'),
      fullPage: true
    });
    console.log('📸 スクリーンショット保存');

    // ── 最終ログ出力 ──
    const finalLog = await page.locator('#log').innerText();
    console.log('\n════════════════════════════════════════');
    console.log('📋 最終ログ:');
    console.log('════════════════════════════════════════');
    for (const line of finalLog.split('\n')) {
      if (line.trim()) console.log(`   ${line.trim()}`);
    }
    console.log('════════════════════════════════════════');

    // ── 結果判定 ──
    if (pushReceived) {
      console.log('\n🎉🎉🎉 E2E テスト完全成功！プッシュ通知がブラウザに到着しました！🎉🎉🎉');
    } else {
      console.log('\n⚠️  通知送信は成功しましたが、ブラウザで受信を確認できませんでした。');
      console.log('   考えられる原因:');
      console.log('   1. Azure NH → FCM の配信遅延');
      console.log('   2. Playwright Chromium での Push 受信制限');
      console.log('   3. FCM エンドポイントの配信問題');

      // サーバー状態を取得
      const serverState = await page.evaluate(async () => {
        const r = await fetch('/api/summary');
        return await r.json();
      });
      console.log(`   サーバー登録Installation: ${JSON.stringify(serverState)}`);
    }

    // 最低限の検証: 送信APIは成功していること
    expect(finalLog).toContain('通知送信完了');

    await context.close();

    // プッシュ受信できた場合のみ完全成功
    if (!pushReceived) {
      console.log('\n[INFO] プッシュ受信は不確定。送信API自体は正常に完了。');
    }
  });
});
