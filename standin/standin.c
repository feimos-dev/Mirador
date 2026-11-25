// Copyright 2026, Finn Zeruhn
// SPDX-License-Identifier: BSL-1.0

#ifndef UNICODE
#define UNICODE
#endif
#ifndef _UNICODE
#define _UNICODE
#endif
#define WIN32_LEAN_AND_MEAN
#define _CRT_SECURE_NO_WARNINGS

#include <windows.h>
#include <aclapi.h>
#include <userenv.h>
#include <stdio.h>
#include <string.h>
#include <wchar.h>
#include <wctype.h>

enum
{
    EXIT_UP = 0,
    EXIT_FAILED = 1,
    EXIT_ELEVATED = 3,
    EXIT_NO_APP = 4,
    EXIT_APP_SILENT = 5,
    EXIT_NO_ANSWER = 6,
};

#define LINGER_MS 2000
#define APP_WAIT_MS 15000
#define ANSWER_WAIT_MS 120000
#define RETRY_MS 100
#define POLL_MS 50
#define MAX_LINE 256
#define MAX_SETTINGS 65536

static ULONGLONG started;

static ULONGLONG elapsed(void)
{
    return GetTickCount64() - started;
}

static void say(const char *text)
{
    fprintf(stderr, "Mirador stand-in: %s\n", text);
    fflush(stderr);
}

static void say_error(const char *what, DWORD error)
{
    fprintf(stderr, "Mirador stand-in: %s (Windows error %lu).\n", what, (unsigned long)error);
    fflush(stderr);
}

static BOOL is_test_pipe(const wchar_t *name)
{
    static const wchar_t prefix[] = L"mirador-standin-test-";
    const size_t prefix_length = sizeof prefix / sizeof prefix[0] - 1;
    size_t i;
    if (wcsncmp(name, prefix, prefix_length) != 0 || wcslen(name) != prefix_length + 12)
    {
        return FALSE;
    }
    for (i = prefix_length; i < prefix_length + 12; i++)
    {
        if (!iswxdigit(name[i]))
        {
            return FALSE;
        }
    }
    return TRUE;
}

static BOOL is_elevated(void)
{
    HANDLE token;
    TOKEN_ELEVATION elevation;
    DWORD size = 0;
    BOOL elevated = FALSE;
    if (OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, &token))
    {
        if (GetTokenInformation(token, TokenElevation, &elevation, sizeof elevation, &size))
        {
            elevated = elevation.TokenIsElevated != 0;
        }
        CloseHandle(token);
    }
    return elevated;
}

static BOOL owned_by_this_user(HANDLE pipe)
{
    PSID owner = NULL;
    PSECURITY_DESCRIPTOR descriptor = NULL;
    union
    {
        TOKEN_USER user;
        BYTE bytes[SECURITY_MAX_SID_SIZE + sizeof(TOKEN_USER)];
    } me;
    HANDLE token;
    DWORD size = 0;
    BOOL same = FALSE;

    if (GetSecurityInfo(pipe, SE_KERNEL_OBJECT, OWNER_SECURITY_INFORMATION, &owner, NULL, NULL, NULL, &descriptor) != ERROR_SUCCESS)
    {
        return FALSE;
    }

    if (OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, &token))
    {
        if (GetTokenInformation(token, TokenUser, &me, sizeof me, &size))
        {
            same = owner != NULL && EqualSid(owner, me.user.User.Sid);
        }
        CloseHandle(token);
    }

    LocalFree(descriptor);
    return same;
}

static HANDLE open_pipe(const wchar_t *name, DWORD *error)
{
    for (;;)
    {
        HANDLE pipe = CreateFileW(
            name, GENERIC_READ | GENERIC_WRITE, 0, NULL, OPEN_EXISTING, SECURITY_SQOS_PRESENT | SECURITY_IDENTIFICATION, NULL);
        if (pipe != INVALID_HANDLE_VALUE)
        {
            *error = ERROR_SUCCESS;
            return pipe;
        }

        *error = GetLastError();
        if (*error != ERROR_PIPE_BUSY || elapsed() >= ANSWER_WAIT_MS)
        {
            return INVALID_HANDLE_VALUE;
        }

        WaitNamedPipeW(name, 2000);
    }
}

static BOOL is_file(const wchar_t *path)
{
    DWORD attributes = GetFileAttributesW(path);
    return attributes != INVALID_FILE_ATTRIBUTES && (attributes & FILE_ATTRIBUTE_DIRECTORY) == 0;
}

