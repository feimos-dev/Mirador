// Copyright 2026, Finn Zeruhn
// SPDX-License-Identifier: BSL-1.0

#pragma once

#include <cstdint>
#include <deque>
#include <optional>
#include <string>
#include <vector>

#include "frame_model.hpp"

namespace nvdm {

inline constexpr int64_t kSessionGapNs = 1'000'000'000;
inline constexpr uint64_t kSessionLinePresents = 90;
inline constexpr uint64_t kWindowPresents = 2700;

class StatsWindow {
public:
    void on_present(int64_t now_ns);
    void on_refresh(uint32_t counter, int64_t qpc_ns);
    void on_wake(int64_t delta_ns);
    void on_counters(const FrameModel::Counters& counters);

    std::optional<std::string> take_line();

private:
    void open_window(int64_t now_ns, uint64_t size);
    void close_window(int64_t now_ns);

    bool in_session_ = false;
    int64_t last_present_ns_ = 0;

    int64_t window_start_ns_ = 0;
    uint64_t window_size_ = 0;
    uint64_t window_presents_ = 0;
    uint64_t window_flips_start_ = 0;
    FrameModel::Counters window_counters_start_;
    std::vector<int64_t> wakes_;
    int64_t max_wake_ns_ = 0;

    bool have_counter_ = false;
    uint32_t counter_ = 0;
    uint64_t flips_ = 0;
    FrameModel::Counters counters_;

    std::deque<std::string> lines_;
};

}
