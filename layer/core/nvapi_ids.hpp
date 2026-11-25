// Copyright 2026, Finn Zeruhn
// SPDX-License-Identifier: BSL-1.0

// Our own declarations for the NVAPI calls the Dream Air's direct mode needs: the ids that go to
// nvapi_QueryInterface, the vendor and status constants, and the three structs whose layout the driver sees.
// Nothing here comes from an NVIDIA header. The layouts and values were worked out from runtime captures of the
// Pimax and SteamVR compositors (docs\captures\windows-directmode-2026-10-29).

#pragma once

#include <cstdint>

namespace nvdm {

enum Id : uint32_t {
    Initialize = 0x0150e828,
    GetPhysicalGpuFromDisplayId = 0x9ea74659,
    GetAdapterIdFromPhysicalGpu = 0x0ff07fde,
    Enumerate = 0x602f60d8,
    EnableDirectMode = 0x1ac2b1e7,
    BindDevice = 0xc4e4efda,
    CreateBackBuffer = 0x86bdb518,
    SetDisplayMode = 0x56df9d51,
    Present = 0x8c84a92a,
    GetPresentStats = 0xace116bc,
    GetDriverAndBranchVersion = 0x2926aaad,
    GetGpuAndOutputIdFromDisplayId = 0x112ba1a5,
    GetEDID = 0x37d32e69,
};

struct IdInfo {
    Id id;
    const char* name;
    bool is_private;
};

inline constexpr IdInfo kAllIds[] = {
    {Initialize, "Initialize", false},
    {GetPhysicalGpuFromDisplayId, "GetPhysicalGpuFromDisplayId", false},
    {GetAdapterIdFromPhysicalGpu, "GetAdapterIdFromPhysicalGpu", false},
    {Enumerate, "Enumerate", true},
    {EnableDirectMode, "EnableDirectMode", true},
    {BindDevice, "BindDevice", true},
    {CreateBackBuffer, "CreateBackBuffer", true},
    {SetDisplayMode, "SetDisplayMode", true},
    {Present, "Present", true},
    {GetPresentStats, "GetPresentStats", true},
    {GetDriverAndBranchVersion, "GetDriverAndBranchVersion", false},
    {GetGpuAndOutputIdFromDisplayId, "GetGpuAndOutputIdFromDisplayId", false},
    {GetEDID, "GetEDID", false},
};

inline constexpr uint32_t kVendorPvr = 0xd242;

inline constexpr int kSetModeOk = -208;

inline constexpr uint32_t kBackBufferCount = 3;

struct Mode {
    uint32_t version = 0x00020030;
    uint32_t width = 7104;
    uint32_t height = 3840;
    uint32_t refresh_num = 90000;
    uint32_t refresh_den = 1000;
    uint32_t bpp = 32;
    uint32_t flags = 1;
    uint32_t pad[5] = {};
};
static_assert(sizeof(Mode) == 48, "the driver takes a 48-byte mode");

struct RawStats {
    uint32_t version = 0x00020018;
    uint32_t refresh_num = 0;
    uint32_t refresh_den = 0;
    uint32_t frame_counter = 0;
    uint64_t qpc = 0;

    // The driver writes past the 24 bytes the version gives (SteamVR's capture; a 24-byte struct crashed Monado
    // under /GS), so leave room.
    uint8_t spare[40] = {};
};
static_assert(sizeof(RawStats) >= 64, "keep room for what the driver writes past the version-2 fields");

struct Handle {
    uint32_t display_id = 0;
    uint32_t pad = 0;
    void* nvapi = nullptr;

    // Room in case a driver writes more than the 16 bytes seen so far.
    uint8_t spare[48] = {};
};
static_assert(sizeof(Handle) == 64, "16 bytes the driver uses, plus 48 spare");

}