static BOOL own_app(wchar_t *path, DWORD count)
{
    DWORD length = GetModuleFileNameW(NULL, path, count);
    int i;
    if (length == 0 || length >= count)
    {
        return FALSE;
    }

    for (i = 0; i < 7; i++)
    {
        wchar_t *slash = wcsrchr(path, L'\\');
        if (slash == NULL)
        {
            return FALSE;
        }
        *slash = L'\0';
        if (wcslen(path) + wcslen(L"\\Mirador.exe") + 1 > count)
        {
            return FALSE;
        }
        wcscat(path, L"\\Mirador.exe");
        if (is_file(path))
        {
            return TRUE;
        }
        *wcsrchr(path, L'\\') = L'\0';
    }

    return FALSE;
}

static BOOL find_app(wchar_t *path, DWORD count)
{
    DWORD size;
    DWORD length = GetEnvironmentVariableW(L"MIRADOR_APP_PATH", path, count);
    if (length > 0 && length < count)
    {
        return TRUE;
    }

    if (own_app(path, count))
    {
        return TRUE;
    }

    size = count * (DWORD)sizeof(wchar_t);
    if (RegGetValueW(HKEY_CURRENT_USER, L"Software\\Mirador", L"AppPath", RRF_RT_REG_SZ | RRF_SUBKEY_WOW6464KEY, NULL, path, &size) == ERROR_SUCCESS
        && is_file(path))
    {
        return TRUE;
    }

    size = count * (DWORD)sizeof(wchar_t);
    return RegGetValueW(HKEY_LOCAL_MACHINE, L"SOFTWARE\\Mirador", L"AppPath", RRF_RT_REG_SZ | RRF_SUBKEY_WOW6464KEY, NULL, path, &size) == ERROR_SUCCESS
        && is_file(path);
}

static BOOL matches_at(const char *text, size_t length, size_t at, const char *word)
{
    size_t word_length = strlen(word);
    return at <= length && length - at >= word_length && memcmp(text + at, word, word_length) == 0;
}

static size_t skip_space(const char *text, size_t length, size_t at)
{
    while (at < length && (text[at] == ' ' || text[at] == '\t' || text[at] == '\r' || text[at] == '\n'))
    {
        at++;
    }
    return at;
}

static int games_may_start_app(void)
{
    static const char key[] = "\"startMiradorForGames\"";
    wchar_t path[MAX_PATH];
    static char text[MAX_SETTINGS + 1];
    DWORD length = GetEnvironmentVariableW(L"LOCALAPPDATA", path, MAX_PATH);
    LARGE_INTEGER size;
    DWORD got = 0;
    HANDLE file;
    size_t at;

    if (length == 0 || length >= MAX_PATH || wcslen(path) + wcslen(L"\\Mirador\\settings.json") + 1 > MAX_PATH)
    {
        return 1;
    }
    wcscat(path, L"\\Mirador\\settings.json");

    file = CreateFileW(path, GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, NULL, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, NULL);
    if (file == INVALID_HANDLE_VALUE)
    {
        return 1;
    }

    if (!GetFileSizeEx(file, &size) || size.QuadPart > MAX_SETTINGS || !ReadFile(file, text, MAX_SETTINGS, &got, NULL))
    {
        CloseHandle(file);
        return 1;
    }
    CloseHandle(file);

    for (at = 0; at < got; at++)
    {
        size_t value;
        if (!matches_at(text, got, at, key))
        {
            continue;
        }

        value = skip_space(text, got, at + sizeof key - 1);
        if (value < got && text[value] == ':')
        {
            value = skip_space(text, got, value + 1);
            if (matches_at(text, got, value, "false"))
            {
                return 0;
            }
        }
    }

    return 1;
}

