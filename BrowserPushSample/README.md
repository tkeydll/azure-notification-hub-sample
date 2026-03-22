# Browser Push Notification Sample

Azure Notification Hubs を使ったブラウザプッシュ通知 (Web Push) の E2E サンプルです。

## アーキテクチャ

```mermaid
graph TB
    subgraph "👤 クライアント（ブラウザ）"
        Browser["🌐 ブラウザ\n(Chrome / Edge)"]
        SW["⚙️ Service Worker\n(sw.js)"]
        PushAPI["📡 Push API\n(PushManager)"]
    end

    subgraph "🖥️ アプリケーションサーバー"
        WebApp["🔧 ASP.NET Web App\n(BrowserPushWeb)"]
    end

    subgraph "☁️ Azure"
        NH["🔔 Azure Notification Hubs"]
        VAPID["🔑 VAPID 鍵ペア\nPublic Key + Private Key\n+ Subject"]
    end

    subgraph "🌍 Google"
        FCM["📨 FCM\n(Firebase Cloud Messaging)"]
    end

    Browser -->|"① SW登録"| SW
    SW -->|"② subscribe(VAPID公開鍵)"| PushAPI
    PushAPI -->|"③ 購読リクエスト"| FCM
    FCM -->|"④ Push Endpoint URL"| PushAPI
    PushAPI -->|"⑤ PushSubscription"| Browser
    Browser -->|"⑥ POST /api/register"| WebApp
    WebApp -->|"⑦ PUT /installations"| NH
    WebApp -->|"⑧ POST /api/send"| NH
    NH -->|"⑨ VAPID署名 + Web Push暗号化"| FCM
    FCM -->|"⑩ HTTP/2 Push"| SW
    SW -->|"⑪ showNotification()"| Browser

    NH -.->|"署名に使用"| VAPID

    style NH fill:#0078d4,color:#fff,stroke:#005a9e
    style FCM fill:#ff9800,color:#fff,stroke:#f57c00
    style WebApp fill:#28a745,color:#fff,stroke:#1e7e34
    style VAPID fill:#e94560,color:#fff,stroke:#c73e54
```

### 登録フェーズ（①〜⑦）

| # | 区間 | プロトコル | 内容 |
|---|------|-----------|------|
| ① | ブラウザ → Service Worker | ローカル | `navigator.serviceWorker.register('/sw.js')` |
| ② | SW → Push API | ローカル | `PushManager.subscribe({ applicationServerKey })` |
| ③ | ブラウザ → FCM | HTTPS | Google FCM に購読を登録 |
| ④ | FCM → ブラウザ | HTTPS | Push Endpoint URL を返却 (`https://fcm.googleapis.com/fcm/send/...`) |
| ⑤ | Push API → ブラウザ | ローカル | `PushSubscription { endpoint, keys: { p256dh, auth } }` |
| ⑥ | ブラウザ → WebApp | HTTP | PushSubscription をサーバーに POST |
| ⑦ | WebApp → Azure NH | HTTPS (SAS認証) | `BrowserInstallation` として登録 |

### 送信フェーズ（⑧〜⑪）

| # | 区間 | プロトコル | 内容 |
|---|------|-----------|------|
| ⑧ | WebApp → Azure NH | HTTPS (SAS認証) | `SendNotificationAsync()` でペイロード送信 |
| ⑨ | Azure NH → FCM | HTTPS | VAPID Private Key で JWT 署名 + RFC 8291 でペイロード暗号化 |
| ⑩ | FCM → Service Worker | HTTP/2 Push | FCM がブラウザへの常時接続経由で配信 |
| ⑪ | SW → ブラウザ | ローカル | `showNotification()` で通知表示 + `postMessage()` でページに通知 |

### 各コンポーネントの役割

| コンポーネント | 役割 |
|---------------|------|
| **Azure Notification Hubs** | VAPID 署名、Web Push 暗号化（RFC 8291）、デバイス管理、タグベースルーティング。アプリ側は FCM を意識しなくてよい |
| **FCM** | ブラウザとの持続接続を維持するプッシュリレー。Chromium 系ブラウザ (Chrome, Edge) は全て FCM を経由する |
| **Service Worker** | バックグラウンドで push イベントを受信し、通知を表示する |
| **VAPID** | Voluntary Application Server Identification。サーバーの身元を証明する鍵ペア。Subject は `mailto:` URL が必須 |

