using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;

namespace LazerSR.Launcher.Upload;

/// <summary>
/// Hook이 <c>%LocalAppData%\LazerSR\replayupload\</c>에 써둔 큐 파일을 리플레이 저장 서버로
/// 업로드한다. 인증 없음 - 리플레이를 친 사람의 osu! 유저네임이 메타데이터에 이미 들어있으므로
/// 그걸 그대로 신뢰한다. UpdateChecker와 같은 방어적 철학 - 어떤 실패도 앱을 막지 않고 그 파일만
/// 건너뛴다.
/// </summary>
public static class ReplayUploader
{
    private const string serverBaseUrl = "http://68.183.226.182";

    private static readonly HttpClient http = CreateClient();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("LazerSR-Launcher");
        return client;
    }

    public static async Task SyncAsync(Action<string> reportStatus)
    {
        string queueDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LazerSR", "replayupload");

        string[] files = Directory.Exists(queueDir) ? Directory.GetFiles(queueDir, "*.json") : [];
        if (files.Length == 0)
        {
            reportStatus("동기화할 리플레이가 없습니다.");
            return;
        }

        int uploaded = 0, failed = 0;

        foreach (string file in files)
        {
            reportStatus($"업로드 중... {uploaded + failed + 1}/{files.Length}");

            if (await TryUploadOneAsync(file))
            {
                uploaded++;
                TryDelete(file);
            }
            else
            {
                failed++;
            }
        }

        reportStatus(failed == 0
            ? $"동기화 완료 — {uploaded}개 업로드됨."
            : $"동기화 완료 — {uploaded}개 업로드, {failed}개 실패(다음 시도에 재시도).");
    }

    private static async Task<bool> TryUploadOneAsync(string queueFile)
    {
        try
        {
            string metadataJson = await File.ReadAllTextAsync(queueFile);
            using var doc = System.Text.Json.JsonDocument.Parse(metadataJson);
            string? replayPath = doc.RootElement.TryGetProperty("replay_path", out var pathEl) ? pathEl.GetString() : null;

            if (replayPath == null || !File.Exists(replayPath))
                return true; // 리플레이 원본이 이미 사라짐 - 재시도해도 의미 없으니 큐만 정리

            using var form = new MultipartFormDataContent();
            form.Add(new StringContent(metadataJson), "metadata");

            byte[] replayBytes = await File.ReadAllBytesAsync(replayPath);
            var fileContent = new ByteArrayContent(replayBytes);
            fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            form.Add(fileContent, "replay", "replay.osr");

            using var response = await http.PostAsync($"{serverBaseUrl}/api/v1/replays", form);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch { }
    }
}
