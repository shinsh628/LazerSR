using System;
using System.Runtime.InteropServices;
using System.Threading;
using osuTK.Input;

namespace LazerSR.Hook.Input;

/// <summary>
/// 창 포커스와 무관하게 <b>하드웨어 키 이벤트</b>를 받는다 (Raw Input + <c>RIDEV_INPUTSINK</c>).
/// <para>
/// 패턴 복제 모드에서는 대상 게임이 포커스를 쥐고 있어야 하므로(그래야 그 게임의 핵 방지에 걸릴 여지가
/// 없다) osu!는 포커스를 못 받는다. <c>INPUTSINK</c>는 <b>비포그라운드 창에도 입력을 배달</b>해주는
/// 플래그라, 대상 게임의 입력 경로를 전혀 건드리지 않고 같은 키를 병렬로 받아올 수 있다.
/// </para>
/// <para>
/// 합성 입력(<c>SendInput</c> 등)은 어디에도 쓰지 않는다 — 사용자가 실제로 누른 키를 <b>전달만</b> 한다.
/// </para>
/// </summary>
internal sealed class RawKeyboardListener : IDisposable
{
    /// <summary>키 상태 변화. <b>전용 메시지 스레드에서 호출된다</b> — 수신 측이 스레드 안전해야 한다.</summary>
    public event Action<Key, bool>? KeyChanged;

    /// <summary>
    /// 윈도우 클래스에 박히는 함수 포인터의 주인. <b>절대 인스턴스 필드로 두면 안 된다.</b>
    /// <para>
    /// Win32 윈도우 클래스는 <c>RegisterClassEx</c> 시점의 함수 포인터를 <b>값으로 복사해 보관</b>하고
    /// <c>UnregisterClass</c> 전까지 프로세스 수명 내내 남는다. 그런데
    /// <c>Marshal.GetFunctionPointerForDelegate</c>가 만든 스텁은 GC가 추적하지 않으므로,
    /// 델리게이트를 세션 수명(인스턴스 필드)에 묶으면 세션이 끝난 뒤 수거되면서
    /// <b>클래스에는 해제된 메모리를 가리키는 포인터만 남는다.</b>
    /// 그 상태로 두 번째 세션이 같은 클래스로 창을 만들면 <c>CreateWindowEx</c>가 반환 전에 보내는
    /// <c>WM_NCCREATE</c>부터 죽은 포인터로 점프해 <b>액세스 위반으로 프로세스가 즉사</b>한다
    /// (2026-08-21 재진입 크래시의 원인).
    /// </para>
    /// <para>
    /// 그래서 클래스의 수명(프로세스)에 델리게이트의 수명을 맞춘다. 프로시저가 하나로 공유되므로
    /// 실제 수신 대상은 <see cref="current"/>로 갈아끼운다.
    /// </para>
    /// </summary>
    private static readonly WndProc shared_wnd_proc = staticWndProc;

    private static readonly object class_lock = new();
    private static bool classRegistered;

    /// <summary>지금 메시지를 받을 리스너. 창은 세션마다 새로 만들지만 프로시저는 하나뿐이다.</summary>
    private static volatile RawKeyboardListener? current;

    private Thread? thread;
    private IntPtr hwnd;
    private uint threadId;

    private readonly ManualResetEventSlim ready = new(false);
    private volatile bool disposed;

    public bool Start()
    {
        if (thread != null) return true;

        thread = new Thread(run) { IsBackground = true, Name = "LazerSR RawInput" };
        thread.Start();

        // 창 생성과 장치 등록이 끝날 때까지 기다린다 — 실패 여부를 호출부가 알아야 한다.
        ready.Wait(3000);

        return hwnd != IntPtr.Zero;
    }

