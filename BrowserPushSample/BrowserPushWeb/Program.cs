using System.Diagnostics;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Azure.NotificationHubs;
using Newtonsoft.Json.Linq;

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

// ── 設定読み込み ──
var config = builder.Configuration;
var connectionString = config["NotificationHub:ConnectionString"]
    ?? throw new InvalidOperationException("NotificationHub:ConnectionString が設定されていません");
var hubName = config["NotificationHub:HubName"]
    ?? throw new InvalidOperationException("NotificationHub:HubName が設定されていません");

// ── SDK クライアント (BrowserPushChannelFixHandler 付き + EnableTestSend) ──
var loggingHandler = new HttpLoggingHandler(
    new BrowserPushChannelFixHandler(new HttpClientHandler()));
var nhSettings = new NotificationHubSettings { MessageHandler = loggingHandler };
var nhClient = NotificationHubClient.CreateClientFromConnectionString(connectionString, hubName, enableTestSend: true);
// MessageHandler を設定するため、内部フィールドに直接設定はできないので別クライアントも作る
var nhClientWithHandler = new NotificationHubClient(connectionString, hubName, nhSettings);

// ── VAPID 公開鍵をHubから取得 ──
string? vapidPublicKey = null;
try
{
    var nsMgr = NamespaceManager.CreateFromConnectionString(connectionString);
    var hub = await nsMgr.GetNotificationHubAsync(hubName);
    var bc = hub.BrowserCredential;
    vapidPublicKey = bc?.VapidPublicKey;
    Console.WriteLine($"[起動] BrowserCredential 診断:");
    Console.WriteLine($"  VAPID Public Key:  {(string.IsNullOrEmpty(bc?.VapidPublicKey) ? "❌ 未設定" : bc.VapidPublicKey[..30] + "...")}");
    Console.WriteLine($"  VAPID Private Key: {(string.IsNullOrEmpty(bc?.VapidPrivateKey) ? "❌ 未設定" : bc.VapidPrivateKey[..10] + "... (設定済み)")}");
    Console.WriteLine($"  Subject:           {(string.IsNullOrEmpty(bc?.Subject) ? "❌ 未設定" : bc.Subject)}");
}
catch (Exception ex)
{
    Console.WriteLine($"[起動] Hub情報取得失敗: {ex.Message}");
}

// ── 登録済み Installation を追跡 ──
var registeredInstallations = new List<string>();
// ── テスト実行ごとのユニークタグ ──
string currentRunTag = $"run-{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}";

// ── 静的ファイル配信 ──
app.UseDefaultFiles();
app.UseStaticFiles();

// ── API: VAPID公開鍵を取得 ──
app.MapGet("/api/vapid-key", () =>
{
    if (string.IsNullOrEmpty(vapidPublicKey))
        return Results.Problem("VAPID public key が Hub に設定されていません");
    return Results.Ok(new { vapidPublicKey });
});