### ネットワーク要件

| 通信経路 | ポート | ホスト | 用途 |
|---------|--------|--------|------|
| WebApp → Azure NH | 443 | `*.servicebus.windows.net` | Installation 登録・通知送信 |
| Azure NH → FCM | 443 | `fcm.googleapis.com` | Web Push Protocol でプッシュ配信 |
| ブラウザ → FCM | 443 | `*.google.com`, `mtalk.google.com:5228` | 購読登録 + プッシュ受信の常時接続 |
| ブラウザ → WebApp | 5285 | `localhost` | API 通信（開発時） |

## プロジェクト構成

```
BrowserPushSample/
├── BrowserPushSample/          # コンソールアプリ（SDK 単体テスト用）
│   └── Program.cs              #   7ステップの登録→送信フロー
├── BrowserPushWeb/             # ASP.NET Web アプリ（E2E テスト用）
│   ├── Program.cs              #   API サーバー + SDK バグ回避ハンドラー
│   └── wwwroot/
│       ├── index.html          #   4ステップ UI (SW登録→購読→NH登録→送信)
│       └── sw.js               #   Service Worker (push受信→通知表示)
├── browser-push.spec.js        # Playwright E2E テスト（Azure NH 経由）
├── direct-push.spec.js         # Playwright 直接テスト（web-push ライブラリ経由）
└── playwright.config.js        # Playwright 設定
```

## セットアップ

### 前提条件

- .NET SDK 6.0 以上
- Node.js 18 以上
- Azure Notification Hub（Browser (Web Push) が有効化済み）
- VAPID 鍵ペアが Hub に設定済み

### 1. 設定

`BrowserPushWeb/appsettings.json` に接続文字列とハブ名を設定:

```json
{
  "NotificationHub": {
    "ConnectionString": "Endpoint=sb://YOUR-NS.servicebus.windows.net/;SharedAccessKeyName=DefaultFullSharedAccessSignature;SharedAccessKey=YOUR-KEY",
    "HubName": "your-hub-name"
  }
}
```

### 2. Web アプリの起動

```bash
cd BrowserPushWeb
dotnet run
```

ブラウザで `http://localhost:5285` を開き、ステップ 1〜4 を順に実行。

### 3. Playwright E2E テストの実行

```bash
npm install
npx playwright install chromium
npx playwright test browser-push.spec.js --headed
```

## 発見した SDK の既知の問題

このサンプルの開発中に発見した Azure Notification Hubs .NET SDK のバグ:

| # | 問題 | 影響 | 回避策 |
|---|------|------|--------|
| 1 | `BrowserInstallation` の `pushChannel` が JSON オブジェクトではなく JSON 文字列として直列化される | `PUT /installations` が 400 エラー | `BrowserPushChannelFixHandler` (DelegatingHandler) でリクエスト本文をインターセプトして修正 |
| 2 | `GetInstallationAsync()` で `pushChannel` の逆直列化に失敗する | Installation の取得不可 | 回避不可（使用しない） |
| 3 | `SendDirectNotificationAsync` + `EnableTestSend` でレスポンスの逆直列化エラー ("Root element is missing") | 直接送信の結果取得不可 | タグベース送信 (`SendNotificationAsync`) を使用 |

### VAPID Subject に関する注意

Azure Notification Hub の BrowserCredential に設定する **Subject** は、Web Push の仕様 (RFC 8292) に従い `mailto:` または `https://` で始まる URL でなければなりません。不正な値（例: `"test"`）を設定すると、FCM が JWT を拒否し、全ての通知配信が失敗します。

```
❌ Subject: "test"           → 全配信失敗 (unknown error)
✅ Subject: "mailto:you@example.com"  → 正常に配信
```

## Wireshark によるネットワーク実測

Playwright E2E テスト実行時に Wireshark でキャプチャした実測データです。

### キャプチャで確認できた通信フロー

