// Copyright 2026, Finn Zeruhn
// SPDX-License-Identifier: BSL-1.0

#pragma once

#include <windows.h>

#include <functional>

namespace dvl {

inline constexpr DWORD kConsoleHookWaitMs = 100;

void set_console_stop_hook(std::function<void()> hook);

BOOL handle_console_event(DWORD event);

bool install_console_handler();
void remove_console_handler();
bool console_handler_installed();

void on_process_detach(void* reserved);

}
