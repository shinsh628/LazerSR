using System;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;

namespace LazerSR.Launcher.Replay;

/// <summary>
/// Drains the Hook's per-play dan queue
/// (<c>%LocalAppData%\LazerSR\danplayupload\*.json</c>) and POSTs each record to the
/// dan server. JSON body only — no file. Errors surface verbatim (toy server).
/// </summary>
public static class DanPlayServerClient
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
        "LazerSR", "danplayupload");

    public readonly record struct SyncResult(int Uploaded, int Failed, string? FirstError);

    public static async Task<SyncResult> DrainQueueAsync(Action<string>? report = null)
    {
        string[] files = Directory.Exists(QueueDir) ? Directory.GetFiles(QueueDir, "*.json") : Array.Empty<string>();
        if (files.Length == 0) return new SyncResult(0, 0, null);

        int uploaded = 0, failed = 0;
        string? firstError = null;

        foreach (string file in files)
        {
            report?.Invoke($"dan 업로드… {uploaded + failed + 1}/{files.Length}");

            string json;
            try { json = await File.ReadAllTextAsync(file); }
            catch (Exception ex) { failed++; firstError ??= $"큐 파일 읽기 실패: {ex.Message}"; continue; }

            try
            {
                using var content = new StringContent(json, Encoding.UTF8, "application/json");
                using var response = await http.PostAsync($"{BaseUrl}/api/v1/dan-plays", content);
                string body = await response.Content.ReadAsStringAsync();

                if (response.IsSuccessStatusCode)
                {
                    uploaded++;
                    TryDelete(file);
                }
                else
                {
                    failed++;
                    string snippet = body.Length > 300 ? body[..300] : body;
                    firstError ??= $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}: {snippet}";
                }
            }
            catch (Exception ex)
            {
                failed++;
                firstError ??= ex.Message;
            }
        }

        return new SyncResult(uploaded, failed, firstError);
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { /* next drain retries */ }
    }
}
