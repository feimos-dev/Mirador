// Copyright 2026, Finn Zeruhn
// SPDX-License-Identifier: BSL-1.0

#pragma once

#include <vulkan/vulkan.h>

#include <cstddef>
#include <cstdint>
#include <deque>
#include <functional>
#include <map>
#include <optional>

#include "core/frame_model.hpp"

namespace dvl {

inline constexpr uint64_t kQpcTimeDomainId = 1;
inline constexpr uint64_t kStageLocalTimeDomainId = 2;
inline constexpr uint64_t kTimingPropertiesCounter = 1;
inline constexpr uint64_t kTimeDomainsCounter = 1;

inline constexpr VkPresentStageFlagsEXT kPresentStages = VK_PRESENT_STAGE_QUEUE_OPERATIONS_END_BIT_EXT |
                                                         VK_PRESENT_STAGE_REQUEST_DEQUEUED_BIT_EXT |
                                                         VK_PRESENT_STAGE_IMAGE_FIRST_PIXEL_OUT_BIT_EXT;

bool present_at_enabled();

void fill_present_timing_caps(VkPresentTimingSurfaceCapabilitiesEXT& out, bool present_at);

inline constexpr int64_t kTargetToleranceNs = 1'000'000;

VkResult fill_time_domains(VkSwapchainTimeDomainPropertiesEXT& out);

struct PresentRequest {
    uint64_t present_id = 0;
    bool timing = false;
    VkPresentTimingInfoFlagsEXT timing_flags = 0;
    uint64_t target_time = 0;
    uint64_t time_domain_id = 0;
    VkPresentStageFlagsEXT stages = 0;
};
PresentRequest read_present_request(const VkPresentInfoKHR& info);

inline constexpr int64_t kFarTargetNs = 500'000'000;

struct Target {
    enum class Use {
        None,
        Hold,
        Off,
        Relative,
        TooFar,
    } use = Use::None;
    int64_t not_before_ns = 0;
};
Target read_target(const PresentRequest& request, int64_t refresh_ns, int64_t now_ns, bool present_at);

class PresentTimings {
public:
    void add_swapchain(nvdm::SwapchainGen gen);
    void remove_swapchain(nvdm::SwapchainGen gen);

    VkResult set_queue_size(nvdm::SwapchainGen gen, uint32_t size);
    bool slot_free(nvdm::SwapchainGen gen, const PresentRequest& request) const;
    void queued(nvdm::SwapchainGen gen, nvdm::FrameSeq seq, const PresentRequest& request);
    void not_queued(nvdm::SwapchainGen gen, nvdm::FrameSeq seq);
    void on_report(const nvdm::FrameReport& report);

    using LiveTimes = std::function<std::optional<nvdm::FrameTimes>(nvdm::FrameSeq)>;
    VkResult past(nvdm::SwapchainGen gen, VkPastPresentationTimingFlagsEXT flags,
                  VkPastPresentationTimingPropertiesEXT& out, const LiveTimes& live);

    size_t slots_taken(nvdm::SwapchainGen gen) const;

private:
    struct Record {
        nvdm::FrameSeq seq;
        uint64_t present_id;
        uint64_t target_time;
        uint64_t time_domain_id;
        VkPresentStageFlagsEXT stages;
        nvdm::FrameTimes times;
        bool complete;
    };
    struct Swapchain {
        uint32_t queue_size = 0;
        std::deque<Record> records;
    };

    std::map<nvdm::SwapchainGen, Swapchain> swapchains_;
};

}
