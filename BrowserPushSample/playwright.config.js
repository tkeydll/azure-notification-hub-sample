// @ts-check
const { defineConfig } = require('@playwright/test');

module.exports = defineConfig({
  testDir: '.',
  testMatch: '*.spec.js',
  timeout: 300_000,
  expect: { timeout: 30_000 },
  use: {
    baseURL: 'http://localhost:5285',
    // headed モードで実行（ブラウザを表示）
    headless: false,
    // Chromium を使用
    browserName: 'chromium',
    // スクリーンショット
    screenshot: 'on',
    trace: 'on',
  },
  // レポーター
  reporter: [['list']],
  // 出力ディレクトリ
  outputDir: 'test-results/',
});
