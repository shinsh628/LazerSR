using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace LazerSR.Launcher.Replay;

public enum UploadOutcome { Accepted, Duplicate, Failed }

public sealed record SyncResult(int Uploaded, int Duplicate, int Failed, string? FirstError)
{
    public int Total => Uploaded + Duplicate + Failed;
}

/// <summary>
/// Hook이 <c>%LocalAppData%\LazerSR\replayupload\</c>에 써둔 큐 파일을 리플레이 저장 서버로 올린다.
/// 인증 없음 — 리플레이를 친 사람의 osu! 유저네임이 메타데이터에 이미 들어 있으므로 그걸 신뢰한다.
/// <para>
/// 이전 구현은 실패를 전부 <c>catch { return false }</c>로 삼켜서 "0개 업로드, 29개 실패"의
/// 원인(서버 500)을 못 봤다. 이번엔 <b>HTTP 상태코드 + 응답 본문을 그대로 올려보낸다</b>.
/// </para>
/// </summary>
public static class ReplayServerClient
{
    public const string BaseUrl = "http://68.183.226.182";

    private static readonly HttpClient http = CreateClient();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("LazerSR-Launcher");
        return client;
    }

    private static string QueueDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "LazerSR", "replayupload");

    /// <summary>
    /// lazerSR 리더보드 원본 JSON. Hook이 파이프로 요청하면 런처가 대신 친다(Hook은 네트워크 금지).
    /// <paramref name="modsToken"/>: <c>*</c>=필터 없음, <c>-</c>=모드 없는 기록만, <c>DT,HD</c>=그 세트.
    /// </summary>
    public static async Task<string> GetLeaderboardRawAsync(string beatmapMd5, string modsToken)
    {
        string url = $"{BaseUrl}/api/v1/leaderboard?beatmap_md5={Uri.EscapeDataString(beatmapMd5)}";

        if (modsToken == "-")
            url += "&mods=";
        else if (modsToken != "*")
            url += "&mods=" + Uri.EscapeDataString(modsToken);

        return await http.GetStringAsync(url);
    }

    /// <summary>서버에서 <c>.osr</c>을 받아 <c>%LocalAppData%\LazerSR\replaycache\</c>에 저장하고 그 경로를 돌려준다.</summary>
    public static async Task<string> DownloadReplayToCacheAsync(string scoreGuid)
    {
        string dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LazerSR", "replaycache");
        Directory.CreateDirectory(dir);

        string path = Path.Combine(dir, $"{scoreGuid}.osr");
        byte[] bytes = await http.GetByteArrayAsync($"{BaseUrl}/api/v1/replays/{scoreGuid}.osr");
        await File.WriteAllBytesAsync(path, bytes);
        return path;
    }

    /// <summary>서버에 저장된 리플레이 총 개수. 실패하면 null.</summary>
    public static async Task<int?> GetReplayCountAsync()
    {
        try
        {
            using var doc = JsonDocument.Parse(await http.GetStringAsync($"{BaseUrl}/api/v1/stats"));
            return doc.RootElement.TryGetProperty("replay_count", out var el) ? el.GetInt32() : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>큐 폴더의 모든 <c>.json</c>을 업로드한다. 2xx(accepted/duplicate)면 그 파일을 지운다.</summary>
    public static async Task<SyncResult> DrainQueueAsync(Action<string> report)
    {
        string[] files = Directory.Exists(QueueDir) ? Directory.GetFiles(QueueDir, "*.json") : [];
        if (files.Length == 0)
            return new SyncResult(0, 0, 0, null);

        int uploaded = 0, duplicate = 0, failed = 0;
        string? firstError = null;

        foreach (string file in files)
        {
            report($"업로드 중… {uploaded + duplicate + failed + 1}/{files.Length}");

            var (outcome, error) = await TryUploadOneAsync(file);

            switch (outcome)
            {
                case UploadOutcome.Accepted:
                    uploaded++;
                    TryDelete(file);
                    break;

                case UploadOutcome.Duplicate:
                    duplicate++;
                    TryDelete(file);
                    break;

                default:
                    failed++;
                    firstError ??= error;
                    break;
            }
        }

        return new SyncResult(uploaded, duplicate, failed, firstError);
    }

    private static async Task<(UploadOutcome, string?)> TryUploadOneAsync(string queueFile)
    {
        string metadataJson;
        string? replayPath;

        try
        {
            metadataJson = await File.ReadAllTextAsync(queueFile);
            using var doc = JsonDocument.Parse(metadataJson);
            replayPath = doc.RootElement.TryGetProperty("replay_path", out var pathEl) ? pathEl.GetString() : null;
        }
        catch (Exception ex)
        {
            return (UploadOutcome.Failed, $"큐 파일을 읽지 못함: {ex.Message}");
        }

        if (string.IsNullOrEmpty(replayPath) || !File.Exists(replayPath))
        {
            // 리플레이 원본이 이미 사라짐 — 재시도해도 의미 없으니 큐만 정리한다.
            TryDelete(queueFile);
            return (UploadOutcome.Duplicate, null);
        }

        try
        {
            using var form = new MultipartFormDataContent();
            form.Add(new StringContent(metadataJson, Encoding.UTF8, "application/json"), "metadata");

            byte[] replayBytes = await File.ReadAllBytesAsync(replayPath);
            var fileContent = new ByteArrayContent(replayBytes);
            fileContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
            form.Add(fileContent, "replay", "replay.osr");

            using var response = await http.PostAsync($"{BaseUrl}/api/v1/replays", form);
            string body = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                string snippet = body.Length > 200 ? body[..200] : body;
                return (UploadOutcome.Failed, $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}: {snippet}");
            }

            bool isDuplicate = body.Contains("\"duplicate\"", StringComparison.OrdinalIgnoreCase);
            return (isDuplicate ? UploadOutcome.Duplicate : UploadOutcome.Accepted, null);
        }
        catch (Exception ex)
        {
            return (UploadOutcome.Failed, ex.Message);
        }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch { /* 다음 동기화에서 다시 시도 */ }
    }
}
