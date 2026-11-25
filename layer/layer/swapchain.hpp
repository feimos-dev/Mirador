// Copyright 2026, Finn Zeruhn
// SPDX-License-Identifier: BSL-1.0

#pragma once

#include <vulkan/vulkan.h>

#include <array>
#include <atomic>
#include <condition_variable>
#include <cstdint>
#include <memory>
#include <mutex>
#include <string>
#include <utility>
#include <vector>

#include "core/display_loop.hpp"
#include "core/display_session.hpp"
#include "core/display_thread.hpp"
#include "core/fence_pool.hpp"
#include "core/frame_model.hpp"
#include "core/stats.hpp"
#include "core/trace.hpp"
#include "layer/dispatch.hpp"
#include "layer/event_table.hpp"
#include "layer/import.hpp"
#include "layer/log.hpp"
#include "layer/present_timing.hpp"
#include "layer/warnings.hpp"

namespace dvl {

inline constexpr uint32_t kImageCount = nvdm::kBackBufferCount;

struct SwapchainRecord {
    nvdm::SwapchainGen gen = 0;
    std::array<ImportedImage, kImageCount> images{};
    VkFormat format = VK_FORMAT_UNDEFINED;
    VkImageUsageFlags usage = 0;

    DeviceData* device = nullptr;
    VkSurfaceKHR surface = VK_NULL_HANDLE;
    VkPresentModeKHR present_mode = VK_PRESENT_MODE_FIFO_KHR;
    bool present_at = true;

    bool alive = true;
    bool images_moved = false;
    std::array<nvdm::FrameSeq, kImageCount> acquired{};
};

VkSwapchainKHR add_swapchain(std::shared_ptr<SwapchainRecord> record);
std::shared_ptr<SwapchainRecord> find_swapchain(VkSwapchainKHR swapchain);
std::shared_ptr<SwapchainRecord> remove_swapchain(VkSwapchainKHR swapchain);
std::vector<std::pair<VkSwapchainKHR, std::shared_ptr<SwapchainRecord>>> swapchains_of(const DeviceData& device);

inline uint64_t handle_key(VkFence fence)
{
    return static_cast<uint64_t>(reinterpret_cast<uintptr_t>(fence));
}

bool is_our_object(VkObjectType type, uint64_t handle);
bool is_our_debug_report_object(VkDebugReportObjectTypeEXT type, uint64_t handle);

class PendingLines {
public:
    void add(Level level, std::string line);
    void flush();

private:
    std::mutex mutex_;
    std::vector<std::pair<Level, std::string>> lines_;
};

class LayerWaiter : public nvdm::Waiter {
public:
    LayerWaiter(nvdm::Clock& clock, PendingLines& pending);
    bool wait_frame_or_until(int64_t deadline_ns) override;
    bool wait_frame_or_until_coarse(int64_t deadline_ns) override;
    void signal_frame_queued() override;
    bool high_resolution() const { return inner_.high_resolution(); }

private:
    nvdm::Clock& clock_;
    nvdm::Win32Waiter inner_;
    PendingLines& pending_;
};

class LayerFenceOps : public nvdm::FenceOps {
public:
    LayerFenceOps(DeviceData& device, nvdm::Clock& clock);
    bool wait(nvdm::FenceSlot slot, int64_t deadline_ns) override;
    bool status(nvdm::FenceSlot slot) override;

    DeviceData* device();
    bool leave(int64_t timeout_ns);

private:
    DeviceData* enter(nvdm::FenceSlot slot, VkFence& fence);
    void exit();

    std::mutex mutex_;
    std::condition_variable idle_;
    DeviceData* device_;
    bool in_call_ = false;
    nvdm::Clock& clock_;
};

class Presenter {
public:
    explicit Presenter(nvdm::DisplaySession& session);
    Presenter(const Presenter&) = delete;
    Presenter& operator=(const Presenter&) = delete;

    nvdm::DisplaySession& session;
    nvdm::Clock& clock;
    std::mutex& lock;
    const int64_t refresh_ns;
    std::condition_variable changed;

    nvdm::FrameModel model;
    nvdm::StatsWindow stats;
    EventTable events;
    PresentTimings timings;
    Warnings warnings;
    DeviceData* device = nullptr;
    uint32_t swapchains = 0;

    std::atomic<size_t> event_count{0};
    PendingLines pending;

    static constexpr int64_t kNever = INT64_MIN;
    std::atomic<uint64_t> fence_fallbacks{0};
    std::atomic<int64_t> fence_fallback_line_ns{kNever};

    std::mutex lifecycle;
    nvdm::DisplayThread* thread;
    LayerWaiter waiter;
    std::shared_ptr<LayerFenceOps> fence_ops;
    std::vector<std::shared_ptr<LayerFenceOps>> past_ops;

    void wait_changed(std::unique_lock<std::mutex>& hold, int64_t until_ns);

    std::unique_ptr<nvdm::Trace> trace;
    std::wstring trace_path;
    void note(nvdm::TraceKind kind, uint64_t a = 0, int64_t b = 0)
    {
        if (trace) {
            trace->add(kind, clock.now_ns(), a, b);
        }
    }
    void write_trace(size_t newest_at_most = SIZE_MAX);
};

Presenter& presenter();
Presenter* presenter_if_any();
void reset_presenter_for_tests();

class Locked {
public:
    enum class Flush { Now, Later };
    explicit Locked(Presenter& p, Flush flush = Flush::Now);
    ~Locked();
    Locked(const Locked&) = delete;
    Locked& operator=(const Locked&) = delete;

    std::unique_lock<std::mutex> hold;

private:
    Presenter& p_;
    Flush flush_;
};

int64_t deadline_after(nvdm::Clock& clock, uint64_t timeout_ns);

void before_device_destroyed(DeviceData& device);
void device_destroyed(DeviceData& device);

}
