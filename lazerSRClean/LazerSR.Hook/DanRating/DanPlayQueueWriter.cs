using System;
using System.IO;
using System.Text.Json;
using LazerSR.DanCalculator.PlayerRating;
using osu.Game.Scoring;

namespace LazerSR.Hook.DanRating;

/// <summary>
/// Writes a per-play dan record to the queue folder
/// (<c>%LocalAppData%\LazerSR\danplayupload\</c>) as <c>{score_guid}.json</c>.
/// The launcher drains it and POSTs to the dan server. Local write only — no network.
/// </summary>
internal static class DanPlayQueueWriter
{
    private const int schema_version = 1;
    private const string queue_folder = "danplayupload";

    public static bool WriteEntry(ScoreInfo score, PlayUploadRecord record)
    {
        try
        {
            string folder = LazerSrStorage.GetFolder(queue_folder);
            if (string.IsNullOrEmpty(folder)) return false;

            var envelope = new
            {
                schema_version,
                score_guid = score.ID.ToString(),
                osu_username = score.RealmUser.Username,
                osu_user_id = score.RealmUser.OnlineID > 1 ? score.RealmUser.OnlineID : (int?)null,
                record,
            };

            string path = Path.Combine(folder, $"{score.ID}.json");
            return LazerSrStorage.WriteText(path, JsonSerializer.Serialize(envelope));
        }
        catch (Exception e)
        {
            HookLog.Write($"[LazerSR] DanPlayQueueWriter.WriteEntry({score.ID}) failed: {e}");
            return false;
        }
    }
}