// ── API: 古いレジストレーションを全削除 ──
app.MapPost("/api/cleanup", async () =>
{
    Console.WriteLine();
    Console.WriteLine("════════════════════════════════════════════════════");
    Console.WriteLine("  POST /api/cleanup - 古いレジストレーションを全削除");
    Console.WriteLine("════════════════════════════════════════════════════");

    var deleted = 0;
    var errors = 0;
    try
    {
        // SDK の GetAllRegistrationsAsync は BrowserRegistrationDescription を逆シリアライズできないため
        // REST API を直接呼び出して RegistrationId のみ取得する
        var csParts = connectionString.Split(';')
            .Select(p => p.Split('=', 2))
            .Where(p => p.Length == 2)
            .ToDictionary(p => p[0], p => p[1], StringComparer.OrdinalIgnoreCase);
        var sbEndpoint = csParts["Endpoint"].Replace("sb://", "https://").TrimEnd('/');
        var sasKeyName = csParts["SharedAccessKeyName"];
        var sasKey = csParts["SharedAccessKey"];
        var resourceUri = $"{sbEndpoint}/{hubName}";
        var expiry = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 3600;
        var stringToSign = Uri.EscapeDataString(resourceUri) + "\n" + expiry;
        using var hmac = new System.Security.Cryptography.HMACSHA256(Encoding.UTF8.GetBytes(sasKey));
        var signature = Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(stringToSign)));
        var sasToken = $"SharedAccessSignature sr={Uri.EscapeDataString(resourceUri)}&sig={Uri.EscapeDataString(signature)}&se={expiry}&skn={sasKeyName}";

        using var httpClient = new HttpClient();
        httpClient.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", sasToken);

        var allIds = new List<string>();
        string? nextUrl = $"{resourceUri}/registrations?api-version=2015-01";
        while (!string.IsNullOrEmpty(nextUrl))
        {
            var resp = await httpClient.GetAsync(nextUrl);
            resp.EnsureSuccessStatusCode();
            var xml = await resp.Content.ReadAsStringAsync();
            var matches = System.Text.RegularExpressions.Regex.Matches(xml, @"<RegistrationId>([^<]+)</RegistrationId>");
            foreach (System.Text.RegularExpressions.Match m in matches)
                allIds.Add(m.Groups[1].Value);
            var nextMatch = System.Text.RegularExpressions.Regex.Match(xml, @"<link rel=""next"" href=""([^""]+)""");
            nextUrl = nextMatch.Success ? nextMatch.Groups[1].Value : null;
        }

        Console.WriteLine($"  見つかったレジストレーション数: {allIds.Count}");
        foreach (var id in allIds)
        {
            try
            {
                await nhClient.DeleteRegistrationAsync(id);
                deleted++;
                Console.WriteLine($"  ✓ 削除: {id}");
            }
            catch (Exception ex)
            {
                errors++;
                Console.WriteLine($"  ✗ 削除失敗: {id} - {ex.Message}");
            }
        }

        // Installation の追跡リストもクリア
        registeredInstallations.Clear();
        // 新しいユニークタグを生成
        currentRunTag = $"run-{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}";

        Console.WriteLine($"  完了: {deleted} 件削除, {errors} 件エラー, 新タグ: {currentRunTag}");

        return Results.Ok(new { deleted, errors, total = allIds.Count, newRunTag = currentRunTag });
    }
    catch (Exception ex)
    {
        Console.WriteLine($"  ✗ クリーンアップ失敗: {ex.Message}");
        return Results.Problem($"クリーンアップ失敗: {ex.Message}");
    }
});

// ── API: ブラウザのPushSubscriptionを登録 ──
app.MapPost("/api/register", async (SubscriptionRequest req) =>
{
    Console.WriteLine();
    Console.WriteLine("════════════════════════════════════════════════════");
    Console.WriteLine("  POST /api/register - ブラウザの PushSubscription を登録");
    Console.WriteLine("════════════════════════════════════════════════════");
    Console.WriteLine($"  endpoint: {req.Endpoint}");
    Console.WriteLine($"  p256dh: {req.P256DH?[..Math.Min(30, req.P256DH?.Length ?? 0)]}...");
    Console.WriteLine($"  auth: {req.Auth?[..Math.Min(15, req.Auth?.Length ?? 0)]}...");

    if (string.IsNullOrEmpty(req.Endpoint) || string.IsNullOrEmpty(req.P256DH) || string.IsNullOrEmpty(req.Auth))
        return Results.BadRequest("endpoint, p256dh, auth は必須です");

    // Endpoint URL からプッシュサービスを判定
    if (Uri.TryCreate(req.Endpoint, UriKind.Absolute, out var endpointUri))
    {
        var host = endpointUri.Host;
        var pushService = host switch
        {
            _ when host.Contains("fcm.googleapis.com") => "Google FCM (Chrome/Edge Chromium)",
            _ when host.Contains("mozilla.com") || host.Contains("push.services.mozilla.com") => "Mozilla Push Service (Firefox)",
            _ when host.Contains("notify.windows.com") => "Windows Push Notification Service (Edge)",
            _ => $"不明: {host}"
        };
        Console.WriteLine($"  Push Service: {pushService}");
    }

    var installationId = $"browser-{Guid.NewGuid():N}"[..36];
    var subscription = new BrowserPushSubscription
    {
        Endpoint = req.Endpoint,
        P256DH = req.P256DH,
        Auth = req.Auth
    };

    var installation = new BrowserInstallation(installationId, subscription);
    installation.Tags = new List<string> { "browser", "web-push-live", currentRunTag };

    var sw = Stopwatch.StartNew();
    try
    {
        await nhClientWithHandler.CreateOrUpdateInstallationAsync(installation);
        sw.Stop();
        registeredInstallations.Add(installationId);
        Console.WriteLine($"  ✓ Installation登録成功: {installationId} ({sw.ElapsedMilliseconds}ms)");
        return Results.Ok(new { installationId, message = "登録完了！" });
    }
    catch (Exception ex)
    {
        sw.Stop();
        Console.WriteLine($"  ✗ Installation登録失敗: {ex.Message}");
        return Results.Problem($"Registration failed: {ex.Message}");
    }
});

