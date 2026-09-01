using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace LazerSR.Launcher.Sunny;

/// <summary>
/// sunny 정렬 캐시값을 리플레이 저장 서버와 주고받는다 (Hook은 네트워크 금지라 런처가 대신).
/// 검증 없음 — 지인 토이.
/// </summary>
public static class SunnySortClient
{
    private const string base_url = "http://68.183.226.182";

    private static readonly HttpClient http = new() { Timeout = System.TimeSpan.FromSeconds(30) };

    public sealed record Entry(string beatmap_md5, double rate, double sr, int calc_version);

    /// <summary>서버 전체 덤프 원본 JSON (Hook이 파이프로 병합).</summary>
    public static async Task<string> GetAllRawAsync(int calcVersion)
        => await http.GetStringAsync($"{base_url}/api/v1/sunny/all?calc_version={calcVersion}");

    /// <summary>배치 업서트. 반환 = 업로드 시도 건수.</summary>
    public static async Task<int> PostBatchAsync(IReadOnlyList<Entry> entries)
    {
        if (entries.Count == 0)
            return 0;

        string body = JsonSerializer.Serialize(new { entries });
        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        using var resp = await http.PostAsync($"{base_url}/api/v1/sunny", content);
        resp.EnsureSuccessStatusCode();
        return entries.Count;
    }
}