    private void run()
    {
        try
        {
            if (!createMessageWindow() || !registerDevice())
            {
                ready.Set();
                return;
            }

            ready.Set();

            // 메시지 전용 창이라 이 루프가 WM_INPUT만 받는다.
            while (!disposed && GetMessage(out MSG msg, IntPtr.Zero, 0, 0) > 0)
            {
                TranslateMessage(ref msg);
                DispatchMessage(ref msg);
            }
        }
        catch (Exception ex)
        {
            HookLog.Write($"[LazerSR] RawKeyboardListener thread failed: {ex}");
        }
        finally
        {
            // 백그라운드 스레드 진입점 밖으로 예외가 나가면 .NET은 프로세스를 즉시 종료한다.
            // 여기서 새는 경로가 실제로 하나 있었다 — Dispose()가 Join 타임아웃 뒤 ready를 버리면
            // 이 Set()이 던졌다. 지금은 Dispose가 ready를 버리지 않지만, 방어는 남겨둔다.
            try
            {
                ready.Set();
                cleanup();
            }
            catch (Exception ex)
            {
                HookLog.Write($"[LazerSR] RawKeyboardListener cleanup failed: {ex}");
            }
        }
    }

    private const string window_class = "LazerSRRawInputSink";

    /// <summary>
    /// 클래스 등록은 <b>프로세스당 한 번</b>이다 (<see cref="shared_wnd_proc"/> 주석 참고).
    /// </summary>
    private static bool ensureClassRegistered()
    {
        lock (class_lock)
        {
            if (classRegistered)
                return true;

            var wc = new WNDCLASSEX
            {
                cbSize = Marshal.SizeOf<WNDCLASSEX>(),
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(shared_wnd_proc),
                hInstance = GetModuleHandle(null),
                lpszClassName = window_class,
            };

            if (RegisterClassEx(ref wc) != 0)
            {
                classRegistered = true;
                return true;
            }

            int error = Marshal.GetLastWin32Error();

            // 이 이름은 우리만 쓰므로 보통 나지 않는다. 그래도 이미 있다면 그 클래스의 프로시저 역시
            // 프로세스 수명으로 붙잡혀 있는 우리 것이므로(이 메서드 말고는 등록하는 곳이 없다) 그대로 쓴다.
            if (error == ERROR_CLASS_ALREADY_EXISTS)
            {
                classRegistered = true;
                return true;
            }

            HookLog.Write($"[LazerSR] RawKeyboardListener: RegisterClassEx failed ({error}).");
            return false;
        }
    }

    private bool createMessageWindow()
    {
        threadId = GetCurrentThreadId();

        if (!ensureClassRegistered())
            return false;

        // 프로시저가 공유이므로 창을 만들기 전에 수신 대상을 우리로 바꿔둔다 —
        // CreateWindowEx는 반환 전에 WM_NCCREATE/WM_CREATE를 동기로 보낸다.
        current = this;

        hwnd = CreateWindowEx(0, window_class, string.Empty, 0, 0, 0, 0, 0, HWND_MESSAGE, IntPtr.Zero, GetModuleHandle(null), IntPtr.Zero);

        if (hwnd == IntPtr.Zero)
        {
            HookLog.Write($"[LazerSR] RawKeyboardListener: CreateWindowEx failed ({Marshal.GetLastWin32Error()}).");
            releaseTarget(this);
        }

        return hwnd != IntPtr.Zero;
    }

    private static void releaseTarget(RawKeyboardListener listener)
    {
        lock (class_lock)
        {
            if (ReferenceEquals(current, listener))
                current = null;
        }
    }

    private bool registerDevice()
    {
        var device = new RAWINPUTDEVICE
        {
            UsagePage = 0x01,      // Generic Desktop
            Usage = 0x06,          // Keyboard
            Flags = RIDEV_INPUTSINK,
            Target = hwnd,
        };

        if (RegisterRawInputDevices(new[] { device }, 1, Marshal.SizeOf<RAWINPUTDEVICE>()))
            return true;

        HookLog.Write($"[LazerSR] RawKeyboardListener: RegisterRawInputDevices failed ({Marshal.GetLastWin32Error()}).");
        return false;
    }

    /// <summary>
    /// <b>네이티브에서 직접 불린다 — 어떤 예외도 밖으로 나가면 안 된다.</b>
    /// 세션마다 새로 만들지 않고 하나를 계속 쓰므로, 실제 처리는 지금 대상인 리스너에게 넘긴다.
    /// </summary>
    private static IntPtr staticWndProc(IntPtr h, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == WM_INPUT)
        {
            try
            {
                var listener = current;

                if (listener != null && !listener.disposed)
                    listener.handleRawInput(lParam);
            }
            catch (Exception ex)
            {
                HookLog.Write($"[LazerSR] RawKeyboardListener.handleRawInput failed: {ex}");
            }
        }

