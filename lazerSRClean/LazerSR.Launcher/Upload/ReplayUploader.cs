using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using LazerSR.Launcher.Configuration;

namespace LazerSR.Launcher.Upload;

/// <summary>
/// Hook이 <c>%LocalAppData%\LazerSR\replayupload\</c>에 써둔 큐 파일을 리플레이 저장 서버로
/// 업로드한다. 키가 없으면 첫 큐 파일의 osu! 유저네임으로 자동 등록부터 한다.
/// UpdateChecker와 같은 방어적 철학 - 어떤 실패도 앱을 막지 않고 그 파일만 건너뛴다.
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

    public static async Task SyncAsync(LauncherSettingsStore settingsStore, Action<string> reportStatus)
    {
        string queueDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LazerSR", "replayupload");

        if (!Directory.Exists(queueDir))
        {
            reportStatus("동기화할 리플레이가 없습니다.");
            return;
        }

        string[] files = Directory.GetFiles(queueDir, "*.json");
        if (files.Length == 0)
        {
            reportStatus("동기화할 리플레이가 없습니다.");
            return;
        }

        var settings = settingsStore.Load();
        string? apiKey = settings.ReplayServerApiKey;

        if (string.IsNullOrEmpty(apiKey))
        {
            apiKey = await TryRegisterAsync(files[0], reportStatus);
            if (apiKey == null) return; // 이미 reportStatus로 이유를 알림

            settingsStore.Save(settings with { ReplayServerApiKey = apiKey });
        }

        int uploaded = 0, failed = 0;

        foreach (string file in files)
        {
            reportStatus($"업로드 중... {uploaded + failed + 1}/{files.Length}");

            bool ok = await TryUploadOneAsync(file, apiKey);
            if (ok)
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

    private static async Task<string?> TryRegisterAsync(string firstQueueFile, Action<string> reportStatus)
    {
        try
        {
            using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(firstQueueFile));
            string? osuUsername = doc.RootElement.TryGetProperty("osu_username", out var el) ? el.GetString() : null;

            if (string.IsNullOrEmpty(osuUsername))
            {
                reportStatus("osu! 유저네임을 확인할 수 없어 등록에 실패했습니다.");
                return null;
            }

            var body = JsonSerializer.Serialize(new { osu_username = osuUsername });
            using var content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");
            using var response = await http.PostAsync($"{serverBaseUrl}/api/v1/register", content);

            if (!response.IsSuccessStatusCode)
            {
                reportStatus($"서버 등록 실패 ({(int)response.StatusCode}).");
                return null;
            }

            using var resultDoc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            return resultDoc.RootElement.TryGetProperty("api_key", out var keyEl) ? keyEl.GetString() : null;
        }
        catch (Exception ex)
        {
            reportStatus($"서버 등록 실패: {ex.Message}");
            return null;
        }
    }

    private static async Task<bool> TryUploadOneAsync(string queueFile, string apiKey)
    {
        try
        {
            string metadataJson = await File.ReadAllTextAsync(queueFile);
            using var doc = JsonDocument.Parse(metadataJson);
            string? replayPath = doc.RootElement.TryGetProperty("replay_path", out var pathEl) ? pathEl.GetString() : null;

            if (replayPath == null || !File.Exists(replayPath))
                return true; // 리플레이 원본이 이미 사라짐 - 재시도해도 의미 없으니 큐만 정리

            using var form = new MultipartFormDataContent();
            form.Add(new StringContent(metadataJson), "metadata");

            byte[] replayBytes = await File.ReadAllBytesAsync(replayPath);
            var fileContent = new ByteArrayContent(replayBytes);
            fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            form.Add(fileContent, "replay", "replay.osr");

            using var request = new HttpRequestMessage(HttpMethod.Post, $"{serverBaseUrl}/api/v1/replays");
            request.Headers.Add("X-Api-Key", apiKey);
            request.Content = form;

            using var response = await http.SendAsync(request);
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
