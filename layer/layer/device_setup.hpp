// Copyright 2026, Finn Zeruhn
// SPDX-License-Identifier: BSL-1.0

#pragma once

#include <vulkan/vulkan.h>

#include <cstdint>
#include <optional>
#include <string>
#include <vector>

namespace dvl {

inline constexpr uint32_t kNvidiaVendorId = 0x10de;

inline constexpr const char* kImportExtensions[] = {
    VK_KHR_EXTERNAL_MEMORY_EXTENSION_NAME,
    VK_KHR_EXTERNAL_MEMORY_WIN32_EXTENSION_NAME,
    VK_KHR_DEDICATED_ALLOCATION_EXTENSION_NAME,
    VK_KHR_GET_MEMORY_REQUIREMENTS_2_EXTENSION_NAME,
    VK_KHR_IMAGE_FORMAT_LIST_EXTENSION_NAME,
};

struct DevicePlan {
    DevicePlan() = default;
    DevicePlan(DevicePlan&&) = default;
    DevicePlan& operator=(DevicePlan&&) = default;
    DevicePlan(const DevicePlan&) = delete;
    DevicePlan& operator=(const DevicePlan&) = delete;

    std::vector<VkDeviceQueueCreateInfo> queues;
    std::vector<std::vector<float>> priorities;
    std::vector<const char*> extensions;
    uint32_t private_family = VK_QUEUE_FAMILY_IGNORED;
    uint32_t private_index = 0;
    bool new_info = false;

    std::vector<const char*> missing_extensions;
    bool display_control = false;
};

std::optional<DevicePlan> plan_device(const VkDeviceCreateInfo& ci, const std::vector<VkQueueFamilyProperties>& families,
                                      const std::vector<std::string>& available_ext, uint32_t vendor_id);

}
