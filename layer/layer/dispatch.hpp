// Copyright 2026, Finn Zeruhn
// SPDX-License-Identifier: BSL-1.0

#pragma once

#ifndef VK_NO_PROTOTYPES
#error "The layer calls Vulkan only through function pointers: define VK_NO_PROTOTYPES (CMake does for dvl_pure)."
#endif

#include <vulkan/vulkan.h>

#include <array>
#include <atomic>
#include <cstdint>
#include <functional>
#include <memory>
#include <mutex>
#include <optional>
#include <unordered_map>
#include <vector>

#include "core/fence_pool.hpp"

namespace dvl {

#define DVL_INSTANCE_FUNCTIONS(X)                    \
    X(DestroyInstance)                               \
    X(EnumeratePhysicalDevices)                      \
    X(EnumerateDeviceExtensionProperties)            \
    X(GetPhysicalDeviceProperties)                   \
    X(GetPhysicalDeviceMemoryProperties)             \
    X(GetPhysicalDeviceQueueFamilyProperties)        \
    X(GetPhysicalDeviceProperties2KHR)               \
    X(GetPhysicalDeviceImageFormatProperties2KHR)    \
    X(GetPhysicalDeviceDisplayPropertiesKHR)         \
    X(GetPhysicalDeviceDisplayPlanePropertiesKHR)    \
    X(GetDisplayPlaneSupportedDisplaysKHR)           \
    X(GetDisplayModePropertiesKHR)                   \
    X(CreateDisplayModeKHR)                          \
    X(GetDisplayPlaneCapabilitiesKHR)                \
    X(CreateDisplayPlaneSurfaceKHR)                  \
    X(GetPhysicalDeviceDisplayProperties2KHR)        \
    X(GetPhysicalDeviceDisplayPlaneProperties2KHR)   \
    X(GetDisplayModeProperties2KHR)                  \
    X(GetDisplayPlaneCapabilities2KHR)                \
    X(DestroySurfaceKHR)                             \
    X(GetPhysicalDeviceSurfaceSupportKHR)            \
    X(GetPhysicalDeviceSurfaceCapabilitiesKHR)       \
    X(GetPhysicalDeviceSurfaceFormatsKHR)            \
    X(GetPhysicalDeviceSurfacePresentModesKHR)       \
    X(GetPhysicalDeviceSurfaceCapabilities2KHR)      \
    X(GetPhysicalDeviceSurfaceFormats2KHR)           \
    X(GetPhysicalDeviceSurfaceCapabilities2EXT)      \
    X(GetPhysicalDevicePresentRectanglesKHR)         \
    X(GetPhysicalDeviceSurfacePresentModes2EXT)      \
    X(ReleaseDisplayEXT)                             \
    X(AcquireWinrtDisplayNV)                         \
    X(GetWinrtDisplayNV)

#define DVL_DEVICE_FUNCTIONS(X)                     \
    X(DestroyDevice)                                \
    X(GetDeviceQueue)                               \
    X(GetDeviceGroupSurfacePresentModesKHR)         \
    X(GetDeviceGroupSurfacePresentModes2EXT)        \
    X(QueueSubmit)                                  \
    X(DeviceWaitIdle)                               \
    X(CreateImage)                                  \
    X(DestroyImage)                                 \
    X(GetImageMemoryRequirements)                   \
    X(AllocateMemory)                               \
    X(FreeMemory)                                   \
    X(BindImageMemory)                              \
    X(GetMemoryWin32HandlePropertiesKHR)            \
    X(CreateFence)                                  \
    X(DestroyFence)                                 \
    X(WaitForFences)                                \
    X(ResetFences)                                  \
    X(GetFenceStatus)                               \
    X(CreateSwapchainKHR)                           \
    X(DestroySwapchainKHR)                          \
    X(GetSwapchainImagesKHR)                        \
    X(AcquireNextImageKHR)                          \
    X(AcquireNextImage2KHR)                         \
    X(QueuePresentKHR)                              \
    X(WaitForPresentKHR)                            \
    X(WaitForPresent2KHR)                           \
    X(GetSwapchainStatusKHR)                        \
    X(QueueWaitIdle)                                \
    X(GetSwapchainCounterEXT)                       \
    X(SetDebugUtilsObjectNameEXT)                   \
    X(SetDebugUtilsObjectTagEXT)                    \
    X(DebugMarkerSetObjectNameEXT)                  \
    X(DebugMarkerSetObjectTagEXT)                   \
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
    X(SetPrivateData)                               \
    X(GetPrivateData)                               \
    X(SetPrivateDataEXT)                            \
    X(GetPrivateDataEXT)                            \
    X(SetLatencySleepModeNV)                        \
    X(LatencySleepNV)                               \
    X(SetLatencyMarkerNV)                           \
    X(GetLatencyTimingsNV)                          \
    X(SignalSemaphoreKHR)                           \
    X(SetLocalDimmingAMD)                           \
    X(BindImageMemory2)                             \
    X(BindImageMemory2KHR)                          \
    X(GetDeviceImageMemoryRequirements)             \
    X(GetDeviceImageMemoryRequirementsKHR)          \
    X(GetDeviceImageSparseMemoryRequirements)       \
    X(GetDeviceImageSparseMemoryRequirementsKHR)    \
    X(GetDeviceImageSubresourceLayout)              \
    X(GetDeviceImageSubresourceLayoutKHR)

inline void* dispatch_key(void* dispatchable)
{
    return *static_cast<void**>(dispatchable);
}

class InstanceData {
public:
    InstanceData() = default;
    InstanceData(const InstanceData&) = delete;
    InstanceData& operator=(const InstanceData&) = delete;

