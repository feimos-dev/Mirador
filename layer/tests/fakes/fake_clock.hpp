// Copyright 2026, Finn Zeruhn
// SPDX-License-Identifier: BSL-1.0

#pragma once

#include <atomic>
#include <chrono>
#include <cstdint>

#include "core/clock.hpp"

namespace nvdm {

class FakeClock : public Clock {
public:
    explicit FakeClock(int64_t start_ns = 0) : now_ns_(start_ns) {}

    int64_t now_ns() override { return now_ns_.load(); }
    void advance(int64_t ns) { now_ns_ += ns; }

private:
    std::atomic<int64_t> now_ns_;
};

class RunningClock : public FakeClock {
public:
    explicit RunningClock(int64_t start_ns = 0) : FakeClock(start_ns), origin_(std::chrono::steady_clock::now()) {}

    int64_t now_ns() override
    {
        const auto elapsed = std::chrono::steady_clock::now() - origin_;
        return FakeClock::now_ns() + std::chrono::duration_cast<std::chrono::nanoseconds>(elapsed).count();
    }

private:
    std::chrono::steady_clock::time_point origin_;
};

}
