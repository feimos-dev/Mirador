// Copyright 2026, Finn Zeruhn
// SPDX-License-Identifier: BSL-1.0

#pragma once

#include <string>
#include <string_view>

namespace dvl {

bool process_allowed(std::wstring_view exe_basename, std::string_view extra_list);

std::wstring basename_of(std::wstring_view path);

std::wstring current_process_basename();

bool current_process_allowed();

}