```
ブラウザ (Chrome)              アプリサーバー              Azure NH                    FCM
     │                              │                         │                         │
     │ ① PushManager.subscribe()   │                         │                         │
     │─────────────────────────────────────────────────────────────────────────────────→│
     │  signaler-pa.googleapis.com:443 (TLSv1.3)              │                         │
     │  フレーム #824  (t=27.8s)    │                         │       endpoint 発行      │
     │ ←───────────────────────────────────────────────────────────────────────────────│
     │  endpoint: https://jmt17.google.com/fcm/send/...        │                         │
     │                              │                         │                         │
     │ ② endpoint をサーバーへ送信  │                         │                         │
     │ ────────────────────────────→│                         │                         │
     │  POST /api/register          │                         │                         │
     │                              │                         │                         │
     │                              │ ③ Installation 登録     │                         │
     │                              │────────────────────────→│                         │
     │                              │  PUT /installations/... (TLSv1.3)                 │
     │                              │  jns-notification-hub-ns.servicebus.windows.net   │
     │                              │  フレーム #1691 (t=43.4s)│                         │
     │                              │←────────────────────────│                         │
     │                              │  200 OK                  │                         │
     │                              │                         │                         │
     │ ④ mtalk 常時接続確立         │                         │                         │
     │─────────────────────────────────────────────────────────────────────────────────→│
     │  mtalk.google.com:5228 (TLSv1.3)                       │    Chrome が待ち受け開始 │
     │  フレーム #1909 (t=47.6s)    │                         │                         │
     │                              │                         │                         │
     │                              │ ⑤ 通知送信リクエスト    │                         │
     │                              │────────────────────────→│                         │
     │                              │  POST /messages (TLSv1.3)│                         │
     │                              │  フレーム #2376 (t=51.5s)│                         │
     │                              │←────────────────────────│                         │
     │                              │  201 Created             │                         │
     │                              │                         │                         │
     │                              │                         │ ⑥ FCM に Web Push 送信  │
     │                              │                         │────────────────────────→│
     │                              │                         │  fcm.googleapis.com      │
     │                              │                         │  ※クラウド内・キャプチャ不可│
     │                              │                         │                         │
     │ ⑦ プッシュ受信               │                         │                         │
     │ ←───────────────────────────────────────────────────────────────────────────────│
     │  mtalk.google.com:5228 から着信                         │                         │
     │  フレーム #2753 (t=68.1s)    │                         │                         │
     │  Service Worker → push イベント → showNotification()   │                         │
```

### 確認できたフレーム一覧

| フレーム | 経過時間 | 接続先 | SNI / 内容 |
|---------|---------|--------|-----------|
| #824 | t=27.8s | `signaler-pa.googleapis.com:443` | **TLS Client Hello** → Push endpoint 取得 |
| #1691 | t=43.4s | `jns-notification-hub-ns.servicebus.windows.net:443` | **TLS Client Hello** → Installation 登録（PUT）|
| #1703 | t=43.5s | 同上 | **200 OK** レスポンス |
| #2376 | t=51.5s | `jns-notification-hub-ns.servicebus.windows.net:443` | **TLS Client Hello** → 通知送信（POST /messages）|
| #2399 | t=51.6s | 同上 | **201 Created** レスポンス |
| #1909 | t=47.6s | `mtalk.google.com:5228` | **TLS Client Hello** → FCM 常時接続確立 |
| **#2753** | **t=68.1s** | `mtalk.google.com:5228` | **← Application Data（通知着信）** |

### ホスト別の役割

| ホスト | ポート | キャプチャ可否 | 役割 |
|--------|--------|--------------|------|
| `signaler-pa.googleapis.com` | 443 | ✅ ローカルで可 | `PushManager.subscribe()` 時に Push endpoint を発行 |
| `jns-notification-hub-ns.servicebus.windows.net` | 443 | ✅ ローカルで可 | Azure NH への Installation 登録・通知送信 |
| `fcm.googleapis.com` | 443 | ❌ クラウド内のみ | Azure NH が FCM に Web Push を送信（クラウド内通信） |
| `mtalk.google.com` | 5228 | ✅ ローカルで可 | Chrome が FCM から通知を受け取る常時接続 |

> **補足**: `fcm.googleapis.com` への送信は Azure NH のクラウド内で行われるため、ローカルの Wireshark では直接見えません。ローカルでキャプチャできるのは **ブラウザが FCM から受け取る側** (`mtalk.google.com:5228`) への常時接続のみです。
