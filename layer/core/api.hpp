// Copyright 2026, Finn Zeruhn
// SPDX-License-Identifier: BSL-1.0

#pragma once

#include <windows.h>
#include <unknwn.h>

#include <cstdint>
#include <memory>
#include <string>
#include <vector>

#include "nvapi_ids.hpp"

namespace nvdm {

inline constexpr int kNoNvapi = -10001;
inline constexpr int kNoEntry = -10002;
inline constexpr int kBadOutput = -10003;
inline constexpr int kNotBound = -10004;
inline constexpr int kBadArgument = -10005;

enum class EdidRoute {
    None,
    Nvapi,
    NvapiOutput,
    Registry,
    Board,
};

inline const char* edid_route_name(EdidRoute route)
{
    switch (route) {
    case EdidRoute::Nvapi: return "NVAPI";
    case EdidRoute::NvapiOutput: return "NVAPI-output";
    case EdidRoute::Registry: return "registry";
    case EdidRoute::Board: return "board";
    case EdidRoute::None: break;
    }
    return "none";
}

struct BackBuffer {
    uint64_t tag = 0;
    HANDLE shared = nullptr;
};

struct RefreshStats {
    uint32_t refresh_num = 0;
    uint32_t refresh_den = 0;
    uint32_t counter = 0;
    int64_t qpc_ns = 0;
};

class Api {
public:
    virtual ~Api() = default;

    virtual int initialize() = 0;

    virtual const char* first_missing_id() = 0;

    virtual std::string driver_version() = 0;

    virtual int enumerate(uint32_t vendor, std::vector<uint32_t>& ids) = 0;

    virtual int adapter_luid(uint32_t display_id, LUID& out) = 0;

    virtual EdidRoute read_edid(uint32_t display_id, std::vector<uint8_t>& out) = 0;

    virtual int enable_direct_mode(uint32_t vendor) = 0;

    virtual int bind_device(uint32_t display_id, IUnknown* d3d11, uint32_t vendor) = 0;

    virtual int create_back_buffer(const Mode& mode, BackBuffer& out) = 0;

    virtual int set_display_mode(const Mode& mode) = 0;

    virtual int present(uint64_t tag) = 0;

    virtual int present_stats(RefreshStats& out) = 0;
};

std::unique_ptr<Api> make_nvapi();

bool read_edid_from_registry(std::vector<uint8_t>& out);

bool read_edid_from_display_key(HKEY display, std::vector<uint8_t>& out);

}
