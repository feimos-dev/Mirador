// Copyright 2026, Finn Zeruhn
// SPDX-License-Identifier: BSL-1.0

#include <windows.h>

#include <atomic>
#include <mutex>

#include "check/modes.hpp"

namespace dvl {
namespace {

constexpr DWORD kStopHookWaitMs = 100;

std::atomic<bool> g_interrupted{false};

struct HookSlot {
    std::mutex mutex;
    std::function<void()> hook;
};

HookSlot& slot()
{
    static HookSlot* const s = new HookSlot;
    return *s;
}

DWORD WINAPI run_stop_hook(LPVOID)
{
    try {
        HookSlot& s = slot();
        std::lock_guard<std::mutex> hold(s.mutex);
        if (s.hook) {
            s.hook();
        }
    } catch (...) {
    }
    return 0;
}

void stop_before_exit()
{
    HANDLE thread = CreateThread(nullptr, 0, run_stop_hook, nullptr, 0, nullptr);
    if (thread == nullptr) {
        return;
    }
    WaitForSingleObject(thread, kStopHookWaitMs);
    CloseHandle(thread);
}

BOOL WINAPI on_console_event(DWORD event)
{
    return on_interrupt_event(event);
}

}

BOOL on_interrupt_event(DWORD event)
{
    if (event == CTRL_CLOSE_EVENT) {
        g_interrupted = true;
        stop_before_exit();
        return FALSE;
    }
    if (event != CTRL_C_EVENT && event != CTRL_BREAK_EVENT) {
        return FALSE;
    }
    if (g_interrupted.exchange(true)) {
        stop_before_exit();
        return FALSE;
    }
    return TRUE;
}

void set_interrupt_stop_hook(std::function<void()> hook)
{
    HookSlot& s = slot();
    std::lock_guard<std::mutex> hold(s.mutex);
    s.hook = std::move(hook);
}

void clear_interrupted_for_tests()
{
    g_interrupted = false;
}

void install_interrupt_handler()
{
    SetConsoleCtrlHandler(on_console_event, TRUE);
}

bool interrupted()
{
    return g_interrupted.load();
}

}
