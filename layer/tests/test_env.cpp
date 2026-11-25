// Copyright 2026, Finn Zeruhn
// SPDX-License-Identifier: BSL-1.0

#include <catch2/catch_test_macros.hpp>

#include <windows.h>

#include <string>

#include "layer/env.hpp"

namespace {

struct Variable {
    const wchar_t* name;
    explicit Variable(const wchar_t* n, const wchar_t* value) : name(n) { SetEnvironmentVariableW(name, value); }
    ~Variable() { SetEnvironmentVariableW(name, nullptr); }
};

}

TEST_CASE("env: a layer variable under its new name is read")
{
    Variable v(L"MIRADOR_DIRECT_MODE_LOG", L"debug");
    CHECK(dvl::env_get(L"MIRADOR_DIRECT_MODE_LOG").value_or("") == "debug");
}
