// Copyright 2026, Finn Zeruhn
// SPDX-License-Identifier: BSL-1.0

#pragma once

#include <cstdint>
#include <string_view>

namespace nvdm {

inline constexpr uint16_t kBoardVendorId = 0x34A4;
inline constexpr uint16_t kBoardProductId = 0x0044;

bool is_board_instance_id(std::wstring_view instance_id);

bool board_present();

}
