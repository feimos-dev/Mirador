// Copyright 2026, Finn Zeruhn
// SPDX-License-Identifier: BSL-1.0

#include "display_loop.hpp"

#include <algorithm>
#include <cstdarg>
#include <cstdio>
#include <utility>

#ifndef CREATE_WAITABLE_TIMER_HIGH_RESOLUTION
#define CREATE_WAITABLE_TIMER_HIGH_RESOLUTION 0x00000002
#endif

namespace nvdm {
namespace {

constexpr int64_t kSpinBelowNs = 1'000'000;
constexpr DWORD kTimerSlackMs = 5;
constexpr int64_t kReadFailLogNs = 1'000'000'000;

std::string format(const char* fmt, ...)
{
    char text[256];
    va_list args;
    va_start(args, fmt);
    vsnprintf(text, sizeof(text), fmt, args);
    va_end(args);
    return text;
}

DWORD ceil_ms(int64_t ns)
{
    if (ns <= 0) {
        return 0;
    }
    const int64_t ms = (ns + 999'999) / 1'000'000;
    return static_cast<DWORD>(std::min<int64_t>(ms, 0x7fffffff));
}

}

Win32Waiter::Win32Waiter(Clock& clock) : clock_(clock)
{
    event_ = CreateEventW(nullptr, FALSE, FALSE, nullptr);
    timer_ = CreateWaitableTimerExW(nullptr, nullptr, CREATE_WAITABLE_TIMER_HIGH_RESOLUTION, TIMER_ALL_ACCESS);
    high_resolution_ = timer_ != nullptr;
    if (timer_ == nullptr) {
        timer_ = CreateWaitableTimerExW(nullptr, nullptr, 0, TIMER_ALL_ACCESS);
    }
}

Win32Waiter::~Win32Waiter()
{
    if (timer_ != nullptr) {
        CloseHandle(timer_);
    }
    if (event_ != nullptr) {
        CloseHandle(event_);
    }
}

bool Win32Waiter::wait_frame_or_until(int64_t deadline_ns)
{
    for (;;) {
        const int64_t left = deadline_ns - clock_.now_ns();
        if (left <= 0) {
            return WaitForSingleObject(event_, 0) == WAIT_OBJECT_0;
        }
        if (left < kSpinBelowNs) {
            if (WaitForSingleObject(event_, 0) == WAIT_OBJECT_0) {
                return true;
            }
            SwitchToThread();
            continue;
        }
        DWORD result = WAIT_TIMEOUT;
        LARGE_INTEGER due{};
        due.QuadPart = -(left / 100);
        if (timer_ != nullptr && SetWaitableTimerEx(timer_, &due, 0, nullptr, nullptr, nullptr, 0)) {
            const HANDLE handles[2] = {event_, timer_};
            result = WaitForMultipleObjects(2, handles, FALSE, ceil_ms(left) + kTimerSlackMs);
            if (result == WAIT_OBJECT_0) {
                CancelWaitableTimer(timer_);
            }
        } else {
            result = WaitForSingleObject(event_, ceil_ms(left));
        }
        if (result == WAIT_OBJECT_0) {
            return true;
        }
        if (result == WAIT_FAILED) {
            Sleep(ceil_ms(left));
            return false;
        }
    }
}

bool Win32Waiter::wait_frame_or_until_coarse(int64_t deadline_ns)
{
    for (;;) {
        const int64_t left = deadline_ns - clock_.now_ns();
        if (left <= 0) {
            return WaitForSingleObject(event_, 0) == WAIT_OBJECT_0;
        }
        const DWORD result = WaitForSingleObject(event_, ceil_ms(left));
        if (result == WAIT_OBJECT_0) {
            return true;
        }
        if (result == WAIT_FAILED) {
            Sleep(ceil_ms(left));
            return false;
        }
    }
}

void Win32Waiter::signal_frame_queued()
{
    SetEvent(event_);
}

DisplayLoop::DisplayLoop(Api& api, FrameModel& model, std::mutex& lock, Clock& clock, Waiter& waiter,
                         FenceOps& fences, StatsWindow& stats, DisplayLoopConfig config)
    : api_(api), model_(model), lock_(lock), clock_(clock), waiter_(waiter), fences_(fences), stats_(stats),
      config_(std::move(config))
{
}

void DisplayLoop::request_stop()
{
    stop_.store(true);
    waiter_.signal_frame_queued();
}

bool DisplayLoop::stop_requested() const
{
    return stop_.load();
}

bool DisplayLoop::idle() const
{
    return idle_.load();
}

void DisplayLoop::abandon()
{
    stop_.store(true);
    std::lock_guard<std::mutex> hold(lock_);
    abandoned_.store(true);
}

void DisplayLoop::pass()
{
    if (stop_requested()) {
        return;
    }
    if (!started_) {
        begin();
        if (stop_requested()) {
            return;
        }
    }

    Plan p = plan();
    if (!p.ready) {
        int64_t until = p.now + config_.idle_poll_ns;
        if (p.mode == Mode::Active) {
            until = p.target - config_.lead_ns;
        } else if (p.mode == Mode::Stopped) {
            until = p.now + config_.stopped_poll_ns;
        }
        note(TraceKind::Step1Wait, p.mode == Mode::Active ? 0 : p.mode == Mode::Idle ? 1 : 2, until);
        const bool woke = p.mode == Mode::Idle ? waiter_.wait_frame_or_until_coarse(until)
                                               : waiter_.wait_frame_or_until(until);
        note(TraceKind::Step1Woke, woke ? 1 : 0);
        if (stop_requested()) {
            return;
        }
        p = plan();
    }

    if (p.ready) {
        const bool stopped = p.mode == Mode::Stopped;
        const int64_t until = stopped ? p.now + config_.stopped_poll_ns : p.target - config_.lead_ns;
        const bool presented = present_newest(until, true);
        if (stop_requested()) {
            return;
        }
        if (stopped && !presented) {
            waiter_.wait_frame_or_until(until);
            if (stop_requested()) {
                return;
            }
        }
    }

    if (p.mode != Mode::Active) {
        read_counter();
    } else if (clock_.now_ns() >= p.target - config_.lead_ns) {
        poll(p.target);
    }
}

void DisplayLoop::begin()
{
    RefreshStats stats{};
    const int code = api_.present_stats(stats);
    {
        std::lock_guard<std::mutex> hold(lock_);
        if (abandoned_.load()) {
            return;
        }
        const int64_t now = clock_.now_ns();
        model_.thread_started(now);
        take_reads(code, stats, now);
    }
    flush_log();
    started_ = true;
}

DisplayLoop::Plan DisplayLoop::plan()
{
    Plan p;
    {
        std::lock_guard<std::mutex> hold(lock_);
        p.now = clock_.now_ns();
        model_.tick(p.now);
        stats_.on_counters(model_.counters());
        p.target = predict(model_.last_refresh_ns(), p.now);
        const int64_t period = std::max<int64_t>(config_.refresh_ns, 1);
        const int64_t overdue = p.target - period;
        if (overdue > model_.last_refresh_ns() && p.now < overdue + config_.poll_after_ns) {
            p.target = overdue;
        }
        p.ready = model_.candidate(p.now).has_value();
        if (model_.flips_stopped()) {
            p.mode = Mode::Stopped;
        } else if (model_.has_queued() || model_.present_outstanding() ||
                   (config_.events_pending && config_.events_pending())) {
            p.mode = Mode::Active;
        } else {
            p.mode = Mode::Idle;
        }
    }
    idle_.store(p.mode == Mode::Idle);
    return p;
}

int64_t DisplayLoop::predict(int64_t last_refresh_ns, int64_t now_ns) const
{
    const int64_t period = std::max<int64_t>(config_.refresh_ns, 1);
    const auto mod = [period](int64_t value) {
        const int64_t rest = value % period;
        return rest < 0 ? rest + period : rest;
    };
    const int64_t since = mod(mod(now_ns) - mod(last_refresh_ns));
    return now_ns + (period - since);
}

bool DisplayLoop::present_newest(int64_t deadline_ns, bool wait)
{
    std::optional<Candidate> c;
    uint64_t pool_serial = 0;
    {
        std::lock_guard<std::mutex> hold(lock_);
        if (abandoned_.load()) {
            return false;
        }
        c = model_.candidate(clock_.now_ns());
        if (!c) {
            return false;
        }
        pool_serial = model_.pool_serial();
        model_.fence_waiting(c->fence, true);
    }

    bool ready = true;
    if (c->fence != kNoFence) {
        if (wait) {
            note(TraceKind::FenceWait, c->seq, deadline_ns);
            ready = fences_.wait(c->fence, deadline_ns);
            note(TraceKind::FenceDone, c->seq, ready ? kFenceSignalled : kFenceNotSignalled);
        } else {
            ready = fences_.status(c->fence);
            if (ready) {
                note(TraceKind::FenceDone, c->seq, kFenceStatusSignalled);
            }
        }
    }

    bool started = false;
    {
        std::lock_guard<std::mutex> hold(lock_);
        if (abandoned_.load()) {
            return false;
        }
        if (model_.pool_serial() == pool_serial) {
            if (ready) {
                model_.fence_seen(c->fence);
            }
            model_.fence_waiting(c->fence, false);
        }
        if (ready && !stop_requested()) {
            const int64_t now = clock_.now_ns();
            started = model_.present_started(c->seq, now);
            if (started) {
                stats_.on_counters(model_.counters());
                stats_.on_present(now);
                while (std::optional<std::string> line = stats_.take_line()) {
                    say(std::move(*line));
                }
            }
        }
    }
    if (!started) {
        flush_log();
        if (wait && !ready && !stop_requested() && clock_.now_ns() < deadline_ns) {
            waiter_.wait_frame_or_until(deadline_ns);
        }
        return false;
    }

    int code = kBadArgument;
    if (c->buffer < config_.tags.size()) {
        note(TraceKind::NvPresentStart, c->seq, c->buffer);
        code = api_.present(config_.tags[c->buffer]);
    } else {
        say(format("no NVAPI tag for back-buffer %u: its Present fails", c->buffer));
    }
    {
        std::lock_guard<std::mutex> hold(lock_);
        if (abandoned_.load()) {
            return true;
        }
        const int64_t returned = clock_.now_ns();
        model_.present_returned(c->seq, code, returned);
        stats_.on_counters(model_.counters());
        if (config_.trace != nullptr) {
            config_.trace->add(TraceKind::NvPresentReturn, returned, c->seq, code);
            traced_present_ = c->seq;
        }
    }
    flush_log();
    return true;
}

void DisplayLoop::poll(int64_t target_ns)
{
    const int64_t until = target_ns + config_.poll_after_ns;
    note(TraceKind::PollBegin, 0, target_ns);
    for (;;) {
        if (stop_requested()) {
            return;
        }
        if (read_counter()) {
            note(TraceKind::PollEnd, 1);
            return;
        }
        if (stop_requested()) {
            return;
        }
        present_newest(0, false);
        if (stop_requested()) {
            return;
        }
        const int64_t now = clock_.now_ns();
        if (now >= until) {
            note(TraceKind::PollEnd, 0);
            return;
        }
        waiter_.wait_frame_or_until(std::min(now + config_.poll_step_ns, until));
    }
}

bool DisplayLoop::read_counter()
{
    RefreshStats stats{};
    const int code = api_.present_stats(stats);
    bool moved = false;
    {
        std::lock_guard<std::mutex> hold(lock_);
        if (abandoned_.load()) {
            return false;
        }
        const int64_t now = clock_.now_ns();
        const uint64_t refreshes = model_.counters().refreshes;
        take_reads(code, stats, now);
        moved = model_.counters().refreshes != refreshes;
        model_.tick(now);
        stats_.on_counters(model_.counters());
        if (config_.trace != nullptr && moved) {
            config_.trace->add(TraceKind::Counter, now, stats.counter, stats.qpc_ns);
            if (traced_present_ != 0 && model_.frame_state(traced_present_) != FrameState::Presented) {
                if (model_.frame_state(traced_present_) == FrameState::OnScreen) {
                    config_.trace->add(TraceKind::Credit, now, traced_present_, stats.qpc_ns);
                }
                traced_present_ = 0;
            }
        }
    }
    flush_log();
    return moved;
}

void DisplayLoop::take_reads(int code, const RefreshStats& stats, int64_t now_ns)
{
    if (code == 0) {
        read_failing_ = false;
        int64_t qpc = stats.qpc_ns;
        const int64_t latest = now_ns + std::max<int64_t>(config_.refresh_ns, 1);
        if (qpc > latest) {
            if (!future_logged_ || now_ns - future_logged_ns_ >= kReadFailLogNs) {
                say(format("GetPresentStats gave a refresh time %.1f ms in the future; taken as one refresh period "
                           "from now (logged at most once a second)",
                           static_cast<double>(qpc - now_ns) / 1e6));
                future_logged_ = true;
                future_logged_ns_ = now_ns;
            }
            qpc = latest;
        }
        model_.on_counter(stats.counter, qpc);
        stats_.on_refresh(stats.counter, qpc);
    } else if (!read_failing_ || now_ns - read_failed_logged_ns_ >= kReadFailLogNs) {
        say(format("GetPresentStats returned %d (logged at most once a second)", code));
        read_failing_ = true;
        read_failed_logged_ns_ = now_ns;
    }
    for (const int64_t qpc : model_.drain_refreshes()) {
        if (config_.refresh_seen) {
            config_.refresh_seen(qpc);
        }
    }
    stats_.on_counters(model_.counters());
}

void DisplayLoop::say(std::string line)
{
    pending_log_.push_back(std::move(line));
}

void DisplayLoop::flush_log()
{
    if (pending_log_.empty()) {
        return;
    }
    std::vector<std::string> lines;
    lines.swap(pending_log_);
    if (config_.log) {
        for (const std::string& line : lines) {
            config_.log(line);
        }
    }
}

}