        return DefWindowProc(h, msg, wParam, lParam);
    }

    private void handleRawInput(IntPtr handle)
    {
        int headerSize = Marshal.SizeOf<RAWINPUTHEADER>();
        uint size = (uint)Marshal.SizeOf<RAWINPUTKEYBOARD>();

        IntPtr buffer = Marshal.AllocHGlobal((int)size);

        try
        {
            if (GetRawInputData(handle, RID_INPUT, buffer, ref size, (uint)headerSize) == unchecked((uint)-1))
                return;

            var raw = Marshal.PtrToStructure<RAWINPUTKEYBOARD>(buffer);

            if (raw.Header.Type != RIM_TYPEKEYBOARD)
                return;

            // 키보드가 만들어내는 잡음 값. 무시하지 않으면 엉뚱한 키로 매핑된다.
            if (raw.Keyboard.VKey >= 0xFF)
                return;

            if (RawKeyMap.FromVirtualKey(raw.Keyboard.VKey) is not Key key)
                return;

            bool pressed = (raw.Keyboard.Flags & RI_KEY_BREAK) == 0;

            KeyChanged?.Invoke(key, pressed);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private void cleanup()
    {
        if (hwnd != IntPtr.Zero)
        {
            DestroyWindow(hwnd);
            hwnd = IntPtr.Zero;
        }

        // 클래스와 프로시저는 프로세스 수명이라 해제하지 않는다 — 수신 대상만 놓는다.
        releaseTarget(this);
    }

    public void Dispose()
    {
        if (disposed) return;

        disposed = true;
        KeyChanged = null;

        // 창을 실제로 부수는 건 메시지 스레드의 cleanup()이지만, Join이 타임아웃할 수 있으므로
        // 수신 대상은 여기서 먼저 놓는다 — 그래야 그 사이에 온 WM_INPUT이 버려진다.
        releaseTarget(this);

        // 메시지 루프를 깨워서 스스로 빠져나가게 한다.
        if (threadId != 0)
            PostThreadMessage(threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);

        thread?.Join(1000);
        thread = null;

        // ready는 일부러 Dispose하지 않는다. Join이 타임아웃하면 메시지 스레드가 아직
        // finally에서 ready.Set()을 부를 수 있고, 그 예외는 백그라운드 스레드 미처리 예외라
        // 프로세스를 즉시 종료시킨다. 내부 핸들은 SafeHandle 파이널라이저가 정리한다.
    }

    // ---- Win32 ----

    private const int WM_INPUT = 0x00FF;
    private const int WM_QUIT = 0x0012;
    private const uint RID_INPUT = 0x10000003;
    private const uint RIM_TYPEKEYBOARD = 1;
    private const uint RIDEV_INPUTSINK = 0x00000100;
    private const ushort RI_KEY_BREAK = 0x01;
    private const int ERROR_CLASS_ALREADY_EXISTS = 1410;

    private static readonly IntPtr HWND_MESSAGE = new(-3);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWINPUTDEVICE
    {
        public ushort UsagePage;
        public ushort Usage;
        public uint Flags;
        public IntPtr Target;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWINPUTHEADER
    {
        public uint Type;
        public uint Size;
        public IntPtr Device;
        public IntPtr WParam;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWKEYBOARD
    {
        public ushort MakeCode;
        public ushort Flags;
        public ushort Reserved;
        public ushort VKey;
        public uint Message;
        public uint ExtraInformation;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWINPUTKEYBOARD
    {
        public RAWINPUTHEADER Header;
        public RAWKEYBOARD Keyboard;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEX
    {
        public int cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszMenuName;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int ptX;
        public int ptY;
    }

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern ushort RegisterClassEx(ref WNDCLASSEX wc);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowEx(uint exStyle, string className, string windowName, uint style,
        int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr DefWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterRawInputDevices(RAWINPUTDEVICE[] devices, uint count, int size);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetRawInputData(IntPtr rawInput, uint command, IntPtr data, ref uint size, uint headerSize);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetMessage(out MSG msg, IntPtr hWnd, uint filterMin, uint filterMax);

    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref MSG msg);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr DispatchMessage(ref MSG msg);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostThreadMessage(uint threadId, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? name);
}
