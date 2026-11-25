// Copyright 2026, Finn Zeruhn
// SPDX-License-Identifier: BSL-1.0

#pragma once

#include <vulkan/vulkan.h>

#include <algorithm>
#include <cstdint>

namespace dvl {

template <class T, class Fill>
VkResult fill_list(uint32_t* count, T* out, uint32_t total, Fill&& fill)
{
    if (out == nullptr) {
        *count = total;
        return VK_SUCCESS;
    }
    const uint32_t n = std::min(*count, total);
    for (uint32_t i = 0; i < n; i++) {
        fill(i, out[i]);
    }
    *count = n;
    return n < total ? VK_INCOMPLETE : VK_SUCCESS;
}

template <class T, class Fill, class Down>
VkResult list_ours_first(uint32_t* count, T* out, uint32_t ours, Fill&& fill, Down&& down)
{
    uint32_t below = 0;
    const VkResult counted = down(&below, static_cast<T*>(nullptr));
    if (counted < 0) {
        return counted;
    }
    const uint32_t total = ours + below;
    if (out == nullptr) {
        *count = total;
        return VK_SUCCESS;
    }
    const uint32_t room = std::min(*count, total);
    const uint32_t own = std::min(room, ours);
    for (uint32_t i = 0; i < own; i++) {
        fill(i, out[i]);
    }
    uint32_t written = own;
    VkResult result = room < total ? VK_INCOMPLETE : VK_SUCCESS;
    if (room > ours) {
        uint32_t theirs = room - ours;
        const VkResult r = down(&theirs, out + ours);
        if (r < 0) {
            return r;
        }
        written += theirs;
        if (r == VK_INCOMPLETE) {
            result = VK_INCOMPLETE;
        }
    }
    *count = written;
    return result;
}

}
