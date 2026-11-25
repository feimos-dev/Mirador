// Copyright 2026, Finn Zeruhn
// SPDX-License-Identifier: BSL-1.0

#pragma once

#include <vulkan/vulkan.h>

#include <cstdint>
#include <optional>
#include <string>
#include <vector>

namespace dvl {

struct ImportConfig {
    VkFormat format;
    VkImageUsageFlags usage;
};

inline constexpr ImportConfig kComputeConfig{VK_FORMAT_R8G8B8A8_UNORM,
                                             VK_IMAGE_USAGE_STORAGE_BIT | VK_IMAGE_USAGE_SAMPLED_BIT |
                                                 VK_IMAGE_USAGE_TRANSFER_SRC_BIT | VK_IMAGE_USAGE_TRANSFER_DST_BIT};
inline constexpr ImportConfig kGraphicsConfig{VK_FORMAT_R8G8B8A8_SRGB,
                                              VK_IMAGE_USAGE_COLOR_ATTACHMENT_BIT | VK_IMAGE_USAGE_SAMPLED_BIT |
                                                  VK_IMAGE_USAGE_TRANSFER_SRC_BIT | VK_IMAGE_USAGE_TRANSFER_DST_BIT};
inline constexpr ImportConfig kTrialConfigs[] = {kComputeConfig, kGraphicsConfig};

inline constexpr VkExternalMemoryHandleTypeFlagBits kBackBufferHandleType =
    VK_EXTERNAL_MEMORY_HANDLE_TYPE_D3D11_TEXTURE_KMT_BIT;

std::optional<std::vector<VkFormat>> view_formats(VkFormat format, VkImageUsageFlags usage);

std::string format_name(VkFormat format);

struct ImportSupport {
    bool ok = false;
    bool dedicated_only = false;
    std::string reason;
};
ImportSupport import_support(VkResult result, const VkImageFormatProperties& properties,
                             const VkExternalMemoryProperties& external, VkExtent2D extent);

struct MemoryPick {
    std::optional<uint32_t> index;
    bool fallback = false;
};
MemoryPick pick_memory_type(std::optional<uint32_t> handle_bits, uint32_t image_bits,
                            const VkPhysicalDeviceMemoryProperties& memory);

}
