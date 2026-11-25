// Copyright 2026, Finn Zeruhn
// SPDX-License-Identifier: BSL-1.0

#pragma once

#include <sal.h>

#include <cstdint>
#include <mutex>
#include <optional>
#include <string>
#include <vector>

#include "core/display_session.hpp"

namespace dvl {

class Output {
public:
    virtual ~Output() = default;
    virtual void line(const std::string& text) = 0;
};

class StdoutOutput final : public Output {
public:
    void line(const std::string& text) override;

private:
    std::mutex mutex_;
};

class CaptureOutput final : public Output {
public:
    void line(const std::string& text) override;
    std::vector<std::string> lines() const;
    std::string text() const;

private:
    mutable std::mutex mutex_;
    std::vector<std::string> lines_;
};

void say(Output& out, _Printf_format_string_ const char* format, ...);

struct HolderFindings {
    bool list_unreadable = false;
    bool display_held = false;
    bool board_held = false;
    bool monado_running = false;
    std::vector<std::string> blockers;
    std::vector<std::string> warnings;
    bool blocked() const { return list_unreadable || !blockers.empty(); }
};

HolderFindings holder_findings(const std::vector<std::string>& processes);

inline constexpr double kRefreshHz = 90.0;

struct RunCounts {
    uint64_t presents = 0;
    uint64_t flips = 0;
    uint64_t failures = 0;
    uint64_t link_drops = 0;
    double seconds = 0;
    double refresh_hz = kRefreshHz;
    double requested_seconds = 0;
};

uint64_t expected_flips(double seconds, double refresh_hz = kRefreshHz);

struct Verdict {
    bool pass = false;
    bool not_used = false;
    std::string line;
};

Verdict judge(bool steps_ok, const std::string& failed_step, const RunCounts& counts);

std::vector<std::string> describe_open_result(const nvdm::OpenResult& result);

std::string driver_version_from_lines(const std::vector<std::string>& lines);

std::vector<std::string> explain_failure(const std::string& line);

}