// ── API: プッシュ通知を送信 ──
app.MapPost("/api/send", async (SendRequest req) =>
{
    Console.WriteLine();
    Console.WriteLine("════════════════════════════════════════════════════");
    Console.WriteLine("  POST /api/send - プッシュ通知を送信");
    Console.WriteLine("════════════════════════════════════════════════════");

    var title = req.Title ?? "Azure Notification Hubs";
    var message = req.Message ?? "テスト通知です！";

    var payload = JsonSerializer.Serialize(new
    {
        title,
        body = message,
        icon = "https://azure.microsoft.com/favicon.ico",
        timestamp = DateTime.UtcNow.ToString("o"),
        data = new { url = "https://portal.azure.com" }
    });

    Console.WriteLine($"  タイトル: {title}");
    Console.WriteLine($"  メッセージ: {message}");
    Console.WriteLine($"  ペイロード: {payload}");
    Console.WriteLine($"  登録済みInstallation数: {registeredInstallations.Count}");

    // 直接送信（installationId 指定）と タグ送信 両方試す
    var results = new List<object>();

    // ── 1. 直接送信（各Installation ID に対して） ──
    foreach (var instId in registeredInstallations.ToList())
    {
        Console.WriteLine($"  [直接送信] Installation: {instId}");
        var sw2 = Stopwatch.StartNew();
        try
        {
            var notification = new BrowserNotification(payload);
            var directOutcome = await nhClient.SendDirectNotificationAsync(notification, instId);
            sw2.Stop();
            Console.WriteLine($"    ✓ 直接送信完了 ({sw2.ElapsedMilliseconds}ms)");
            Console.WriteLine($"      TrackingId: {directOutcome.TrackingId}");
            Console.WriteLine($"      NotificationId: {directOutcome.NotificationId}");
            Console.WriteLine($"      State: {directOutcome.State}");
            Console.WriteLine($"      Success: {directOutcome.Success}, Failure: {directOutcome.Failure}");
            if (directOutcome.Results != null)
            {
                foreach (var r in directOutcome.Results)
                {
                    Console.WriteLine($"      Result: Platform={r.ApplicationPlatform}, Outcome={r.Outcome}, PnsHandle={r.PnsHandle?[..Math.Min(50, r.PnsHandle?.Length ?? 0)]}..., RegId={r.RegistrationId}");
                }
            }
            results.Add(new { method = "direct", installationId = instId, trackingId = directOutcome.TrackingId, notificationId = directOutcome.NotificationId, state = directOutcome.State.ToString(), success = directOutcome.Success, failure = directOutcome.Failure, details = directOutcome.Results?.Select(r => new { r.ApplicationPlatform, r.Outcome, r.RegistrationId }).ToList() });
        }
        catch (Exception ex)
        {
            sw2.Stop();
            Console.WriteLine($"    ✗ 直接送信失敗 ({sw2.ElapsedMilliseconds}ms): {ex.Message}");
            results.Add(new { method = "direct", installationId = instId, error = ex.Message });
        }
    }

    // ── 2. タグ送信 (ユニークタグで現在のブラウザだけに送信) ──
    var sw = Stopwatch.StartNew();
    try
    {
        var notification = new BrowserNotification(payload);
        Console.WriteLine($"  [タグ送信] タグ: {currentRunTag}");
        var outcome = await nhClient.SendNotificationAsync(notification, currentRunTag);
        sw.Stop();

        Console.WriteLine($"  [タグ送信] ✓ 完了 ({sw.ElapsedMilliseconds}ms)");
        Console.WriteLine($"    TrackingId: {outcome.TrackingId}");
        Console.WriteLine($"    NotificationId: {outcome.NotificationId}");
        Console.WriteLine($"    State: {outcome.State}");
        Console.WriteLine($"    Success: {outcome.Success}, Failure: {outcome.Failure}");
        if (outcome.Results != null)
        {
            foreach (var r in outcome.Results)
            {
                Console.WriteLine($"    Result: Platform={r.ApplicationPlatform}, Outcome={r.Outcome}, PnsHandle={r.PnsHandle?[..Math.Min(50, r.PnsHandle?.Length ?? 0)]}..., RegId={r.RegistrationId}");
            }
        }

        results.Add(new { method = "tag", trackingId = outcome.TrackingId, notificationId = outcome.NotificationId, state = outcome.State.ToString(), success = outcome.Success, failure = outcome.Failure, details = outcome.Results?.Select(r => new { r.ApplicationPlatform, r.Outcome, r.RegistrationId }).ToList() });
    }
    catch (Exception ex)
    {
        sw.Stop();
        Console.WriteLine($"  [タグ送信] ✗ 失敗 ({sw.ElapsedMilliseconds}ms): {ex.Message}");
        results.Add(new { method = "tag", error = ex.Message });
    }

    return Results.Ok(new
    {
        success = true,
        results
    });
});

