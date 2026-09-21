// LazerSR osu! host — runs osu!lazer's own osu!.dll through osu!'s own hostfxr, in place of osu!.exe.
//
// Why this exists: from 2026.920.0-lazer osu! ships with `StartupHookSupport=false`, which bakes
// `System.StartupHookProvider.IsSupported=false` into osu!.runtimeconfig.json and makes the runtime
// ignore DOTNET_STARTUP_HOOKS. This host does exactly what osu!.exe (the stock .NET apphost) does,
// except that it flips that one runtime property back to true in memory before starting the app.
// No osu! file is read for writing or modified. See docs\guides\architecture.md §1.
//
// Usage: osu!.exe <osu! install dir> [args passed through to osu!...]
// DOTNET_STARTUP_HOOKS (and every other LazerSR env var) is set by the launcher on this process only.
//
// Everything here runs before any managed code exists, so failures cannot be logged by HookLog —
// every failure path shows a message box and returns a distinct exit code instead.

#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <shellapi.h>
#include <stdio.h>
#include <stdlib.h>
#include <wchar.h>

typedef void* hostfxr_handle;

typedef struct
{
    size_t size;
    const wchar_t* host_path;
    const wchar_t* dotnet_root;
} hostfxr_initialize_parameters;

typedef int(__cdecl* hostfxr_initialize_for_dotnet_command_line_fn)(int argc, const wchar_t** argv,
    const hostfxr_initialize_parameters* parameters, hostfxr_handle* host_context_handle);
typedef int(__cdecl* hostfxr_set_runtime_property_value_fn)(hostfxr_handle host_context_handle,
    const wchar_t* name, const wchar_t* value);
typedef int(__cdecl* hostfxr_run_app_fn)(hostfxr_handle host_context_handle);
typedef int(__cdecl* hostfxr_close_fn)(hostfxr_handle host_context_handle);

#define MAX_ARGS 256

static int fail(int code, const wchar_t* format, ...)
{
    wchar_t message[1024];
    va_list args;
    va_start(args, format);
    _vsnwprintf_s(message, _countof(message), _TRUNCATE, format, args);
    va_end(args);

    MessageBoxW(NULL, message, L"LazerSR osu! host", MB_OK | MB_ICONERROR);
    return code;
}

int WINAPI wWinMain(HINSTANCE instance, HINSTANCE prevInstance, PWSTR commandLine, int showCommand)
{
    (void)instance; (void)prevInstance; (void)commandLine; (void)showCommand;

    int argc = 0;
    wchar_t** argv = CommandLineToArgvW(GetCommandLineW(), &argc);
    if (argv == NULL || argc < 2)
        return fail(1, L"Usage: osu!.exe <osu! install directory> [arguments...]\n\nThis program is started by LazerSR.exe.");

    const wchar_t* osuDir = argv[1];

    wchar_t hostfxrPath[MAX_PATH];
    wchar_t appPath[MAX_PATH];
    if (swprintf_s(hostfxrPath, MAX_PATH, L"%s\\hostfxr.dll", osuDir) < 0
        || swprintf_s(appPath, MAX_PATH, L"%s\\osu!.dll", osuDir) < 0)
        return fail(2, L"osu! install path is too long:\n%s", osuDir);

    if (GetFileAttributesW(appPath) == INVALID_FILE_ATTRIBUTES)
        return fail(3, L"osu!.dll not found:\n%s", appPath);

    HMODULE hostfxr = LoadLibraryExW(hostfxrPath, NULL, LOAD_WITH_ALTERED_SEARCH_PATH);
    if (hostfxr == NULL)
        return fail(4, L"Failed to load osu!'s hostfxr.dll (error %lu):\n%s", GetLastError(), hostfxrPath);

    hostfxr_initialize_for_dotnet_command_line_fn initialize =
        (hostfxr_initialize_for_dotnet_command_line_fn)GetProcAddress(hostfxr, "hostfxr_initialize_for_dotnet_command_line");
    hostfxr_set_runtime_property_value_fn setProperty =
        (hostfxr_set_runtime_property_value_fn)GetProcAddress(hostfxr, "hostfxr_set_runtime_property_value");
    hostfxr_run_app_fn runApp = (hostfxr_run_app_fn)GetProcAddress(hostfxr, "hostfxr_run_app");
    hostfxr_close_fn close = (hostfxr_close_fn)GetProcAddress(hostfxr, "hostfxr_close");

    if (initialize == NULL || setProperty == NULL || runApp == NULL || close == NULL)
        return fail(5, L"osu!'s hostfxr.dll is missing a required export.");

    // argv for the app: the app path first (as the stock apphost does), then everything after our own argument.
    const wchar_t* appArgs[MAX_ARGS];
    int appArgc = 0;
    appArgs[appArgc++] = appPath;
    for (int i = 2; i < argc && appArgc < MAX_ARGS; i++)
        appArgs[appArgc++] = argv[i];

    // osu! is self-contained: the runtime lives in the install directory itself.
    hostfxr_initialize_parameters parameters = { sizeof(hostfxr_initialize_parameters), NULL, osuDir };
    hostfxr_handle context = NULL;

    int rc = initialize(appArgc, appArgs, &parameters, &context);
    if (rc < 0 || context == NULL)
        return fail(6, L"hostfxr_initialize_for_dotnet_command_line failed (0x%08X).", (unsigned int)rc);

    rc = setProperty(context, L"System.StartupHookProvider.IsSupported", L"true");
    if (rc < 0)
    {
        close(context);
        return fail(7, L"hostfxr_set_runtime_property_value failed (0x%08X).", (unsigned int)rc);
    }

    // Blocks until osu! exits; returns osu!'s own exit code.
    rc = runApp(context);
    close(context);
    LocalFree(argv);
    return rc;
}
