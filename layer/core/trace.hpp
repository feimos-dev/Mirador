// Copyright 2026, Finn Zeruhn
// SPDX-License-Identifier: BSL-1.0

#pragma once

#include <atomic>
#include <cstddef>
#include <cstdint>
#include <functional>
#include <memory>
#include <mutex>
#include <string>

namespace nvdm {

enum class TraceKind : uint32_t {
    Acquire = 1,
    AcquireSubmitted = 2,
    PresentEnter = 3,
    PresentQueued = 4,
    PresentReturn = 5,
    PresentTarget = 6,
    Step1Wait = 10,
    Step1Woke = 11,
    FenceWait = 12,
    FenceDone = 13,
    NvPresentStart = 20,
    NvPresentReturn = 21,
    PollBegin = 30,
    Counter = 31,
    Credit = 32,
    PollEnd = 33,
    PresentWaitEnter = 40,
    PresentWaitReturn = 41,
    EventRegister = 50,
    EventWake = 51,
};

inline constexpr int64_t kFenceNotSignalled = 0;
inline constexpr int64_t kFenceSignalled = 1;
inline constexpr int64_t kFenceStatusSignalled = 3;

class Trace {
public:
    explicit Trace(size_t capacity, std::function<void()> on_full = {});
    Trace(const Trace&) = delete;
    Trace& operator=(const Trace&) = delete;

    void add(TraceKind kind, int64_t t_ns, uint64_t a = 0, int64_t b = 0) noexcept;

    size_t size() const;
    uint64_t dropped() const;

    struct Written {
        bool ok = false;
        size_t records = 0;
        size_t skipped = 0;
    };
    Written append_csv(const std::wstring& path, size_t newest_at_most = SIZE_MAX);

    struct Record {
        int64_t t_ns;
        uint64_t a;
        int64_t b;
        std::atomic<uint32_t> kind;
    };
    const Record& at(size_t i) const;

private:
    std::unique_ptr<Record[]> records_;
    size_t capacity_;
    std::function<void()> on_full_;
    std::atomic<size_t> next_{0};

    std::mutex write_mutex_;
    size_t written_ = 0;
    bool header_written_ = false;
};

}
