// Copyright 2026, Finn Zeruhn
// SPDX-License-Identifier: BSL-1.0

#pragma once

#include <cstdint>
#include <optional>
#include <string>
#include <vector>

namespace nvdm {

struct Product {
    uint16_t pnp_vendor;
    uint16_t pnp_product;
    const char* name;
    uint32_t width, height;
    uint32_t refresh_mhz;
    bool tested = true;
};

struct DirectModeVendor {
    uint32_t nvapi_id;
    uint16_t pnp_vendor;
    const char* letters;
    const char* name;
};

inline constexpr DirectModeVendor kDirectModeVendors[] = {
    {0xd242, 0x42d2, "PVR", "Pimax"},
};

const DirectModeVendor* vendor_for_pnp(uint16_t pnp_vendor);

extern const Product kDreamAir;

std::optional<Product> find_product(const std::vector<uint8_t>& edid);

struct EdidIds {
    uint16_t pnp_vendor;
    uint16_t pnp_product;
};

std::optional<EdidIds> parse_edid_ids(const std::vector<uint8_t>& edid);

std::string pnp_letters(uint16_t pnp_vendor);

}
