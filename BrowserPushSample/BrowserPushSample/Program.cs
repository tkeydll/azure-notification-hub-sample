// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Azure.NotificationHubs;
using Newtonsoft.Json.Linq;

namespace BrowserPushSample
{
    /// <summary>
    /// Browser Push の仕組みとネットワーク要件を理解するための詳細ログ付きサンプル。
    ///
    /// === ブラウザプッシュのアーキテクチャ ===
    ///
    /// [ブラウザ(Service Worker)]
    ///    │  ① PushManager.subscribe() で PushSubscription を取得
    ///    │     - endpoint: プッシュサービスURL (例: https://fcm.googleapis.com/fcm/send/...)
    ///    │     - p256dh: クライアントの公開鍵 (ECDH P-256)
    ///    │     - auth: 認証シークレット (16バイト)
    ///    ▼
    /// [アプリケーションサーバー (このサンプル)]
    ///    │  ② Azure Notification Hubs に Installation/Registration を登録
    ///    │  ③ Azure Notification Hubs に通知送信リクエスト
    ///    ▼
    /// [Azure Notification Hubs]
    ///    │  ④ VAPID 認証を使って Push Service にリクエスト
    ///    │     - RFC 8292 (VAPID) による JWT トークン生成
    ///    │     - RFC 8291 (Web Push Encryption) によるペイロード暗号化
    ///    ▼
    /// [Push Service (FCM/Mozilla Push/Edge Push 等)]
    ///    │  ⑤ Push Service がブラウザに通知を配信
    ///    ▼
    /// [ブラウザ(Service Worker)]
    ///    ⑥ push イベントで通知を受信、showNotification() で表示
    ///
    /// === ネットワーク要件 ===
    /// 
    /// 1. アプリサーバー → Azure Notification Hubs
    ///    - HTTPS (443) で *.servicebus.windows.net に接続
    ///    - SAS トークンで認証
    ///
    /// 2. Azure Notification Hubs → Push Service
    ///    - HTTPS (443) で各ブラウザの Push Service に接続
    ///    - FCM: fcm.googleapis.com
    ///    - Mozilla: updates.push.services.mozilla.com
    ///    - Edge: wns2-par02p.notify.windows.com (等)
    ///    - VAPID JWT + Web Push Encryption
    ///
    /// 3. Push Service → ブラウザ
    ///    - 常時接続 (WebSocket/HTTP2 persistent connection)
    ///    - ブラウザ側でファイアウォールがPush Serviceへの接続をブロックしないこと
    /// </summary>
    class Program
    {
        static async Task Main(string[] args)
        {
            Console.OutputEncoding = Encoding.UTF8;
            Console.WriteLine("╔══════════════════════════════════════════════════════════════╗");
            Console.WriteLine("║   Azure Notification Hubs - Browser Push (Web Push) Sample  ║");
            Console.WriteLine("║   詳細ログ付き: 仕組みとネットワーク要件の理解用             ║");
            Console.WriteLine("╚══════════════════════════════════════════════════════════════╝");
            Console.WriteLine();

            // ── 設定読み込み ──
            var config = LoadConfig();

            LogSection("STEP 0: ブラウザプッシュのアーキテクチャ概要");
            PrintArchitectureOverview();

            // ── HTTP通信のログを取るための DelegatingHandler を注入 ──
            // BrowserPushChannelFixHandler: SDK の BrowserInstallation が pushChannel を
            //   JSON文字列(エスケープ済み)で送信するバグを回避し、JSONオブジェクトに変換する
            // HttpLoggingHandler: 全HTTP通信の詳細をコンソールに出力する
            var loggingHandler = new HttpLoggingHandler(
                new BrowserPushChannelFixHandler(new HttpClientHandler()));
            var settings = new NotificationHubSettings
            {
                MessageHandler = loggingHandler
            };

            LogSection("STEP 1: Notification Hub クライアント作成");
            Console.WriteLine($"  接続先 Namespace: {ExtractNamespace(config.ConnectionString)}");
            Console.WriteLine($"  Hub名: {config.HubName}");
            Console.WriteLine();
            Console.WriteLine("  [ネットワーク要件]");
            Console.WriteLine("  - HTTPS (443) → *.servicebus.windows.net");
            Console.WriteLine("  - 認証: SharedAccessSignature (SAS Token)");
            Console.WriteLine();

            // MessageHandler を注入するために settings 付きコンストラクタを使用
            // (EnableTestSend はファクトリメソッド経由でしか設定できないため、
            //  HTTP ログと NotificationDetails で詳細を確認する)
            var nhClient = new NotificationHubClient(config.ConnectionString, config.HubName, settings);

            // ── STEP 2: Hub情報の取得とBrowserCredentialの確認 ──
            LogSection("STEP 2: Hub の Browser (Web Push) 資格情報を確認");
            Console.WriteLine("  NamespaceManager を使って Hub の設定を取得します");
            Console.WriteLine("  >> GET https://<namespace>.servicebus.windows.net/<hub>?api-version=2017-04");
            Console.WriteLine();
            var namespaceManager = NamespaceManager.CreateFromConnectionString(config.ConnectionString);
            try
            {
                var hub = await namespaceManager.GetNotificationHubAsync(config.HubName);
                Console.WriteLine($"  Hub パス: {hub.Path}");

                if (hub.BrowserCredential != null)
                {
                    Console.WriteLine($"  ✓ BrowserCredential が設定済み");
                    Console.WriteLine($"    VAPID Subject: {hub.BrowserCredential.Subject}");
                    Console.WriteLine($"    VAPID Public Key: {Truncate(hub.BrowserCredential.VapidPublicKey, 20)}...");
                    Console.WriteLine($"    VAPID Private Key: [設定済み - セキュリティのため非表示]");
                    Console.WriteLine();
                    Console.WriteLine("    [VAPID (Voluntary Application Server Identification) とは]");
                    Console.WriteLine("    - RFC 8292 で規定");
                    Console.WriteLine("    - サーバーが Push Service に自身を証明するための仕組み");
                    Console.WriteLine("    - 公開鍵: ブラウザ側の subscribe() に渡す");
                    Console.WriteLine("    - 秘密鍵: サーバー側で JWT 署名に使用");
                    Console.WriteLine("    - Subject: 連絡先 (mailto: or https://)");
                }
                else
                {
                    Console.WriteLine("  ✗ BrowserCredential 未設定!");
                    Console.WriteLine("    Azure Portal > Notification Hub > Settings > Browser (Web Push) で設定してください");
                    Console.WriteLine("    必要な情報: VAPID Subject, Public Key, Private Key");
                }

                // 他のPNSの状態も確認
                Console.WriteLine();
                Console.WriteLine("  [Hub に設定されている PNS 資格情報]");
                Console.WriteLine($"    APNS:   {(hub.ApnsCredential != null ? "✓" : "✗")}");
                Console.WriteLine($"    FCM:    {(hub.FcmCredential != null ? "✓" : "✗")}");
                Console.WriteLine($"    FCMv1:  {(hub.FcmV1Credential != null ? "✓" : "✗")}");
                Console.WriteLine($"    WNS:    {(hub.WnsCredential != null ? "✓" : "✗")}");
                Console.WriteLine($"    Browser:{(hub.BrowserCredential != null ? "✓" : "✗")}");
                Console.WriteLine($"    ADM:    {(hub.AdmCredential != null ? "✓" : "✗")}");
                Console.WriteLine($"    Baidu:  {(hub.BaiduCredential != null ? "✓" : "✗")}");
            }
            catch (Exception ex)
            {
                LogError("Hub情報の取得に失敗", ex);
            }

            // ── STEP 3: Browser Installation の登録 ──
            LogSection("STEP 3: Browser Installation の登録");
            Console.WriteLine("  [実際の運用では]");
            Console.WriteLine("  ブラウザの PushManager.subscribe() の戻り値 (PushSubscription) から取得:");
            Console.WriteLine("    endpoint → BrowserPushSubscription.Endpoint");
            Console.WriteLine("    getKey('p256dh') → BrowserPushSubscription.P256DH (Base64URL)");
            Console.WriteLine("    getKey('auth')   → BrowserPushSubscription.Auth (Base64URL)");
            Console.WriteLine();

            var browserPushSubscription = new BrowserPushSubscription
            {
                Endpoint = config.TestEndpoint,
                P256DH = config.TestP256DH,
                Auth = config.TestAuth
            };

            var installationId = $"browser-push-sample-{Guid.NewGuid():N}".Substring(0, 36);

            Console.WriteLine($"  Installation ID: {installationId}");
            Console.WriteLine($"  Endpoint: {browserPushSubscription.Endpoint}");
            Console.WriteLine($"  P256DH: {Truncate(browserPushSubscription.P256DH, 20)}...");
            Console.WriteLine($"  Auth: {Truncate(browserPushSubscription.Auth, 10)}...");
            Console.WriteLine();

            Console.WriteLine("  [Endpoint URL の構造]");
            if (Uri.TryCreate(browserPushSubscription.Endpoint, UriKind.Absolute, out var endpointUri))
            {
                Console.WriteLine($"    スキーム: {endpointUri.Scheme}");
                Console.WriteLine($"    ホスト: {endpointUri.Host}");
                Console.WriteLine($"    ポート: {endpointUri.Port}");
                Console.WriteLine($"    パス: {endpointUri.AbsolutePath}");
                Console.WriteLine();
                Console.WriteLine("    [Push Service の判定]");
                if (endpointUri.Host.Contains("fcm.googleapis.com"))
                    Console.WriteLine("    → Google FCM (Chrome, Edge Chromium等)");
                else if (endpointUri.Host.Contains("mozilla.com") || endpointUri.Host.Contains("push.services.mozilla.com"))
                    Console.WriteLine("    → Mozilla Push Service (Firefox)");
                else if (endpointUri.Host.Contains("notify.windows.com"))
                    Console.WriteLine("    → Windows Push Notification Service (Edge Legacy)");
                else
                    Console.WriteLine($"    → 不明な Push Service: {endpointUri.Host}");
            }
            Console.WriteLine();

            var installation = new BrowserInstallation(installationId, browserPushSubscription);
            installation.Tags = new List<string> { "browser", "test", "web-push-sample" };

            Console.WriteLine("  >> Azure Notification Hubs への HTTP リクエスト送信中...");
            Console.WriteLine($"    PUT https://<namespace>.servicebus.windows.net/<hub>/installations/{installationId}?api-version=2020-06");
            Console.WriteLine();

            var sw = Stopwatch.StartNew();
            try
            {
                await nhClient.CreateOrUpdateInstallationAsync(installation);
                sw.Stop();
                Console.WriteLine($"  ✓ Installation 登録成功 (所要時間: {sw.ElapsedMilliseconds}ms)");
            }
            catch (Exception ex)
            {
                sw.Stop();
                LogError($"Installation 登録失敗 (所要時間: {sw.ElapsedMilliseconds}ms)", ex);
            }

            // ── STEP 4: Installation の取得で確認 ──
            LogSection("STEP 4: 登録した Installation を取得して確認");
            Console.WriteLine($"  >> GET https://<namespace>.servicebus.windows.net/<hub>/installations/{installationId}?api-version=2020-06");
            Console.WriteLine();

            sw.Restart();
            try
            {
                var retrieved = await nhClient.GetInstallationAsync(installationId);
                sw.Stop();
                Console.WriteLine($"  ✓ Installation 取得成功 (所要時間: {sw.ElapsedMilliseconds}ms)");
                Console.WriteLine($"    InstallationId: {retrieved.InstallationId}");
                Console.WriteLine($"    Platform: {retrieved.Platform}");
                Console.WriteLine($"    PushChannel: {Truncate(retrieved.PushChannel, 60)}...");
                Console.WriteLine($"    Tags: {string.Join(", ", retrieved.Tags ?? Array.Empty<string>())}");
                Console.WriteLine($"    ExpirationTime: {retrieved.ExpirationTime}");
                Console.WriteLine($"    PushChannelExpired: {retrieved.PushChannelExpired}");

                // PushChannel を解析
                Console.WriteLine();
                Console.WriteLine("  [PushChannel の内部構造 (JSON)]");
                try
                {
                    var parsed = JsonSerializer.Deserialize<BrowserPushSubscription>(retrieved.PushChannel);
                    Console.WriteLine($"    endpoint: {parsed?.Endpoint}");
                    Console.WriteLine($"    p256dh: {Truncate(parsed?.P256DH ?? "", 20)}...");
                    Console.WriteLine($"    auth: {Truncate(parsed?.Auth ?? "", 10)}...");
                }
                catch
                {
                    Console.WriteLine($"    (パース失敗: {Truncate(retrieved.PushChannel, 80)})");
                }
            }
            catch (Exception ex)
            {
                sw.Stop();
                LogError($"Installation 取得失敗 (所要時間: {sw.ElapsedMilliseconds}ms)", ex);
            }

            // ── STEP 5: Browser 通知の送信 ──
            LogSection("STEP 5: Browser Push 通知の送信");
            Console.WriteLine("  [Web Push 通知のペイロード]");
            Console.WriteLine("  - JSON 形式");
            Console.WriteLine("  - ブラウザの Service Worker が受信する");
            Console.WriteLine("  - 暗号化: RFC 8291 (Message Encryption for Web Push)");
            Console.WriteLine("  - Content-Encoding: aes128gcm");
            Console.WriteLine();

            var payload = JsonSerializer.Serialize(new
            {
                title = "Azure Notification Hubs",
                message = "Browser Push テスト通知です！",
                icon = "https://azure.microsoft.com/favicon.ico",
                timestamp = DateTime.UtcNow.ToString("o"),
                data = new
                {
                    url = "https://portal.azure.com",
                    source = "BrowserPushSample"
                }
            });

            Console.WriteLine($"  ペイロード:");
            Console.WriteLine($"    {payload}");
            Console.WriteLine();

            // タグ指定で送信
            var tagExpression = "browser";
            Console.WriteLine($"  送信方法: タグ式 \"{tagExpression}\" を使用");
            Console.WriteLine("  >> POST https://<namespace>.servicebus.windows.net/<hub>/messages?api-version=2020-06");
            Console.WriteLine("     ServiceBusNotification-Format: browser");
            Console.WriteLine($"     ServiceBusNotification-Tags: {tagExpression}");
            Console.WriteLine();

            sw.Restart();
            try
            {
                var notification = new BrowserNotification(payload);
                var outcome = await nhClient.SendNotificationAsync(notification, tagExpression);
                sw.Stop();

                Console.WriteLine($"  ✓ 通知送信完了 (所要時間: {sw.ElapsedMilliseconds}ms)");
                Console.WriteLine($"    TrackingId: {outcome.TrackingId}");
                Console.WriteLine($"    NotificationId: {outcome.NotificationId}");
                Console.WriteLine($"    State: {outcome.State}");

                if (outcome.Results != null)
                {
                    Console.WriteLine($"    Results ({outcome.Results.Count} 件):");
                    foreach (var result in outcome.Results)
                    {
                        Console.WriteLine($"      - ApplicationPlatform: {result.ApplicationPlatform}");
                        Console.WriteLine($"        PnsHandle: {Truncate(result.PnsHandle, 40)}");
                        Console.WriteLine($"        RegistrationId: {result.RegistrationId}");
                        Console.WriteLine($"        Outcome: {result.Outcome}");
                    }
                }

                // 送信結果の詳細を取得
                if (!string.IsNullOrEmpty(outcome.NotificationId))
                {
                    Console.WriteLine();
                    Console.WriteLine("  >> 送信結果の詳細を取得中...");
                    await Task.Delay(2000); // 結果がまだ処理中の場合があるため少し待つ

                    try
                    {
                        var details = await nhClient.GetNotificationOutcomeDetailsAsync(outcome.NotificationId);
                        Console.WriteLine($"    NotificationId: {details.NotificationId}");
                        Console.WriteLine($"    State: {details.State}");
                        Console.WriteLine($"    EnqueueTime: {details.EnqueueTime}");
                        Console.WriteLine($"    StartTime: {details.StartTime}");
                        Console.WriteLine($"    EndTime: {details.EndTime}");
                        Console.WriteLine($"    TargetPlatforms: {details.TargetPlatforms}");

                        if (details.PnsErrorDetailsUri != null)
                        {
                            Console.WriteLine($"    PnsErrorDetailsUri: {details.PnsErrorDetailsUri}");
                        }

                        // プラットフォーム別の結果
                        Console.WriteLine($"    [プラットフォーム別 Outcome Counts]");
                        PrintOutcomeCounts("    APNS", details.ApnsOutcomeCounts);
                        PrintOutcomeCounts("    WNS", details.WnsOutcomeCounts);
                        PrintOutcomeCounts("    FCM", details.FcmOutcomeCounts);
                        PrintOutcomeCounts("    FCMv1", details.FcmV1OutcomeCounts);
                        PrintOutcomeCounts("    ADM", details.AdmOutcomeCounts);
                        // Browser の OutcomeCounts は現時点で専用プロパティがない場合がある
                        Console.WriteLine($"    Browser: (Notification Details の PnsErrorDetailsUri を確認)");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"    ⚠ 詳細取得失敗: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                sw.Stop();
                LogError($"通知送信失敗 (所要時間: {sw.ElapsedMilliseconds}ms)", ex);
            }

            // ── STEP 6: ブロードキャスト送信 ──
            LogSection("STEP 6: Browser Push ブロードキャスト送信");
            Console.WriteLine("  タグ指定なしの全デバイスへの送信テスト");
            Console.WriteLine();

            sw.Restart();
            try
            {
                var broadcastPayload = JsonSerializer.Serialize(new
                {
                    title = "ブロードキャスト",
                    message = "全ブラウザデバイスへの通知テスト",
                    timestamp = DateTime.UtcNow.ToString("o")
                });

                var broadcastNotification = new BrowserNotification(broadcastPayload);
                var broadcastOutcome = await nhClient.SendNotificationAsync(broadcastNotification);
                sw.Stop();

                Console.WriteLine($"  ✓ ブロードキャスト送信完了 (所要時間: {sw.ElapsedMilliseconds}ms)");
                Console.WriteLine($"    TrackingId: {broadcastOutcome.TrackingId}");
                Console.WriteLine($"    NotificationId: {broadcastOutcome.NotificationId}");
                Console.WriteLine($"    State: {broadcastOutcome.State}");

                if (broadcastOutcome.Results != null)
                {
                    foreach (var result in broadcastOutcome.Results)
                    {
                        Console.WriteLine($"    - {result.ApplicationPlatform}: {result.Outcome} (PnsHandle: {Truncate(result.PnsHandle, 30)})");
                    }
                }
            }
            catch (Exception ex)
            {
                sw.Stop();
                LogError($"ブロードキャスト送信失敗 (所要時間: {sw.ElapsedMilliseconds}ms)", ex);
            }

            // ── STEP 7: Installation クリーンアップ ──
            LogSection("STEP 7: テスト用 Installation の削除");
            sw.Restart();
            try
            {
                await nhClient.DeleteInstallationAsync(installationId);
                sw.Stop();
                Console.WriteLine($"  ✓ Installation 削除完了 (所要時間: {sw.ElapsedMilliseconds}ms)");
            }
            catch (Exception ex)
            {
                sw.Stop();
                LogError($"Installation 削除失敗 (所要時間: {sw.ElapsedMilliseconds}ms)", ex);
            }

            // ── サマリー ──
            LogSection("ネットワーク通信サマリー");
            loggingHandler.PrintSummary();

            LogSection("ブラウザプッシュのネットワーク要件まとめ");
            PrintNetworkRequirements();

            Console.WriteLine();
            Console.WriteLine("サンプル実行完了。");
        }

        static void PrintArchitectureOverview()
        {
            Console.WriteLine(@"
  ┌─────────────────────┐
  │ ブラウザ             │
  │ (Service Worker)    │
  │                     │
  │ PushManager         │
  │  .subscribe({       │
  │    applicationServer│
  │    Key: VAPID公開鍵 │
  │  })                 │
  └─────────┬───────────┘
            │ PushSubscription
            │  { endpoint, p256dh, auth }
            ▼
  ┌─────────────────────┐     HTTPS (443)     ┌──────────────────────────┐
  │ アプリサーバー       │ ──────────────────→ │ Azure Notification Hubs  │
  │ (このサンプル)       │   REST API           │                          │
  │                     │   SAS Token認証      │  - Installation 管理     │
  │ Installation登録    │                      │  - 通知ルーティング       │
  │ 通知送信リクエスト   │                      │  - タグベースの配信       │
  └─────────────────────┘                      └────────────┬─────────────┘
                                                            │ HTTPS (443)
                                                            │ VAPID JWT認証
                                                            │ Web Push暗号化
                                                            ▼
                                               ┌──────────────────────────┐
                                               │ Push Service             │
                                               │                          │
                                               │ ・Chrome → FCM           │
                                               │   fcm.googleapis.com     │
                                               │ ・Firefox → Mozilla Push │
                                               │   push.services.mozilla..│
                                               │ ・Edge → WNS             │
                                               │   notify.windows.com     │
                                               └────────────┬─────────────┘
                                                            │ 常時接続
                                                            │ (HTTP/2, WebSocket)
                                                            ▼
                                               ┌──────────────────────────┐
                                               │ ブラウザ (Service Worker) │
                                               │                          │
                                               │ self.addEventListener(   │
                                               │   'push', (event) => {}  │
                                               │ )                        │
                                               └──────────────────────────┘
");
        }

        static void PrintNetworkRequirements()
        {
            Console.WriteLine(@"
  [必要なネットワーク接続]

  1. アプリサーバー → Azure Notification Hubs
     ┌──────────────────────────────────────────────────────────┐
     │ プロトコル: HTTPS (TLS 1.2+)                             │
     │ ポート: 443                                              │
     │ ホスト: *.servicebus.windows.net                         │
     │ 認証: SharedAccessSignature (SAS Token)                  │
     │ API: REST API (PUT/GET/POST/DELETE)                      │
     │ Content-Type: application/json / application/atom+xml    │
     └──────────────────────────────────────────────────────────┘

  2. Azure Notification Hubs → Push Service (ANH が内部で行う)
     ┌──────────────────────────────────────────────────────────┐
     │ プロトコル: HTTPS (TLS 1.2+)                             │
     │ ポート: 443                                              │
     │ ホスト (ブラウザごとに異なる):                              │
     │   Chrome/Edge: fcm.googleapis.com                        │
     │   Firefox: updates.push.services.mozilla.com             │
     │   Safari: (Web Push は macOS Ventura+ で対応)            │
     │ 認証: VAPID (RFC 8292) - JWT Bearer Token                │
     │ 暗号化: RFC 8291 (aes128gcm)                             │
     │ Content-Encoding: aes128gcm                              │
     │ TTL: 秒単位 (0 = 即時配信のみ)                            │
     └──────────────────────────────────────────────────────────┘

  3. Push Service → ブラウザ (ブラウザが管理)
     ┌──────────────────────────────────────────────────────────┐
     │ プロトコル: HTTPS + HTTP/2 Server Push / WebSocket       │
     │ 接続タイプ: 常時接続 (persistent connection)             │
     │ ブラウザが自動的に Push Service に接続を維持              │
     │ ファイアウォール要件:                                     │
     │   - *.googleapis.com:443 (Chrome)                        │
     │   - *.push.services.mozilla.com:443 (Firefox)            │
     │   - *.notify.windows.com:443 (Edge)                      │
     └──────────────────────────────────────────────────────────┘

  [ファイアウォール/プロキシ考慮事項]
  - WebSocket をブロックするプロキシがある場合、プッシュ受信に影響
  - SSL インスペクション (TLS 復号化) は Push Service の証明書ピンニングに干渉する可能性
  - VAPID の JWT は ES256 (ECDSA P-256) で署名
");
        }

        static AppConfig LoadConfig()
        {
            try
            {
                var json = System.IO.File.ReadAllText("appsettings.json");
                var doc = JsonDocument.Parse(json);

                var nh = doc.RootElement.GetProperty("NotificationHub");
                var bp = doc.RootElement.GetProperty("BrowserPush");

                return new AppConfig
                {
                    ConnectionString = nh.GetProperty("ConnectionString").GetString()!,
                    HubName = nh.GetProperty("HubName").GetString()!,
                    VapidSubject = bp.GetProperty("VapidSubject").GetString()!,
                    VapidPublicKey = bp.GetProperty("VapidPublicKey").GetString()!,
                    VapidPrivateKey = bp.GetProperty("VapidPrivateKey").GetString()!,
                    TestEndpoint = bp.GetProperty("TestEndpoint").GetString()!,
                    TestP256DH = bp.GetProperty("TestP256DH").GetString()!,
                    TestAuth = bp.GetProperty("TestAuth").GetString()!,
                };
            }
            catch (Exception ex)
            {
                Console.WriteLine($"設定ファイルの読み込みに失敗: {ex.Message}");
                Console.WriteLine("appsettings.json を正しく設定してください。");
                throw;
            }
        }

        static string ExtractNamespace(string connectionString)
        {
            var match = System.Text.RegularExpressions.Regex.Match(connectionString, @"Endpoint=sb://([^/;]+)");
            return match.Success ? match.Groups[1].Value : "(不明)";
        }

        static string Truncate(string value, int maxLength)
        {
            if (string.IsNullOrEmpty(value)) return "(empty)";
            return value.Length <= maxLength ? value : value.Substring(0, maxLength);
        }

        static void LogSection(string title)
        {
            Console.WriteLine();
            Console.WriteLine($"━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━");
            Console.WriteLine($"  {title}");
            Console.WriteLine($"━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━");
            Console.WriteLine();
        }

        static void LogError(string context, Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"  ✗ {context}");
            Console.WriteLine($"    例外型: {ex.GetType().Name}");
            Console.WriteLine($"    メッセージ: {ex.Message}");
            if (ex.InnerException != null)
            {
                Console.WriteLine($"    InnerException: {ex.InnerException.GetType().Name}: {ex.InnerException.Message}");
            }
            Console.ResetColor();
        }

        static void PrintOutcomeCounts(string label, NotificationOutcomeCollection? counts)
        {
            if (counts == null || counts.Count == 0)
            {
                Console.WriteLine($"{label}: (データなし)");
                return;
            }
            foreach (var kvp in counts)
            {
                Console.WriteLine($"{label} - {kvp.Key}: {kvp.Value}");
            }
        }
    }

    class AppConfig
    {
        public string ConnectionString { get; set; } = "";
        public string HubName { get; set; } = "";
        public string VapidSubject { get; set; } = "";
        public string VapidPublicKey { get; set; } = "";
        public string VapidPrivateKey { get; set; } = "";
        public string TestEndpoint { get; set; } = "";
        public string TestP256DH { get; set; } = "";
        public string TestAuth { get; set; } = "";
    }

    /// <summary>
    /// SDK の BrowserInstallation が pushChannel を JSON文字列 (二重エスケープ) で
    /// シリアライズする問題を HTTP レベルで修正する DelegatingHandler。
    ///
    /// 問題:
    ///   SDK: "pushChannel": "{\"endpoint\":\"...\",\"p256dh\":\"...\",\"auth\":\"...\"}"
    ///   (pushChannel が JSON 文字列)
    ///
    /// 期待:
    ///   Server: "pushChannel": {"endpoint":"...", "p256dh":"...", "auth":"..."}
    ///   (pushChannel が JSON オブジェクト)
    /// </summary>
    class BrowserPushChannelFixHandler : DelegatingHandler
    {
        public BrowserPushChannelFixHandler(HttpMessageHandler innerHandler) : base(innerHandler) { }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            // PUT /installations/ リクエストで platform=Browser の場合のみ修正
            if (request.Method == HttpMethod.Put
                && request.RequestUri?.AbsolutePath.Contains("/installations/") == true
                && request.Content != null)
            {
                var body = await request.Content.ReadAsStringAsync(cancellationToken);
                try
                {
                    var jObj = JObject.Parse(body);
                    var platform = jObj["platform"]?.ToString();
                    var pushChannel = jObj["pushChannel"];

                    if (string.Equals(platform, "browser", StringComparison.OrdinalIgnoreCase)
                        && pushChannel?.Type == JTokenType.String)
                    {
                        Console.ForegroundColor = ConsoleColor.Yellow;
                        Console.WriteLine("    [BrowserPushChannelFix] pushChannel を JSON文字列 → JSONオブジェクトに変換");
                        Console.WriteLine($"    修正前: pushChannel = \"{Truncate(pushChannel.ToString(), 60)}...\"");

                        // JSON文字列をパースしてオブジェクトに置き換え
                        var pushChannelObj = JObject.Parse(pushChannel.ToString());
                        jObj["pushChannel"] = pushChannelObj;

                        var fixedBody = jObj.ToString(Newtonsoft.Json.Formatting.None);
                        Console.WriteLine($"    修正後: pushChannel = {Truncate(pushChannelObj.ToString(Newtonsoft.Json.Formatting.None), 60)}...");
                        Console.ResetColor();

                        request.Content = new StringContent(fixedBody, Encoding.UTF8, "application/json");
                    }
                }
                catch
                {
                    // パース失敗時はそのまま送信
                }
            }

            return await base.SendAsync(request, cancellationToken);
        }

        private static string Truncate(string value, int maxLength)
        {
            if (string.IsNullOrEmpty(value)) return "";
            return value.Length <= maxLength ? value : value.Substring(0, maxLength);
        }
    }

    /// <summary>
    /// HTTP 通信の詳細をログに出力する DelegatingHandler。
    /// Azure Notification Hubs SDK が内部で行う全ての HTTP 通信をキャプチャする。
    /// </summary>
    class HttpLoggingHandler : DelegatingHandler
    {
        private readonly List<HttpRequestLog> _logs = new();
        private int _requestNumber = 0;

        public HttpLoggingHandler(HttpMessageHandler innerHandler) : base(innerHandler) { }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var requestNum = Interlocked.Increment(ref _requestNumber);
            var sw = Stopwatch.StartNew();

            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine($"    ─── HTTP Request #{requestNum} ───");
            Console.WriteLine($"    → {request.Method} {request.RequestUri}");
            Console.ResetColor();

            // Request Headers
            Console.ForegroundColor = ConsoleColor.DarkGray;
            foreach (var header in request.Headers)
            {
                var value = header.Key.Contains("Authorization", StringComparison.OrdinalIgnoreCase)
                    ? "[SAS Token - 非表示]"
                    : string.Join(", ", header.Value);
                Console.WriteLine($"      {header.Key}: {value}");
            }

            // Request Body
            string? requestBody = null;
            if (request.Content != null)
            {
                requestBody = await request.Content.ReadAsStringAsync(cancellationToken);
                foreach (var header in request.Content.Headers)
                {
                    Console.WriteLine($"      {header.Key}: {string.Join(", ", header.Value)}");
                }
                if (!string.IsNullOrEmpty(requestBody))
                {
                    var displayBody = requestBody.Length > 500 ? requestBody.Substring(0, 500) + "..." : requestBody;
                    Console.WriteLine($"      [Body] {displayBody}");
                }
            }
            Console.ResetColor();

            HttpResponseMessage? response = null;
            string? responseBody = null;
            try
            {
                response = await base.SendAsync(request, cancellationToken);
                sw.Stop();

                var color = (int)response.StatusCode < 400 ? ConsoleColor.Green : ConsoleColor.Red;
                Console.ForegroundColor = color;
                Console.WriteLine($"    ← {(int)response.StatusCode} {response.StatusCode} ({sw.ElapsedMilliseconds}ms)");
                Console.ResetColor();

                // Response Headers
                Console.ForegroundColor = ConsoleColor.DarkGray;
                foreach (var header in response.Headers)
                {
                    Console.WriteLine($"      {header.Key}: {string.Join(", ", header.Value)}");
                }

                // Response Body
                responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
                if (!string.IsNullOrEmpty(responseBody))
                {
                    var displayBody = responseBody.Length > 500 ? responseBody.Substring(0, 500) + "..." : responseBody;
                    Console.WriteLine($"      [Body] {displayBody}");
                }
                Console.ResetColor();

                _logs.Add(new HttpRequestLog
                {
                    RequestNumber = requestNum,
                    Method = request.Method.ToString(),
                    Uri = request.RequestUri?.ToString() ?? "",
                    StatusCode = (int)response.StatusCode,
                    DurationMs = sw.ElapsedMilliseconds,
                    RequestBodyLength = requestBody?.Length ?? 0,
                    ResponseBodyLength = responseBody?.Length ?? 0
                });

                return response;
            }
            catch (Exception ex)
            {
                sw.Stop();
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"    ✗ HTTP Error ({sw.ElapsedMilliseconds}ms): {ex.GetType().Name}: {ex.Message}");
                Console.ResetColor();

                _logs.Add(new HttpRequestLog
                {
                    RequestNumber = requestNum,
                    Method = request.Method.ToString(),
                    Uri = request.RequestUri?.ToString() ?? "",
                    StatusCode = -1,
                    DurationMs = sw.ElapsedMilliseconds,
                    Error = ex.Message
                });

                throw;
            }
        }

