// Copyright 2026, Finn Zeruhn
// SPDX-License-Identifier: BSL-1.0

#pragma once

#include <cstdint>
#include <functional>
#include <map>
#include <optional>
#include <string>
#include <vector>

#include "fence_pool.hpp"

namespace nvdm {

enum class BufferState {
    Idle,
    Presented,
    OnScreen,
    Reserved,
};

enum class FrameState { Acquired, Queued, Presented, OnScreen };

enum class FrameEnd {
    Replaced,
    Retired,
    Dropped,
    Failed,
    Unconfirmed,
    Destroyed,
};

using SwapchainGen = uint64_t;
using FrameSeq = uint64_t;

struct FrameModelConfig {
    uint32_t buffers = 3;
    int64_t refresh_ns = 11'111'111;
    int64_t bound_ns = 600'000'000;
    int64_t stopped_ns = 600'000'000;
    uint32_t unconfirmed_after = 3;
};

struct Acquired {
    uint32_t buffer;
    FrameSeq seq;
    enum class Via {
        Free,
        DroppedQueued,
        Reserved,
        Presented,
    } via;
};

struct Candidate {
    FrameSeq seq;
    uint32_t buffer;
    FenceSlot fence;
};

struct FrameTimes {
    int64_t queue_done_ns = 0;
    int64_t dequeued_ns = 0;
    int64_t on_screen_ns = 0;
};

struct FrameReport {
    SwapchainGen gen;
    FrameSeq seq;
    FrameTimes times;
};

class FrameModel {
public:
    struct Counters {
        uint64_t presents = 0;
        uint64_t refreshes = 0;
        uint64_t missed = 0;
        uint64_t link_drops = 0;
        uint64_t dropped = 0;
        uint64_t failed = 0;
        uint64_t unconfirmed = 0;
        uint64_t reserved = 0;
        uint64_t held = 0;
        uint64_t target_skips = 0;
    };

    explicit FrameModel(FrameModelConfig config, FencePool* pool);

    void set_pool(FencePool* pool);
    uint64_t pool_serial() const;
    void set_log(std::function<void(const std::string&)> log);
    void set_report(std::function<void(const FrameReport&)> report);

    SwapchainGen add_swapchain();
    void remove_swapchain(SwapchainGen gen);

    std::optional<Acquired> acquire(SwapchainGen gen, int64_t now_ns);
    int64_t acquire_bound_ns() const;

    void release(FrameSeq seq);

    bool queue(FrameSeq seq, uint64_t present_id, FenceSlot fence, int64_t qpc_ns);
    void hold(FrameSeq seq, int64_t not_before_flip_ns);

    void fence_waiting(FenceSlot fence, bool on);
    void fence_seen(FenceSlot fence);

    std::optional<Candidate> candidate(int64_t now_ns) const;
    bool present_started(FrameSeq seq, int64_t qpc_ns);
    void present_returned(FrameSeq seq, int code, int64_t qpc_ns);

    void on_counter(uint32_t counter, int64_t qpc_ns);
    void thread_started(int64_t now_ns);
    void tick(int64_t now_ns);
    bool flips_stopped() const;
    bool has_queued() const;
    bool present_outstanding() const;

    void device_destroyed();

    uint64_t completed_present_id(SwapchainGen gen) const;
    BufferState buffer_state(uint32_t buffer) const;
    int64_t last_refresh_ns() const;
    int64_t last_credited_ns() const;
    std::vector<int64_t> drain_refreshes();
    Counters counters() const;

    std::optional<FrameState> frame_state(FrameSeq seq) const;
    std::optional<FrameEnd> frame_end(FrameSeq seq) const;
    std::optional<FrameTimes> frame_times(FrameSeq seq) const;
    uint64_t ids_completed() const;

    void check_invariants() const;

private:
    struct Buffer {
        BufferState state = BufferState::Idle;
        FrameSeq holder = 0;
        FrameSeq present_seq = 0;
        int64_t start_ns = 0;
        int64_t return_ns = 0;
        bool returned = false;
        int64_t on_screen_ns = 0;
        uint64_t uncredited = 0;
        uint64_t reserved_order = 0;
        int64_t not_before_ns = 0;
        int64_t reserved_ref_ns = 0;
        bool reserved_failed = false;
        uint64_t stamp = 0;
    };
    struct Frame {
        uint32_t buffer = 0;
        SwapchainGen gen = 0;
        FenceSlot fence = kNoFence;
        uint64_t present_id = 0;
        FrameState state = FrameState::Acquired;
        bool id_done = false;
        FrameTimes times;
        int64_t not_before_ns = 0;
    };

    std::optional<uint32_t> presented_buffer() const;
    std::optional<uint32_t> oldest_free() const;
    bool stopped_at(int64_t now_ns) const;
    bool present_allowed() const;
    std::optional<FrameSeq> newest_queued() const;
    bool shown_at_next_flip(const Frame& frame) const;
    std::optional<FrameSeq> newest_presentable() const;
    Acquired take(uint32_t buffer, SwapchainGen gen, Acquired::Via via);
    void end_frame(FrameSeq seq, FrameEnd end);
    void complete_id(Frame& frame);
    void make_idle(uint32_t buffer);
    void make_reserved(uint32_t buffer, int64_t ref_ns, bool failed);
    void credit(uint32_t buffer, int64_t qpc_ns);
    void free_older_reserved(int64_t qpc_ns);
    void enter_stopped(int64_t now_ns);
    void drop_queued_for_stop();
    void say(const std::string& line) const;
    void report(FrameSeq seq, const Frame& frame) const;

    FrameModelConfig config_;
    FencePool* pool_;
    uint64_t pool_serial_ = 0;
    std::function<void(const std::string&)> log_;
    std::function<void(const FrameReport&)> report_;
    std::vector<Buffer> buffers_;
    std::map<FrameSeq, Frame> frames_;
    std::map<FrameSeq, FrameEnd> ended_;
    std::map<SwapchainGen, uint64_t> completed_;
    mutable std::map<SwapchainGen, uint64_t> checked_completed_;
    SwapchainGen next_gen_ = 1;
    FrameSeq next_seq_ = 1;
    uint64_t stamp_ = 0;
    uint64_t reserved_order_ = 0;
    uint64_t ids_completed_ = 0;

    bool started_ = false;
    bool have_counter_ = false;
    uint32_t counter_ = 0;
    uint64_t refresh_count_ = 0;
    int64_t last_refresh_ns_ = 0;
    int64_t last_credited_ns_ = 0;
    bool have_flip_ = false;
    int64_t flip_ns_ = 0;
    bool prev_credit_valid_ = false;
    int64_t prev_credit_ns_ = 0;
    uint64_t prev_credit_count_ = 0;
    std::vector<int64_t> refreshes_;

    bool wait_increment_ = false;
    int64_t wait_after_ns_ = 0;
    bool stopped_ = false;
    bool stopped_present_used_ = false;
    bool failed_logged_ = false;
    int64_t failed_logged_ns_ = 0;

    Counters counters_;
};

}