// ── API: HTTP通信サマリー ──
app.MapGet("/api/summary", async () =>
{
    // Hub の BrowserCredential 診断情報も返す
    string privateKeyStatus = "unknown";
    string subject = "unknown";
    try
    {
        var nsMgr = NamespaceManager.CreateFromConnectionString(connectionString);
        var hub = await nsMgr.GetNotificationHubAsync(hubName);
        var bc = hub.BrowserCredential;
        privateKeyStatus = string.IsNullOrEmpty(bc?.VapidPrivateKey) ? "NOT SET" : $"SET ({bc.VapidPrivateKey.Length} chars)";
        subject = bc?.Subject ?? "NOT SET";
    }
    catch { }

    return Results.Ok(new
    {
        registeredInstallations = registeredInstallations.Count,
        installationIds = registeredInstallations,
        vapidPrivateKeyStatus = privateKeyStatus,
        vapidSubject = subject
    });
});

// ── API: BrowserCredential の Subject を修正 ──
app.MapPost("/api/fix-subject", async () =>
{
    Console.WriteLine();
    Console.WriteLine("════════════════════════════════════════════════════");
    Console.WriteLine("  POST /api/fix-subject - VAPID Subject を修正");
    Console.WriteLine("════════════════════════════════════════════════════");

    try
    {
        var nsMgr = NamespaceManager.CreateFromConnectionString(connectionString);
        var hub = await nsMgr.GetNotificationHubAsync(hubName);

        var oldSubject = hub.BrowserCredential?.Subject;
        Console.WriteLine($"  現在の Subject: \"{oldSubject}\"");

        if (hub.BrowserCredential != null)
        {
            hub.BrowserCredential.Subject = "mailto:test@example.com";
            await nsMgr.UpdateNotificationHubAsync(hub);

            // 更新確認
            var updated = await nsMgr.GetNotificationHubAsync(hubName);
            var newSubject = updated.BrowserCredential?.Subject;
            Console.WriteLine($"  更新後の Subject: \"{newSubject}\"");

            return Results.Ok(new { oldSubject, newSubject, message = "Subject を修正しました" });
        }

        return Results.Problem("BrowserCredential が見つかりません");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"  ✗ 修正失敗: {ex.Message}");
        return Results.Problem($"修正失敗: {ex.Message}");
    }
});

