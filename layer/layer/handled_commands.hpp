// Copyright 2026, Finn Zeruhn
// SPDX-License-Identifier: BSL-1.0

#pragma once

#include <cstdint>
#include <functional>
#include <string>
#include <string_view>
#include <vector>

namespace dvl {

struct HandleCommand {
    std::string_view name;
    std::string_view extension;
};

const std::vector<HandleCommand>& handle_commands();

bool handled_command(std::string_view name);

const char* not_applicable(std::string_view name);

struct NotApplicableCommand {
    std::string_view name;
    const char* reason;
};
const std::vector<NotApplicableCommand>& not_applicable_commands();

const std::vector<std::string_view>& intercepted_commands();

const std::vector<std::string_view>& known_extensions();
std::string_view registry_version();

std::vector<std::string> coverage_errors(const std::vector<HandleCommand>& list, const char* const* extensions,
                                         uint32_t count, const std::function<bool(std::string_view)>& classified,
                                         std::string_view where);

std::vector<std::string> unknown_extension_warnings(const std::vector<std::string_view>& known,
                                                    std::string_view version, const char* const* extensions,
                                                    uint32_t count);

void check_command_coverage(const char* const* extensions, uint32_t count, const char* where) noexcept;

}
