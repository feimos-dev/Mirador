// Copyright 2026, Finn Zeruhn
// SPDX-License-Identifier: BSL-1.0

#pragma once

#include <vulkan/vulkan.h>

#include <exception>
#include <new>
#include <optional>
#include <type_traits>

#include "core/display_session.hpp"
#include "layer/dispatch.hpp"
#include "layer/log.hpp"

namespace dvl {

#define DVL_INSTANCE_INTERCEPTS(X)                 \
    X(CreateDevice)                                \
    X(GetPhysicalDeviceDisplayPropertiesKHR)       \
    X(GetPhysicalDeviceDisplayProperties2KHR)      \
    X(GetPhysicalDeviceDisplayPlanePropertiesKHR)  \
    X(GetPhysicalDeviceDisplayPlaneProperties2KHR) \
    X(GetDisplayPlaneSupportedDisplaysKHR)         \
    X(GetDisplayModePropertiesKHR)                 \
    X(GetDisplayModeProperties2KHR)                \
    X(CreateDisplayModeKHR)                        \
    X(GetDisplayPlaneCapabilitiesKHR)              \
    X(GetDisplayPlaneCapabilities2KHR)             \
    X(ReleaseDisplayEXT)                           \
    X(AcquireWinrtDisplayNV)                       \
    X(GetWinrtDisplayNV)                           \
    X(CreateDisplayPlaneSurfaceKHR)                \
    X(DestroySurfaceKHR)                           \
    X(GetPhysicalDeviceSurfaceSupportKHR)          \
    X(GetPhysicalDeviceSurfaceCapabilitiesKHR)     \
    X(GetPhysicalDeviceSurfaceFormatsKHR)          \
    X(GetPhysicalDeviceSurfacePresentModesKHR)     \
    X(GetPhysicalDeviceSurfaceCapabilities2KHR)    \
    X(GetPhysicalDeviceSurfaceFormats2KHR)         \
    X(GetPhysicalDeviceSurfaceCapabilities2EXT)    \
    X(GetPhysicalDevicePresentRectanglesKHR)       \
    X(GetPhysicalDeviceSurfacePresentModes2EXT)

#define DVL_DEVICE_INTERCEPTS(X)                    \
    X(DestroyDevice)                                \
    X(DeviceWaitIdle)                               \
    X(GetDeviceGroupSurfacePresentModesKHR)         \
    X(GetDeviceGroupSurfacePresentModes2EXT)        \
    X(CreateSwapchainKHR)                           \
    X(DestroySwapchainKHR)                          \
    X(GetSwapchainImagesKHR)                        \
    X(GetSwapchainStatusKHR)                        \
    X(AcquireNextImageKHR)                          \
    X(AcquireNextImage2KHR)                         \
    X(QueuePresentKHR)                              \
    X(WaitForPresentKHR)                            \
    X(WaitForPresent2KHR)                           \
    X(RegisterDisplayEventEXT)                      \
    X(RegisterDeviceEventEXT)                       \
    X(DisplayPowerControlEXT)                       \
    X(GetSwapchainCounterEXT)                       \
    X(WaitForFences)                                \
    X(GetFenceStatus)                               \
    X(ResetFences)                                  \
    X(DestroyFence)                                 \
    X(SetHdrMetadataEXT)                            \
    X(CreateSharedSwapchainsKHR)                    \
    X(AcquireFullScreenExclusiveModeEXT)            \
    X(ReleaseFullScreenExclusiveModeEXT)            \
    X(ReleaseSwapchainImagesEXT)                    \
    X(ReleaseSwapchainImagesKHR)                    \
    X(GetRefreshCycleDurationGOOGLE)                \
    X(GetPastPresentationTimingGOOGLE)              \
    X(SetSwapchainPresentTimingQueueSizeEXT)        \
    X(GetSwapchainTimingPropertiesEXT)              \
    X(GetSwapchainTimeDomainPropertiesEXT)          \
    X(GetPastPresentationTimingEXT)                 \
    X(GetCalibratedTimestampsKHR)                   \
    X(GetCalibratedTimestampsEXT)                   \
    X(SetDebugUtilsObjectNameEXT)                   \
    X(SetDebugUtilsObjectTagEXT)                    \
    X(DebugMarkerSetObjectNameEXT)                  \
    X(DebugMarkerSetObjectTagEXT)                   \
    X(SetPrivateData)                               \
    X(GetPrivateData)                               \
    X(SetPrivateDataEXT)                            \
    X(GetPrivateDataEXT)                            \
    X(SetLatencySleepModeNV)                        \
    X(LatencySleepNV)                               \
    X(SetLatencyMarkerNV)                           \
    X(GetLatencyTimingsNV)                          \
    X(SetLocalDimmingAMD)                           \
    X(CreateImage)                                  \
    X(BindImageMemory2)                             \
    X(BindImageMemory2KHR)                          \
    X(GetDeviceImageMemoryRequirements)             \
    X(GetDeviceImageMemoryRequirementsKHR)          \
    X(GetDeviceImageSparseMemoryRequirements)       \
    X(GetDeviceImageSparseMemoryRequirementsKHR)    \
    X(GetDeviceImageSubresourceLayout)              \
    X(GetDeviceImageSubresourceLayoutKHR)

#define DVL_DECLARE_INTERCEPT(name) extern std::remove_pointer_t<PFN_vk##name> layer_##name;
DVL_INSTANCE_INTERCEPTS(DVL_DECLARE_INTERCEPT)
DVL_DEVICE_INTERCEPTS(DVL_DECLARE_INTERCEPT)
#undef DVL_DECLARE_INTERCEPT

nvdm::DisplaySession& display_session();

void set_display_session_for_tests(nvdm::DisplaySession* session);

bool display_listed(InstanceData& instance, VkPhysicalDevice physical_device);
bool display_listed_now(InstanceData& instance, VkPhysicalDevice physical_device);
VkImageUsageFlags surface_usage(InstanceData& instance, VkPhysicalDevice physical_device);
std::optional<VkPhysicalDevice> listing_physical_device(InstanceData& instance);
uint32_t driver_plane_count(InstanceData& instance, VkPhysicalDevice physical_device);

template <class Body>
VkResult guarded(const char* command, Body&& body) noexcept
{
    try {
        return body();
    } catch (const std::bad_alloc&) {
        log(Level::Error, "%s: out of host memory", command);
        return VK_ERROR_OUT_OF_HOST_MEMORY;
    } catch (const std::exception& e) {
        log(Level::Error, "%s: %s", command, e.what());
        return VK_ERROR_UNKNOWN;
    } catch (...) {
        log(Level::Error, "%s: unexpected error", command);
        return VK_ERROR_UNKNOWN;
    }
}

template <class Body>
void guarded_void(const char* command, Body&& body) noexcept
{
    guarded(command, [&] {
        body();
        return VK_SUCCESS;
    });
}

VkResult missing_below(const char* command) noexcept;

template <class Fn, class... Args>
VkResult call_down(Fn fn, const char* command, Args... args)
{
    if (fn == nullptr) {
        return missing_below(command);
    }
    return fn(args...);
}

}
