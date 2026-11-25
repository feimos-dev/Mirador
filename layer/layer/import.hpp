// Copyright 2026, Finn Zeruhn
// SPDX-License-Identifier: BSL-1.0

#pragma once

#include <windows.h>

#include <vulkan/vulkan.h>

#include "layer/dispatch.hpp"
#include "layer/import_rules.hpp"

namespace dvl {

struct ImportedImage {
    VkImage image = VK_NULL_HANDLE;
    VkDeviceMemory memory = VK_NULL_HANDLE;
};

VkResult import_back_buffer(DeviceData& device, HANDLE shared, VkExtent2D extent, const ImportConfig& config,
                            ImportedImage& out);

void release_imported(DeviceData& device, ImportedImage& image);

bool trial_import(DeviceData& device);

bool trial_import_at_swapchain(DeviceData& device);

VkImageUsageFlags supported_usage(InstanceData& instance, VkPhysicalDevice physical_device, VkExtent2D extent);
VkImageUsageFlags supported_usage(DeviceData& device);

struct ImportVerdict {
    bool listed = false;
    VkImageUsageFlags usage = 0;
};
ImportVerdict import_verdict(InstanceData& instance, VkPhysicalDevice physical_device);

}
