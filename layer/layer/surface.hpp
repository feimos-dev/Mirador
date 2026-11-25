// Copyright 2026, Finn Zeruhn
// SPDX-License-Identifier: BSL-1.0

#pragma once

#include <vulkan/vulkan.h>
#include <vulkan/vk_icd.h>

#include <cstddef>
#include <memory>
#include <optional>
#include <string>
#include <type_traits>
#include <vector>

namespace dvl {

struct SurfaceSpec {
    VkExtent2D extent;
    VkImageUsageFlags usage;
};

inline constexpr VkImageUsageFlags kSurfaceUsage = VK_IMAGE_USAGE_STORAGE_BIT | VK_IMAGE_USAGE_COLOR_ATTACHMENT_BIT |
                                                   VK_IMAGE_USAGE_SAMPLED_BIT | VK_IMAGE_USAGE_TRANSFER_SRC_BIT |
                                                   VK_IMAGE_USAGE_TRANSFER_DST_BIT;

inline constexpr uint32_t kSurfaceImageCount = 3;

VkSurfaceCapabilitiesKHR surface_capabilities(const SurfaceSpec& spec);

void fill_surface_capabilities_2ext(const SurfaceSpec& spec, VkSurfaceCapabilities2EXT& out);

void fill_surface_capabilities_chain(void* next, const SurfaceSpec& spec, std::optional<VkPresentModeKHR> present_mode);

std::optional<VkPresentModeKHR> queried_present_mode(const void* next);

void fill_surface_format_chain(void* next);

std::vector<VkSurfaceFormatKHR> surface_formats();

std::vector<VkPresentModeKHR> present_modes();

struct SwapchainCheck {
    bool refused = false;
    std::string reason;
    std::vector<std::string> notes;
};
SwapchainCheck check_swapchain_parameters(const VkSwapchainCreateInfoKHR& ci, const SurfaceSpec& spec);

struct SurfaceRecord {
    VkIcdSurfaceDisplay icd{};
    VkInstance instance = VK_NULL_HANDLE;
    VkDisplaySurfaceCreateInfoKHR info{};
    SurfaceSpec spec{};
};
static_assert(std::is_standard_layout_v<SurfaceRecord>, "the header's place is defined for a standard-layout type");
static_assert(offsetof(SurfaceRecord, icd) == 0, "the handle points at the VkIcdSurfaceDisplay header");
static_assert(offsetof(VkIcdSurfaceDisplay, base) == 0 && offsetof(VkIcdSurfaceDisplay, displayMode) == 8 &&
                  offsetof(VkIcdSurfaceDisplay, planeIndex) == 16 &&
                  offsetof(VkIcdSurfaceDisplay, planeStackIndex) == 20 &&
                  offsetof(VkIcdSurfaceDisplay, transform) == 24 && offsetof(VkIcdSurfaceDisplay, globalAlpha) == 28 &&
                  offsetof(VkIcdSurfaceDisplay, alphaMode) == 32 && offsetof(VkIcdSurfaceDisplay, imageExtent) == 36 &&
                  sizeof(VkIcdSurfaceDisplay) == 48,
              "VkIcdSurfaceDisplay as the loader and drivers lay it out on x64");

VkIcdSurfaceDisplay icd_surface_display(const VkDisplaySurfaceCreateInfoKHR& ci);

VkSurfaceKHR add_surface(std::unique_ptr<SurfaceRecord> record);
const SurfaceRecord* find_surface(VkSurfaceKHR surface);
std::unique_ptr<SurfaceRecord> remove_surface(VkSurfaceKHR surface);

}
