// Copyright 2026, Finn Zeruhn
// SPDX-License-Identifier: BSL-1.0

#pragma once

#include <windows.h>

#include <functional>
#include <string>
#include <vector>

#include "check/options.hpp"
#include "check/report.hpp"
#include "core/api.hpp"
#include "core/display_session.hpp"

namespace dvl {

struct ModeResult {
    bool steps_ok = true;
    std::string failed_step;
    RunCounts counts;
};

void install_interrupt_handler();
bool interrupted();

BOOL on_interrupt_event(DWORD event);
void set_interrupt_stop_hook(std::function<void()> hook);
void clear_interrupted_for_tests();

ModeResult run_nvapi_mode(const Options& options, Output& out);

struct NvapiEnv {
    nvdm::DisplaySession* session = nullptr;
    std::vector<LUID> adapters;
    bool clear_back_buffers = true;
    int64_t warm_up_ns = 3'000'000'000;
    std::function<std::vector<std::string>()> session_lines;
};

int run_enable_direct_mode(nvdm::Api& api, Output& out);

ModeResult run_nvapi_check(const NvapiEnv& env, const Options& options, Output& out);

}