static void log_line(const char *text)
{
    wchar_t path[MAX_PATH];
    char line[512];
    SYSTEMTIME now;
    DWORD length = GetEnvironmentVariableW(L"LOCALAPPDATA", path, MAX_PATH);
    DWORD written = 0;
    HANDLE file;
    int count;

    if (length == 0 || length >= MAX_PATH || wcslen(path) + wcslen(L"\\Mirador\\logs\\standin.log") + 1 > MAX_PATH)
    {
        return;
    }
    wcscat(path, L"\\Mirador");
    CreateDirectoryW(path, NULL);
    wcscat(path, L"\\logs");
    CreateDirectoryW(path, NULL);
    wcscat(path, L"\\standin.log");

    GetLocalTime(&now);
    count = snprintf(line, sizeof line, "%04u-%02u-%02uT%02u:%02u:%02u %s\r\n",
                     now.wYear, now.wMonth, now.wDay, now.wHour, now.wMinute, now.wSecond, text);
    if (count <= 0 || (size_t)count >= sizeof line)
    {
        return;
    }

    file = CreateFileW(path, FILE_APPEND_DATA, FILE_SHARE_READ | FILE_SHARE_WRITE, NULL, OPEN_ALWAYS, FILE_ATTRIBUTE_NORMAL, NULL);
    if (file != INVALID_HANDLE_VALUE)
    {
        WriteFile(file, line, (DWORD)count, &written, NULL);
        CloseHandle(file);
    }
}

static BOOL start_app(const wchar_t *exe, HANDLE *app)
{
    wchar_t command[MAX_PATH + 32];
    wchar_t folder[MAX_PATH];
    wchar_t *slash;
    STARTUPINFOW startup;
    PROCESS_INFORMATION process;
    void *environment = NULL;
    HANDLE token;
    DWORD flags = CREATE_UNICODE_ENVIRONMENT | CREATE_BREAKAWAY_FROM_JOB;
    BOOL ok;

    if (swprintf(command, MAX_PATH + 32, L"\"%ls\" --background", exe) < 0)
    {
        SetLastError(ERROR_FILENAME_EXCED_RANGE);
        return FALSE;
    }

    wcsncpy(folder, exe, MAX_PATH - 1);
    folder[MAX_PATH - 1] = L'\0';
    slash = wcsrchr(folder, L'\\');
    if (slash != NULL)
    {
        *slash = L'\0';
    }

    if (OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY | TOKEN_DUPLICATE, &token))
    {
        if (!CreateEnvironmentBlock(&environment, token, FALSE))
        {
            environment = NULL;
        }
        CloseHandle(token);
    }

    ZeroMemory(&startup, sizeof startup);
    startup.cb = sizeof startup;
    ZeroMemory(&process, sizeof process);
    ok = CreateProcessW(exe, command, NULL, NULL, FALSE, flags, environment, slash != NULL ? folder : NULL, &startup, &process);
    if (!ok && GetLastError() == ERROR_ACCESS_DENIED)
    {
        ok = CreateProcessW(exe, command, NULL, NULL, FALSE, flags & ~(DWORD)CREATE_BREAKAWAY_FROM_JOB, environment,
                            slash != NULL ? folder : NULL, &startup, &process);
    }

    if (environment != NULL)
    {
        DWORD error = GetLastError();
        DestroyEnvironmentBlock(environment);
        SetLastError(error);
    }

    if (ok)
    {
        CloseHandle(process.hThread);
        *app = process.hProcess;
    }
    return ok;
}

static int read_reply(HANDLE pipe, char *line, DWORD count)
{
    DWORD have = 0;
    for (;;)
    {
        DWORD available = 0;
        char *newline;
        if (!PeekNamedPipe(pipe, NULL, 0, NULL, &available, NULL))
        {
            say("Mirador closed the connection without answering. Its log is %LOCALAPPDATA%\\Mirador\\logs\\mirador.log.");
            return EXIT_FAILED;
        }

        if (available > 0)
        {
            DWORD got = 0;
            DWORD want = count - 1 - have;
            if (want == 0)
            {
                say("Mirador's answer is longer than the stand-in reads.");
                return EXIT_FAILED;
            }
            if (want > available)
            {
                want = available;
            }
            if (!ReadFile(pipe, line + have, want, &got, NULL))
            {
                say("Mirador closed the connection without answering. Its log is %LOCALAPPDATA%\\Mirador\\logs\\mirador.log.");
                return EXIT_FAILED;
            }

            have += got;
            line[have] = '\0';
            newline = memchr(line, '\n', have);
            if (newline != NULL)
            {
                *newline = '\0';
                if (newline > line && newline[-1] == '\r')
                {
                    newline[-1] = '\0';
                }
                return -1;
            }
            continue;
        }

        if (elapsed() >= ANSWER_WAIT_MS)
        {
            say("Mirador didn't answer within 120 s.");
            return EXIT_NO_ANSWER;
        }

        Sleep(POLL_MS);
    }
}

