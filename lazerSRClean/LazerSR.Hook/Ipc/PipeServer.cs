using System;
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

                    if (line == "sunny:on")
                        SunnyState.SetEnabled(true);
                    else if (line == "sunny:off")
                        SunnyState.SetEnabled(false);
                    else if (line == "sunnyplus:on")
                        SetUniversalDiffEnabled(true);
                    else if (line == "sunnyplus:off")
                        SetUniversalDiffEnabled(false);
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
}
