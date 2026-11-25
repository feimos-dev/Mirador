// Copyright 2026, Finn Zeruhn
// SPDX-License-Identifier: BSL-1.0

#include <vulkan/vulkan.h>

#include <cstdint>
#include <memory>
#include <vector>

#include "core/clock.hpp"
#include "layer/dispatch.hpp"
#include "layer/intercepts.hpp"
#include "layer/log.hpp"
#include "layer/present_timing.hpp"
#include "layer/swapchain.hpp"

namespace dvl {
namespace {

bool names_our_swapchain(const void* next)
{
    for (auto* s = static_cast<const VkBaseInStructure*>(next); s != nullptr; s = s->pNext) {
        if (s->sType == VK_STRUCTURE_TYPE_SWAPCHAIN_CALIBRATED_TIMESTAMP_INFO_EXT &&
            find_swapchain(reinterpret_cast<const VkSwapchainCalibratedTimestampInfoEXT*>(s)->swapchain)) {
            return true;
        }
    }
    return false;
}

bool swapchain_local(VkTimeDomainKHR domain)
{
    return domain == VK_TIME_DOMAIN_SWAPCHAIN_LOCAL_EXT || domain == VK_TIME_DOMAIN_PRESENT_STAGE_LOCAL_EXT;
}

int64_t qpc_frequency()
{
    LARGE_INTEGER frequency{};
    QueryPerformanceFrequency(&frequency);
    return frequency.QuadPart;
}

VkResult calibrated_timestamps(const char* command, PFN_vkGetCalibratedTimestampsKHR down, VkDevice device,
                               uint32_t count, const VkCalibratedTimestampInfoKHR* infos, uint64_t* timestamps,
                               uint64_t* max_deviation)
{
    bool ours = false;
    for (uint32_t i = 0; i < count && !ours; ++i) {
        ours = names_our_swapchain(infos[i].pNext);
    }
    if (!ours) {
        return call_down(down, command, device, count, infos, timestamps, max_deviation);
    }

    constexpr size_t kNone = SIZE_MAX;
    std::vector<VkCalibratedTimestampInfoKHR> below;
    std::vector<size_t> from(count, kNone);
    std::vector<bool> local(count, false);
    size_t qpc = kNone;
    for (uint32_t i = 0; i < count; ++i) {
        if (names_our_swapchain(infos[i].pNext)) {
            if (swapchain_local(infos[i].timeDomain)) {
                local[i] = true;
                continue;
            }
            VkCalibratedTimestampInfoKHR plain = infos[i];
            plain.pNext = nullptr;
            from[i] = below.size();
            below.push_back(plain);
        } else {
            from[i] = below.size();
            below.push_back(infos[i]);
        }
        if (infos[i].timeDomain == VK_TIME_DOMAIN_QUERY_PERFORMANCE_COUNTER_KHR && qpc == kNone) {
            qpc = from[i];
        }
    }
    for (uint32_t i = 0; i < count; ++i) {
        if (!local[i]) {
            continue;
        }
        if (qpc == kNone) {
            qpc = below.size();
            VkCalibratedTimestampInfoKHR reading{VK_STRUCTURE_TYPE_CALIBRATED_TIMESTAMP_INFO_KHR};
            reading.timeDomain = VK_TIME_DOMAIN_QUERY_PERFORMANCE_COUNTER_KHR;
            below.push_back(reading);
        }
        from[i] = qpc;
    }

    std::vector<uint64_t> values(below.size());
    const VkResult r = call_down(down, command, device, static_cast<uint32_t>(below.size()), below.data(),
                                 values.data(), max_deviation);
    if (r != VK_SUCCESS) {
        return r;
    }
    const int64_t frequency = qpc_frequency();
    for (uint32_t i = 0; i < count; ++i) {
        const uint64_t value = values[from[i]];
        timestamps[i] = local[i] ? static_cast<uint64_t>(nvdm::qpc_ticks_to_ns(static_cast<int64_t>(value), frequency))
                                 : value;
    }
    return VK_SUCCESS;
}

}

VKAPI_ATTR VkResult VKAPI_CALL layer_SetSwapchainPresentTimingQueueSizeEXT(VkDevice device, VkSwapchainKHR swapchain,
                                                                          uint32_t size)
{
    return guarded("vkSetSwapchainPresentTimingQueueSizeEXT", [&] {
        if (std::shared_ptr<SwapchainRecord> record = find_swapchain(swapchain)) {
            Presenter& p = presenter();
            Locked lk(p);
            return p.timings.set_queue_size(record->gen, size);
        }
        DeviceData& d = device_data(device);
        return call_down(d.SetSwapchainPresentTimingQueueSizeEXT, "vkSetSwapchainPresentTimingQueueSizeEXT", device,
                         swapchain, size);
    });
}

VKAPI_ATTR VkResult VKAPI_CALL layer_GetSwapchainTimingPropertiesEXT(VkDevice device, VkSwapchainKHR swapchain,
                                                                     VkSwapchainTimingPropertiesEXT* properties,
                                                                     uint64_t* counter)
{
    return guarded("vkGetSwapchainTimingPropertiesEXT", [&] {
        if (find_swapchain(swapchain)) {
            const uint64_t period = static_cast<uint64_t>(presenter().refresh_ns);
            properties->refreshDuration = period;
            properties->refreshInterval = period;
            if (counter != nullptr) {
                *counter = kTimingPropertiesCounter;
            }
            return VK_SUCCESS;
        }
        DeviceData& d = device_data(device);
        return call_down(d.GetSwapchainTimingPropertiesEXT, "vkGetSwapchainTimingPropertiesEXT", device, swapchain,
                         properties, counter);
    });
}

VKAPI_ATTR VkResult VKAPI_CALL layer_GetSwapchainTimeDomainPropertiesEXT(
    VkDevice device, VkSwapchainKHR swapchain, VkSwapchainTimeDomainPropertiesEXT* properties, uint64_t* counter)
{
    return guarded("vkGetSwapchainTimeDomainPropertiesEXT", [&] {
        if (find_swapchain(swapchain)) {
            if (counter != nullptr) {
                *counter = kTimeDomainsCounter;
            }
            return fill_time_domains(*properties);
        }
        DeviceData& d = device_data(device);
        return call_down(d.GetSwapchainTimeDomainPropertiesEXT, "vkGetSwapchainTimeDomainPropertiesEXT", device,
                         swapchain, properties, counter);
    });
}

VKAPI_ATTR VkResult VKAPI_CALL layer_GetPastPresentationTimingEXT(VkDevice device,
                                                                  const VkPastPresentationTimingInfoEXT* info,
                                                                  VkPastPresentationTimingPropertiesEXT* properties)
{
    return guarded("vkGetPastPresentationTimingEXT", [&] {
        if (std::shared_ptr<SwapchainRecord> record = find_swapchain(info->swapchain)) {
            Presenter& p = presenter();
            Locked lk(p);
            return p.timings.past(record->gen, info->flags, *properties,
                                  [&p](nvdm::FrameSeq seq) { return p.model.frame_times(seq); });
        }
        DeviceData& d = device_data(device);
        return call_down(d.GetPastPresentationTimingEXT, "vkGetPastPresentationTimingEXT", device, info, properties);
    });
}

VKAPI_ATTR VkResult VKAPI_CALL layer_GetCalibratedTimestampsKHR(VkDevice device, uint32_t count,
                                                                const VkCalibratedTimestampInfoKHR* infos,
                                                                uint64_t* timestamps, uint64_t* max_deviation)
{
    return guarded("vkGetCalibratedTimestampsKHR", [&] {
        return calibrated_timestamps("vkGetCalibratedTimestampsKHR", device_data(device).GetCalibratedTimestampsKHR,
                                     device, count, infos, timestamps, max_deviation);
    });
}

VKAPI_ATTR VkResult VKAPI_CALL layer_GetCalibratedTimestampsEXT(VkDevice device, uint32_t count,
                                                                const VkCalibratedTimestampInfoEXT* infos,
                                                                uint64_t* timestamps, uint64_t* max_deviation)
{
    return guarded("vkGetCalibratedTimestampsEXT", [&] {
        return calibrated_timestamps("vkGetCalibratedTimestampsEXT", device_data(device).GetCalibratedTimestampsEXT,
                                     device, count, infos, timestamps, max_deviation);
    });
}

}