    VkInstance handle = VK_NULL_HANDLE;
    PFN_vkGetInstanceProcAddr gipa = nullptr;

#define X(name) PFN_vk##name name = nullptr;
    DVL_INSTANCE_FUNCTIONS(X)
#undef X

    std::vector<VkPhysicalDevice> physical_devices();

    std::mutex display_mutex;
    std::unordered_map<VkPhysicalDevice, bool> display_listed_on;
    std::unordered_map<VkPhysicalDevice, VkImageUsageFlags> surface_usage_on;

private:
    std::mutex physical_mutex_;
    std::vector<VkPhysicalDevice> physical_;
    bool physical_listed_ = false;
};

class DeviceData {
public:
    DeviceData() = default;
    DeviceData(const DeviceData&) = delete;
    DeviceData& operator=(const DeviceData&) = delete;

    VkDevice handle = VK_NULL_HANDLE;
    VkPhysicalDevice physical_device = VK_NULL_HANDLE;
    InstanceData* instance = nullptr;
    PFN_vkGetDeviceProcAddr gdpa = nullptr;

#define X(name) PFN_vk##name name = nullptr;
    DVL_DEVICE_FUNCTIONS(X)
#undef X

    VkQueue private_queue = VK_NULL_HANDLE;
    uint32_t private_family = 0;
    uint32_t private_index = 0;
    std::mutex queue_mutex;

    bool display_control = false;

    std::atomic<bool> latency_signal_missing_logged{false};

    std::vector<const char*> missing_import_extensions;
    std::mutex import_mutex;
    std::optional<bool> trial_import_result;
    std::atomic<bool> memory_fallback_logged{false};

    std::array<VkFence, nvdm::kFencePoolCap> fences{};
    nvdm::FencePool fence_pool{nvdm::kFencePoolCap, [this](nvdm::FenceSlot slot) { return fence_signalled(slot); }};
    bool fence_signalled(nvdm::FenceSlot slot) const
    {
        return slot < fences.size() && fences[slot] != VK_NULL_HANDLE && GetFenceStatus != nullptr &&
               GetFenceStatus(handle, fences[slot]) == VK_SUCCESS;
    }
};

std::unique_ptr<InstanceData> make_instance_data(VkInstance instance, PFN_vkGetInstanceProcAddr next_gipa);
std::unique_ptr<DeviceData> make_device_data(VkDevice device, VkPhysicalDevice physical_device, InstanceData* instance,
                                             PFN_vkGetDeviceProcAddr next_gdpa);

InstanceData& add_instance(std::unique_ptr<InstanceData> data);
std::unique_ptr<InstanceData> remove_instance(void* dispatchable);
InstanceData* find_instance_data(void* dispatchable);
InstanceData& instance_data(void* dispatchable);

DeviceData& add_device(std::unique_ptr<DeviceData> data);
std::unique_ptr<DeviceData> remove_device(void* dispatchable);
DeviceData* find_device_data(void* dispatchable);
DeviceData& device_data(void* dispatchable);

size_t for_each_device(const InstanceData* instance, VkPhysicalDevice physical_device,
                       const std::function<void(DeviceData&)>& fn);

PFN_vkDestroyInstance fallback_destroy_instance();
PFN_vkDestroyDevice fallback_destroy_device();

}
