// Copyright 2026, Finn Zeruhn
// SPDX-License-Identifier: BSL-1.0

#pragma once

#include <vulkan/vulkan.h>

#include <cstdint>
#include <optional>

#include "core/product_table.hpp"

namespace dvl {

VkDisplayKHR our_display();
VkDisplayModeKHR our_mode();
bool is_our_display(VkDisplayKHR display);
bool is_our_mode(VkDisplayModeKHR mode);

struct PlaneMap {
    uint32_t driver_planes = 0;

    std::optional<uint32_t> to_driver(uint32_t layer_index) const;
    uint32_t count() const;
};

void fill_display_properties(const nvdm::Product& product, VkDisplayPropertiesKHR& out);

VkDisplayModeParametersKHR mode_parameters(const nvdm::Product& product);
bool is_product_mode(const nvdm::Product& product, const VkDisplayModeParametersKHR& parameters);

VkDisplayPlanePropertiesKHR our_plane_properties();

VkDisplayPlaneCapabilitiesKHR plane_capabilities(const nvdm::Product& product);

}
