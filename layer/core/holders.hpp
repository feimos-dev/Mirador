// Copyright 2026, Finn Zeruhn
// SPDX-License-Identifier: BSL-1.0

#pragma once

#include <string>
#include <vector>

namespace nvdm {

enum class Held {
    Display,
    Board,
    Receiver,
    Monado,
};

std::vector<std::string> list_processes();

std::vector<std::string> running_holders(Held held, const std::vector<std::string>& processes);

}