Console.WriteLine();
Console.WriteLine("╔══════════════════════════════════════════════════════════════╗");
Console.WriteLine("║   Browser Push Web - Azure Notification Hubs サンプル       ║");
Console.WriteLine("╚══════════════════════════════════════════════════════════════╝");
Console.WriteLine($"  Hub: {hubName}");
Console.WriteLine($"  VAPID Key: {(vapidPublicKey != null ? "取得済み" : "未取得")}");
Console.WriteLine();
Console.WriteLine("  → ブラウザで http://localhost:5280 を開いてください");
Console.WriteLine();

app.Run("http://localhost:5285");

// ── Request/Response モデル ──
record SubscriptionRequest(string? Endpoint, string? P256DH, string? Auth);
record SendRequest(string? Title, string? Message);

// ── BrowserPushChannelFixHandler (コンソール版と同じ) ──
class BrowserPushChannelFixHandler : DelegatingHandler
{
    public BrowserPushChannelFixHandler(HttpMessageHandler inner) : base(inner) { }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        if (request.Method == HttpMethod.Put
            && request.RequestUri?.AbsolutePath.Contains("/installations/") == true
            && request.Content != null)
        {
            var body = await request.Content.ReadAsStringAsync(ct);
            try
            {
                var jObj = JObject.Parse(body);
                if (string.Equals(jObj["platform"]?.ToString(), "browser", StringComparison.OrdinalIgnoreCase)
                    && jObj["pushChannel"]?.Type == JTokenType.String)
                {
                    Console.ForegroundColor = ConsoleColor.Yellow;
                    Console.WriteLine("    [Fix] pushChannel: JSON文字列 → JSONオブジェクトに変換");
                    Console.ResetColor();
                    jObj["pushChannel"] = JObject.Parse(jObj["pushChannel"]!.ToString());
                    request.Content = new StringContent(jObj.ToString(Newtonsoft.Json.Formatting.None), Encoding.UTF8, "application/json");
                }
            }
            catch { }
        }
        return await base.SendAsync(request, ct);
    }
}

// ── HttpLoggingHandler ──
class HttpLoggingHandler : DelegatingHandler
{
    private int _requestNumber;
    public HttpLoggingHandler(HttpMessageHandler inner) : base(inner) { }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var num = Interlocked.Increment(ref _requestNumber);
        var sw = Stopwatch.StartNew();

        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine($"    ─── HTTP #{num} → {request.Method} {request.RequestUri} ───");
        Console.ResetColor();

        if (request.Content != null)
        {
            var body = await request.Content.ReadAsStringAsync(ct);
            if (!string.IsNullOrEmpty(body))
            {
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.WriteLine($"      [Body] {(body.Length > 300 ? body[..300] + "..." : body)}");
                Console.ResetColor();
            }
        }

        try
        {
            var response = await base.SendAsync(request, ct);
            sw.Stop();
            var color = (int)response.StatusCode < 400 ? ConsoleColor.Green : ConsoleColor.Red;
            Console.ForegroundColor = color;
            Console.WriteLine($"    ← {(int)response.StatusCode} {response.StatusCode} ({sw.ElapsedMilliseconds}ms)");
            Console.ResetColor();

            var respBody = await response.Content.ReadAsStringAsync(ct);
            if (!string.IsNullOrEmpty(respBody))
            {
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.WriteLine($"      [Body] {(respBody.Length > 300 ? respBody[..300] + "..." : respBody)}");
                Console.ResetColor();
            }

            return response;
        }
        catch (Exception ex)
        {
            sw.Stop();
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"    ✗ HTTP Error ({sw.ElapsedMilliseconds}ms): {ex.Message}");
            Console.ResetColor();
            throw;
        }
    }
}
