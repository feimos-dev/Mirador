// Copyright 2026, Finn Zeruhn
// SPDX-License-Identifier: BSL-1.0

#pragma once

#include <string>
#include <vector>

namespace dvl {

inline constexpr int kExitPass = 0;
inline constexpr int kExitFail = 1;
inline constexpr int kExitUsage = 2;
inline constexpr int kExitNotUsed = 3;

inline constexpr int kDefaultSeconds = 20;
inline constexpr int kMaxSeconds = 3600;

struct Options {
    bool no_heartbeat = false;
    bool ignore_holders = false;
    bool enable_direct_mode = false;
    bool help = false;
    int seconds = kDefaultSeconds;
};

struct ParsedOptions {
    Options options;
    std::string error;
    bool ok() const { return error.empty(); }
};

ParsedOptions parse_options(const std::vector<std::string>& args);

std::string usage_text();

void apply_environment(const Options& options);

}
