// Copyright 2026, Finn Zeruhn
// SPDX-License-Identifier: BSL-1.0

#pragma once

#include <windows.h>

#include <atomic>
#include <cstdint>
#include <functional>
#include <mutex>
#include <optional>
#include <string>
#include <vector>

#include "api.hpp"
#include "clock.hpp"
#include "fence_pool.hpp"
#include "frame_model.hpp"
#include "stats.hpp"
#include "trace.hpp"

namespace nvdm {

class Waiter {
public:
    virtual ~Waiter() = default;
    virtual bool wait_frame_or_until(int64_t deadline_ns) = 0;
    virtual bool wait_frame_or_until_coarse(int64_t deadline_ns) = 0;
    virtual void signal_frame_queued() = 0;
};

class Win32Waiter : public Waiter {
public:
    explicit Win32Waiter(Clock& clock);
    ~Win32Waiter() override;
    Win32Waiter(const Win32Waiter&) = delete;
    Win32Waiter& operator=(const Win32Waiter&) = delete;

    bool wait_frame_or_until(int64_t deadline_ns) override;
    bool wait_frame_or_until_coarse(int64_t deadline_ns) override;
    void signal_frame_queued() override;

    bool high_resolution() const { return high_resolution_; }

private:
    Clock& clock_;
    HANDLE event_ = nullptr;
    HANDLE timer_ = nullptr;
    bool high_resolution_ = false;
};

class FenceOps {
public:
    virtual ~FenceOps() = default;
    virtual bool wait(FenceSlot slot, int64_t deadline_ns) = 0;
    virtual bool status(FenceSlot slot) = 0;
};

struct DisplayLoopConfig {
    std::vector<uint64_t> tags;
    int64_t refresh_ns = 11'111'111;
    int64_t lead_ns = 1'000'000;
    int64_t poll_after_ns = 2'000'000;
    int64_t poll_step_ns = 100'000;
    int64_t idle_poll_ns = 3'000'000;
    int64_t stopped_poll_ns = 4'000'000;

    std::function<void(const std::string&)> log;
    std::function<void(int64_t qpc_ns)> refresh_seen;
    std::function<bool()> events_pending;
    Trace* trace = nullptr;
};

class DisplayLoop {
public:
    DisplayLoop(Api& api, FrameModel& model, std::mutex& lock, Clock& clock, Waiter& waiter, FenceOps& fences,
                StatsWindow& stats, DisplayLoopConfig config = {});
    DisplayLoop(const DisplayLoop&) = delete;
    DisplayLoop& operator=(const DisplayLoop&) = delete;

    void pass();

    void request_stop();
    bool stop_requested() const;

    bool idle() const;

    void abandon();

private:
    enum class Mode {
        Active,
        Idle,
        Stopped,
    };
    struct Plan {
        int64_t now = 0;
        int64_t target = 0;
        Mode mode = Mode::Idle;
        bool ready = false;
    };

    void begin();
    Plan plan();
    int64_t predict(int64_t last_refresh_ns, int64_t now_ns) const;
    bool present_newest(int64_t deadline_ns, bool wait);
    void poll(int64_t target_ns);
    bool read_counter();
    void take_reads(int code, const RefreshStats& stats, int64_t now_ns);
    void say(std::string line);
    void flush_log();
    void note(TraceKind kind, uint64_t a = 0, int64_t b = 0)
    {
        if (config_.trace != nullptr) {
            config_.trace->add(kind, clock_.now_ns(), a, b);
        }
    }

    Api& api_;
    FrameModel& model_;
    std::mutex& lock_;
    Clock& clock_;
    Waiter& waiter_;
    FenceOps& fences_;
    StatsWindow& stats_;
    DisplayLoopConfig config_;

    std::atomic<bool> stop_{false};
    std::atomic<bool> abandoned_{false};
    std::atomic<bool> idle_{false};

    bool started_ = false;
    bool read_failing_ = false;
    int64_t read_failed_logged_ns_ = 0;
    bool future_logged_ = false;
    int64_t future_logged_ns_ = 0;
    std::vector<std::string> pending_log_;
    FrameSeq traced_present_ = 0;
};

}
