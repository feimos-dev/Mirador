// Copyright 2026, Finn Zeruhn
// SPDX-License-Identifier: BSL-1.0

#pragma once

#include <vulkan/vulkan.h>

#include <cstdint>
#include <map>
#include <set>
#include <string>
#include <vector>

#include "core/frame_model.hpp"

namespace dvl {

class Warnings {
public:
    static constexpr int64_t kAppSessionGapNs = 1'000'000'000;
    static constexpr uint64_t kCheckAfterPresents = 90;
    static constexpr int64_t kEventWaitNs = 2'000'000'000;

    void set_display_control(bool enabled);
    void set_present_mode(VkPresentModeKHR mode);
    void set_memory_fallback(bool used);

    void on_present(int64_t now_ns);
    void on_present_wait();
    void on_event_registered(uint64_t key, int64_t now_ns);
    void on_event_waited(uint64_t key);
    void on_event_gone(uint64_t key);
    void check(int64_t now_ns);
    void on_counters(const nvdm::FrameModel::Counters& counters, bool flips_stopped);
    void on_fence_fallback();
    void on_thread_left_behind();

    std::vector<std::string> take_lines();

private:
    enum class Kind {
        NoDisplayControl,
        PresentMode,
        MemoryFallback,
        NoDisplayEvent,
        NoPresentWait,
        EventNotWaited,
        FlipsStopped,
        Dropped,
        Failed,
        Unconfirmed,
        Reserved,
        FenceFallback,
        ThreadLeftBehind,
    };
    void fire(Kind kind, std::string line);
    void begin_session();

    bool display_control_ = true;
    VkPresentModeKHR present_mode_ = VK_PRESENT_MODE_FIFO_KHR;
    bool memory_fallback_ = false;

    bool in_session_ = false;
    int64_t last_present_ns_ = 0;
    uint64_t presents_ = 0;
    bool saw_event_ = false;
    bool saw_present_wait_ = false;
    std::set<Kind> fired_;
    std::map<uint64_t, int64_t> unwaited_;
    nvdm::FrameModel::Counters counters_;
    std::vector<std::string> lines_;
};

}
