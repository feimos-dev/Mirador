// Copyright 2026, Finn Zeruhn
// SPDX-License-Identifier: BSL-1.0

#include "clock.hpp"

#include <windows.h>

namespace nvdm {

int64_t qpc_ticks_to_ns(int64_t ticks, int64_t frequency)
{
    if (frequency <= 0) {
        return 0;
    }
    constexpr int64_t kNsPerSecond = 1'000'000'000;
    const int64_t seconds = ticks / frequency;
    const int64_t rest = ticks % frequency;
    return seconds * kNsPerSecond + rest * kNsPerSecond / frequency;
}

QpcClock::QpcClock()
{
    LARGE_INTEGER frequency{};
    QueryPerformanceFrequency(&frequency);
    frequency_ = frequency.QuadPart;
}

int64_t QpcClock::now_ns()
{
    LARGE_INTEGER counter{};
    QueryPerformanceCounter(&counter);
    return qpc_ticks_to_ns(counter.QuadPart, frequency_);
}

}
