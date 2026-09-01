using System;
using System.Collections.Concurrent;
using System.IO;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;
using LazerSR.SunnyCalculator.Tuning;

namespace LazerSR.Hook.Ipc;

public static class PipeServer
{
    private static int _started;
    private static StreamWriter? _activeWriter;
    private static readonly object _writerLock = new();

    // 요청/응답 상관: Hook이 런처에 뭔가 물어보고(리더보드 조회, 리플레이 다운로드) 답을 기다린다.
    private static readonly ConcurrentDictionary<string, TaskCompletionSource<string>> _pending = new();

    /// <summary>
    /// 런처에 <c>{verb}:{reqId}:{payload}</c>를 보내고, 런처가 <c>{verb}ok:{reqId}:{result}</c> 또는
    /// <c>{verb}err:{reqId}:{message}</c>로 답할 때까지 기다린다. 파이프가 안 붙어 있으면 즉시 예외.
    /// </summary>
    public static async Task<string> RequestAsync(string verb, string payload, int timeoutMs)
    {
        string reqId = Guid.NewGuid().ToString("N")[..8];
        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[reqId] = tcs;

        try
        {
            lock (_writerLock)
            {
                if (_activeWriter == null)
                    throw new InvalidOperationException("launcher not connected");
            }

            await BroadcastAsync($"{verb}:{reqId}:{payload}").ConfigureAwait(false);

            var completed = await Task.WhenAny(tcs.Task, Task.Delay(timeoutMs)).ConfigureAwait(false);
            if (completed != tcs.Task)
                throw new TimeoutException($"{verb} timed out");
            return await tcs.Task.ConfigureAwait(false);
        }
        finally
        {
            _pending.TryRemove(reqId, out _);
        }
    }

    private static bool tryCompleteReply(string line)
    {
        // "<verb>ok:<reqId>:<result>" / "<verb>err:<reqId>:<message>"
        int firstColon = line.IndexOf(':');
        if (firstColon < 0) return false;
        string tag = line[..firstColon];
        if (!tag.EndsWith("ok", StringComparison.Ordinal) && !tag.EndsWith("err", StringComparison.Ordinal))
            return false;

        string rest = line[(firstColon + 1)..];
        int secondColon = rest.IndexOf(':');
        string reqId = secondColon < 0 ? rest : rest[..secondColon];
        string result = secondColon < 0 ? string.Empty : rest[(secondColon + 1)..];

        if (!_pending.TryRemove(reqId, out var tcs))
            return false;

        if (tag.EndsWith("err", StringComparison.Ordinal))
            tcs.TrySetException(new Exception(result));
        else
            tcs.TrySetResult(result);
        return true;
    }

    public static string PipeName => $"osu-lazer-sr-mod-{Environment.ProcessId}";

    public static void StartBackground()
    {
        if (Interlocked.CompareExchange(ref _started, 1, 0) == 0)
            Task.Run(RunLoop);
    }

    public static async Task BroadcastAsync(string message)
    {
        StreamWriter? writer;
        lock (_writerLock)
            writer = _activeWriter;
        if (writer == null) return;
        try
        {
            await writer.WriteLineAsync(message).ConfigureAwait(false);
            await writer.FlushAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            HookLog.Write($"[PipeServer] BroadcastAsync failed: {ex.Message}");
        }
    }

    private static async Task RunLoop()
    {
        while (true)
        {
            try
            {
                var pipe = new NamedPipeServerStream(
                    PipeName,
                    PipeDirection.InOut,
                    NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);

                try
                {
                    await pipe.WaitForConnectionAsync();
                }
                catch (Exception ex)
                {
                    HookLog.Write($"[PipeServer] WaitForConnectionAsync failed: {ex.Message}");
                    await pipe.DisposeAsync();
                    continue;
                }

                _ = Task.Run(() => HandleConnectionAsync(pipe));
            }
            catch (Exception ex)
            {
                HookLog.Write($"[PipeServer] RunLoop error: {ex.Message}");
            }
        }
    }

    private static async Task HandleConnectionAsync(NamedPipeServerStream pipe)
    {
        await using (pipe)
        {
            using var reader = new StreamReader(pipe, leaveOpen: true);
            var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = false };
            using var _ = writer;

            lock (_writerLock)
                _activeWriter = writer;

            try
            {
                await writer.WriteAsync("connected\n");
                await writer.FlushAsync();

                // 런처가 (재)연결됐다 - sunny 정렬의 서버 대조가 아직 안 됐으면 지금 재시도.
                SunnySort.SunnySortServerSync.OnLauncherConnected();

                while (pipe.IsConnected)
                {
                    string? line;
                    try
                    {
                        line = await reader.ReadLineAsync();
                    }
                    catch
                    {
                        break;
                    }

                    if (line is null)
                        break;

                    // 패턴 복제 모드의 실시간 노트 스트림(newScreen이 보낸다). 양이 많아 먼저 걸러낸다.
                    if (PatternCopy.PatternCopyBridge.TryHandleLine(line))
                        continue;

                    // 런처가 보낸 요청 응답(리더보드/리플레이 다운로드).
                    if (tryCompleteReply(line))
                        continue;

                    if (line == "sunny:on")
                        SunnyState.SetEnabled(true);
                    else if (line == "sunny:off")
                        SunnyState.SetEnabled(false);
                    else if (line == "sunnyplus:on")
                        SetUniversalDiffEnabled(true);
                    else if (line == "sunnyplus:off")
                        SetUniversalDiffEnabled(false);
                    else if (line == "replaycollect:scan")
                        DispatchReplayCollectScan();
                }
            }
            finally
            {
                lock (_writerLock)
                {
                    if (ReferenceEquals(_activeWriter, writer))
                        _activeWriter = null;
                }
            }
        }
    }

    // TEMP: sunny+ on/off checkbox in the Launcher. Reload() recomputes SunnyConstants' process-wide
    // default so every sunny calculation started after this point (song select, tooltips, results
    // screen, ...) picks up the change - already-displayed pills only refresh once they recompute.
    private static void SetUniversalDiffEnabled(bool enabled)
    {
        DiffCombiner.UniversalDiffEnabled = enabled;
        SunnyConstants.Reload();
    }

    private static void DispatchReplayCollectScan() => Task.Run(HandleReplayCollectScan);

    // "리플레이 수집" 버튼. 네트워크는 안 함 — 로컬 realm을 훑어 큐 파일만 쓰고, 몇 건 썼는지
    // (또는 아직 준비 안 됐는지)를 런처에 회신한다. 실제 업로드는 런처가 이어서 한다.
    private static async Task HandleReplayCollectScan()
    {
        try
        {
            int? count = ReplayUpload.ReplayCollectService.CollectAll();
            await BroadcastAsync(count == null
                ? "replaycollect:notready"
                : $"replaycollect:queued:{count}");
        }
        catch (Exception ex)
        {
            HookLog.Write($"[PipeServer] HandleReplayCollectScan failed: {ex.Message}");
            await BroadcastAsync("replaycollect:error");
        }
    }
}