        public void PrintSummary()
        {
            Console.WriteLine($"  合計 HTTP リクエスト数: {_logs.Count}");
            Console.WriteLine();
            Console.WriteLine($"  {"#",-4} {"Method",-8} {"Status",-8} {"Time(ms)",-10} {"ReqSize",-10} {"ResSize",-10} URI");
            Console.WriteLine($"  {new string('-', 100)}");
            foreach (var log in _logs)
            {
                Console.WriteLine($"  {log.RequestNumber,-4} {log.Method,-8} {log.StatusCode,-8} {log.DurationMs,-10} {log.RequestBodyLength,-10} {log.ResponseBodyLength,-10} {TruncateUri(log.Uri, 60)}");
            }

            Console.WriteLine();
            Console.WriteLine($"  合計通信時間: {_logs.Sum(l => l.DurationMs)}ms");
            Console.WriteLine($"  平均レスポンス時間: {(_logs.Count > 0 ? _logs.Average(l => l.DurationMs) : 0):F0}ms");
            Console.WriteLine($"  送信データ合計: {_logs.Sum(l => l.RequestBodyLength)} bytes");
            Console.WriteLine($"  受信データ合計: {_logs.Sum(l => l.ResponseBodyLength)} bytes");

            var errors = _logs.Where(l => l.StatusCode >= 400 || l.StatusCode == -1).ToList();
            if (errors.Any())
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine();
                Console.WriteLine($"  ⚠ エラーレスポンス: {errors.Count} 件");
                foreach (var err in errors)
                {
                    Console.WriteLine($"    #{err.RequestNumber} {err.Method} {err.StatusCode} - {err.Error ?? TruncateUri(err.Uri, 50)}");
                }
                Console.ResetColor();
            }
        }

        private static string TruncateUri(string uri, int maxLen)
        {
            if (uri.Length <= maxLen) return uri;
            return uri.Substring(0, maxLen) + "...";
        }
    }

    class HttpRequestLog
    {
        public int RequestNumber { get; set; }
        public string Method { get; set; } = "";
        public string Uri { get; set; } = "";
        public int StatusCode { get; set; }
        public long DurationMs { get; set; }
        public int RequestBodyLength { get; set; }
        public int ResponseBodyLength { get; set; }
        public string? Error { get; set; }
    }
}
