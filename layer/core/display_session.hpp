// Copyright 2026, Finn Zeruhn
// SPDX-License-Identifier: BSL-1.0

#pragma once

#include <windows.h>
#include <unknwn.h>

#include <atomic>
#include <cstdint>
#include <functional>
#include <mutex>
#include <string>
#include <vector>

#include "api.hpp"
#include "clock.hpp"
#include "holders.hpp"
#include "product_table.hpp"

namespace nvdm {

inline constexpr const char* kTestedDrivers[] = {"621.01"};

bool is_tested_driver(const std::string& version);

enum class OpenStep {
    Holders,
    LoadNvapi,
    CheckIds,
    Enumerate,
    Luid,
    Product,
    EnableDirectMode,
    D3D11,
    BindDevice,
    CreateBackBuffers,
    SetDisplayMode,
    Done,
};

const char* step_name(OpenStep step);

struct OpenResult {
    bool ok;
    OpenStep step;
    int code;
    std::string message;
};

struct SessionDeps {
    Api& api;
    Clock& clock;

    std::function<IUnknown*(const LUID&)> make_d3d11;

    std::function<std::vector<std::string>()> processes;
    std::function<void()> pin_module;
    std::function<bool()> board_present;
    bool ignore_holders = false;
    bool skip_edid = false;
    uint32_t wait_ms = 10000;
    std::function<void(int64_t ns)> sleep;

    std::function<void(const std::string&)> log;
};

class DisplaySession {
public:
    explicit DisplaySession(SessionDeps deps);
    DisplaySession(const DisplaySession&) = delete;
    DisplaySession& operator=(const DisplaySession&) = delete;

    OpenResult open(const LUID& queried);

    bool is_open() const { return open_.load(std::memory_order_acquire); }

    const Product& product() const { return product_; }
    LUID luid() const { return luid_; }
    const BackBuffer& back_buffer(uint32_t index) const;
    Mode mode() const;
    EdidRoute edid_route() const { return edid_route_; }

    Api& api() { return deps_.api; }
    Clock& clock() { return deps_.clock; }

    std::mutex& lock() { return lock_; }

private:
    OpenResult run_steps(const LUID& queried);
    OpenResult check_holders();
    OpenResult load_nvapi();
    OpenResult check_ids();
    OpenResult find_display();
    void enable_all_vendors();
    OpenResult check_adapter(const LUID& queried);
    OpenResult identify_product();
    OpenResult enable_direct_mode();
    OpenResult make_device();
    OpenResult bind_device();
    OpenResult create_back_buffers();
    OpenResult set_display_mode();

    OpenResult proceed(OpenStep step) const { return OpenResult{true, step, 0, std::string()}; }
    OpenResult fail(OpenStep step, int code, const std::string& detail);
    void log(const std::string& line) const;
    void report_driver_version();

    SessionDeps deps_;
    std::mutex lock_;
    std::mutex open_mutex_;
    std::atomic<bool> open_{false};

    bool pinned_ = false;
    bool nvapi_loaded_ = false;
    bool ids_checked_ = false;
    uint32_t display_id_ = 0;
    const DirectModeVendor* vendor_ = nullptr;
    bool luid_known_ = false;
    LUID luid_{};
    size_t display_count_ = 0;
    bool product_known_ = false;
    bool skip_edid_logged_ = false;
    Product product_{0, 0, "", 0, 0, 0};
    EdidRoute edid_route_ = EdidRoute::None;
    bool direct_mode_enabled_ = false;
    IUnknown* device_ = nullptr;
    bool bound_ = false;
    std::vector<BackBuffer> back_buffers_;
    std::string done_message_;
};

struct SessionSettings {
    uint32_t wait_ms = 10000;
    bool ignore_holders = false;
    bool skip_edid = false;
};
SessionSettings parse_session_settings(const char* wait_ms, const char* ignore_holders, const char* no_edid = nullptr);

void set_process_session_log(std::function<void(const std::string&)> sink);

DisplaySession& process_session();

}