int main(void)
{
    wchar_t name[64];
    wchar_t app[MAX_PATH];
    char line[MAX_LINE + 2];
    DWORD session = 0;
    DWORD error = ERROR_SUCCESS;
    DWORD written = 0;
    DWORD override;
    HANDLE pipe;
    HANDLE started_app = NULL;
    int result;
    static const char request[] = "START\n";

    started = GetTickCount64();

    override = GetEnvironmentVariableW(L"MIRADOR_STANDIN_PIPE", app, MAX_PATH);
    if (override > 0 && override < MAX_PATH && is_test_pipe(app) && !is_elevated())
    {
        swprintf(name, 64, L"\\\\.\\pipe\\%ls", app);
    }
    else
    {
        if (!ProcessIdToSessionId(GetCurrentProcessId(), &session))
        {
            session = 0;
        }
        swprintf(name, 64, L"\\\\.\\pipe\\mirador-app-%lu", (unsigned long)session);
    }

    pipe = open_pipe(name, &error);
    if (pipe == INVALID_HANDLE_VALUE && error == ERROR_FILE_NOT_FOUND)
    {
        if (is_elevated())
        {
            say("Mirador isn't running. Start Mirador first: it doesn't start from an elevated app.");
            return EXIT_ELEVATED;
        }

        if (!games_may_start_app())
        {
            log_line("A game asked for Monado; \"Start Mirador when a game needs Monado\" is off.");
            say("Mirador isn't running, and \"Start Mirador when a game needs Monado\" is off in its settings: start Mirador first.");
            return EXIT_NO_APP;
        }

        if (!find_app(app, MAX_PATH))
        {
            say("Mirador isn't running, and the stand-in can't find it: start Mirador once, or reinstall it.");
            return EXIT_NO_APP;
        }

        if (!start_app(app, &started_app))
        {
            say_error("Mirador isn't running and couldn't be started", GetLastError());
            return EXIT_NO_APP;
        }

        {
            ULONGLONG asked = elapsed();
            DWORD app_exit = 0;
            for (;;)
            {
                Sleep(RETRY_MS);
                pipe = open_pipe(name, &error);
                if (pipe != INVALID_HANDLE_VALUE || error != ERROR_FILE_NOT_FOUND)
                {
                    break;
                }

                if (WaitForSingleObject(started_app, 0) == WAIT_OBJECT_0 && GetExitCodeProcess(started_app, &app_exit) && app_exit != 0)
                {
                    CloseHandle(started_app);
                    fprintf(stderr, "Mirador stand-in: Mirador was started but closed at once (exit code %lu).\n", (unsigned long)app_exit);
                    return EXIT_NO_APP;
                }

                if (elapsed() - asked >= APP_WAIT_MS)
                {
                    CloseHandle(started_app);
                    say("Mirador was started but didn't answer within 15 s.");
                    return EXIT_APP_SILENT;
                }
            }

            CloseHandle(started_app);
        }
    }

    if (pipe == INVALID_HANDLE_VALUE)
    {
        if (error == ERROR_PIPE_BUSY)
        {
            say("Mirador was too busy to answer within 120 s.");
            return EXIT_NO_ANSWER;
        }
        say_error("Mirador couldn't be reached", error);
        return EXIT_FAILED;
    }

    if (!owned_by_this_user(pipe))
    {
        CloseHandle(pipe);
        say("Another user's program holds Mirador's pipe, so Mirador can't be asked. Restart Windows.");
        return EXIT_FAILED;
    }

    if (!WriteFile(pipe, request, (DWORD)(sizeof request - 1), &written, NULL) || written != sizeof request - 1)
    {
        error = GetLastError();
        CloseHandle(pipe);
        say_error("Mirador couldn't be asked to start Monado", error);
        return EXIT_FAILED;
    }

    result = read_reply(pipe, line, (DWORD)sizeof line);
    CloseHandle(pipe);
    if (result >= 0)
    {
        return result;
    }

    if (strcmp(line, "OK") == 0)
    {
        Sleep(LINGER_MS);
        return EXIT_UP;
    }

    if (strncmp(line, "FAIL ", 5) == 0)
    {
        say(line + 5);
        return EXIT_FAILED;
    }

    say("Mirador answered something the stand-in doesn't know.");
    return EXIT_FAILED;
}
