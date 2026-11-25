// Copyright 2026, Finn Zeruhn
// SPDX-License-Identifier: BSL-1.0

#pragma once

#include <optional>
#include <string>
#include <string_view>

namespace dvl {

std::string to_utf8(std::wstring_view text);
std::wstring from_utf8(std::string_view text);

std::optional<std::string> env_get(const wchar_t* name);

}
