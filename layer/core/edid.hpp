// Copyright 2026, Finn Zeruhn
// SPDX-License-Identifier: BSL-1.0

#pragma once

#include <cstdint>
#include <optional>
#include <vector>

namespace nvdm {

struct EdidMode {
    uint32_t width = 0;
    uint32_t height = 0;
    uint32_t refresh_mhz = 0;
};

std::optional<EdidMode> preferred_mode(const std::vector<uint8_t>& edid);

bool has_microsoft_hmd_block(const std::vector<uint8_t>& edid);

inline constexpr const char kWindowsDrivesIt[] = "carries Microsoft's HMD block, so Windows drives it";

}
