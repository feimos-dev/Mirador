// Copyright 2026, Finn Zeruhn
// SPDX-License-Identifier: BSL-1.0

// The real Api: NVAPI through nvapi64.dll's nvapi_QueryInterface. Ported from the nvdm_* functions of Monado's
// comp_window_nvapi_direct.c (nvdm_load to nvdm_poll_stats) and the standalone nvdm_probe. The target's
// nvdm_create_d3d11 is not here: the display session makes the D3D11 device.

#include "api.hpp"

#include <wchar.h>

#include <algorithm>
#include <cstdio>
#include <iterator>
#include <string>
#include <utility>

#include "clock.hpp"

namespace nvdm {
namespace {

using QueryInterfaceFn = void*(__cdecl*)(uint32_t id);
using InitializeFn = int(__cdecl*)();

// The private calls take at most six integer or pointer arguments; passing extra zeroes is harmless on x64.
using CallFn = int(__cdecl*)(uintptr_t, uintptr_t, uintptr_t, uintptr_t, uintptr_t, uintptr_t);

template <class T>
uintptr_t arg(T* pointer)
{
    return reinterpret_cast<uintptr_t>(pointer);
}

constexpr size_t kEdidBlock = 128;

bool looks_like_edid(const std::vector<uint8_t>& edid)
{
    static const uint8_t kHeader[8] = {0x00, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0x00};
    return edid.size() >= kEdidBlock && std::equal(std::begin(kHeader), std::end(kHeader), edid.begin());
}

constexpr uint32_t kEdidPage = 256;
struct EdidPage {
    uint32_t version;
    uint8_t data[kEdidPage];
    uint32_t size;
    uint32_t id;
    uint32_t offset;
};
static_assert(sizeof(EdidPage) == 272, "NV_EDID version 3 is 272 bytes");
constexpr uint32_t kEdidPageVersion = sizeof(EdidPage) | (3u << 16);

// More than the layout needs, in case a driver writes past it.
struct EdidBuffer {
    EdidPage page;
    uint8_t spare[256];
};

constexpr uint32_t kEdidMaxPages = 16;

struct RegKey {
    HKEY key = nullptr;
    RegKey() = default;
    RegKey(const RegKey&) = delete;
    RegKey& operator=(const RegKey&) = delete;
    ~RegKey()
    {
        if (key != nullptr) {
            RegCloseKey(key);
        }
    }
};

std::vector<uint8_t> read_binary_value(HKEY parent, const wchar_t* subkey, const wchar_t* name)
{
    constexpr DWORD kMaxBytes = 1u << 16;
    DWORD size = 0;
    if (RegGetValueW(parent, subkey, name, RRF_RT_REG_BINARY, nullptr, nullptr, &size) != ERROR_SUCCESS || size == 0 ||
        size > kMaxBytes) {
        return {};
    }
    std::vector<uint8_t> data(size);
    DWORD got = size;
    if (RegGetValueW(parent, subkey, name, RRF_RT_REG_BINARY, nullptr, data.data(), &got) != ERROR_SUCCESS) {
        return {};
    }
    data.resize(got);
    return data;
}

class NvapiApi final : public Api {
public:
    NvapiApi()
    {
        LARGE_INTEGER frequency{};
        QueryPerformanceFrequency(&frequency);
        qpc_frequency_ = frequency.QuadPart;
    }

    int initialize() override
    {
        if (initialized_) {
            return 0;
        }
        if (dll_ == nullptr) {
            dll_ = LoadLibraryExW(L"nvapi64.dll", nullptr, LOAD_LIBRARY_SEARCH_SYSTEM32);
            if (dll_ == nullptr) {
                return kNoNvapi;
            }
        }
        if (query_ == nullptr) {
            query_ = reinterpret_cast<QueryInterfaceFn>(GetProcAddress(dll_, "nvapi_QueryInterface"));
            if (query_ == nullptr) {
                return kNoEntry;
            }
        }

        auto initialize_nvapi = reinterpret_cast<InitializeFn>(query_(Initialize));
        if (initialize_nvapi == nullptr) {
            return kNoEntry;
        }
        const int ret = initialize_nvapi();
        if (ret != 0) {
            return ret;
        }

        for (size_t i = 0; i < std::size(kAllIds); i++) {
            fns_[i] = query_(kAllIds[i].id);
        }
        initialized_ = true;
        return 0;
    }

    const char* first_missing_id() override
    {
        if (!initialized_) {
            return "Initialize";
        }
        for (size_t i = 0; i < std::size(kAllIds); i++) {
            if (kAllIds[i].is_private && fns_[i] == nullptr) {
                return kAllIds[i].name;
            }
        }
        return nullptr;
    }

    std::string driver_version() override
    {
        uint32_t version = 0;
        char branch[256] = {};
        if (call(GetDriverAndBranchVersion, arg(&version), arg(branch)) != 0 || version == 0) {
            return {};
        }
        char text[32];
        snprintf(text, sizeof(text), "%u.%02u", version / 100, version % 100);
        return text;
    }

    int enumerate(uint32_t vendor, std::vector<uint32_t>& ids) override
    {
        ids.clear();
        uint32_t count = 8;
        uint32_t buffer[32] = {};
        const int ret = call(Enumerate, vendor, arg(&count), arg(buffer), 1);
        if (ret != 0) {
            return ret;
        }
        count = std::min<uint32_t>(count, static_cast<uint32_t>(std::size(buffer)));
        for (uint32_t i = 0; i < count; i++) {
            if (buffer[i] != 0) {
                ids.push_back(buffer[i]);
            }
        }
        return 0;
    }

    int adapter_luid(uint32_t display_id, LUID& out) override
    {
        void* gpu = nullptr;
        int ret = call(GetPhysicalGpuFromDisplayId, display_id, arg(&gpu));
        if (ret != 0) {
            return ret;
        }
        if (gpu == nullptr) {
            return kBadOutput;
        }
        LUID luid{};
        ret = call(GetAdapterIdFromPhysicalGpu, arg(gpu), arg(&luid));
        if (ret != 0) {
            return ret;
        }
        out = luid;
        return 0;
    }

    EdidRoute read_edid(uint32_t display_id, std::vector<uint8_t>& out) override
    {
        out.clear();
        if (read_edid_of_display(display_id, out)) {
            return EdidRoute::Nvapi;
        }
        out.clear();
        if (read_edid_of_output(display_id, out)) {
            return EdidRoute::NvapiOutput;
        }
        out.clear();
        return read_edid_from_registry(out) ? EdidRoute::Registry : EdidRoute::None;
    }

    int enable_direct_mode(uint32_t vendor) override { return call(EnableDirectMode, vendor); }

    int bind_device(uint32_t display_id, IUnknown* d3d11, uint32_t vendor) override
    {
        if (d3d11 == nullptr) {
            return kBadArgument;
        }
        bound_ = false;
        handle_ = Handle{};
        handle_.display_id = display_id;
        const int ret = call(BindDevice, vendor, arg(d3d11), arg(&handle_));
        if (ret != 0) {
            return ret;
        }
        if (handle_.nvapi == nullptr) {
            return kBadOutput;
        }
        bound_ = true;
        return 0;
    }

    int create_back_buffer(const Mode& mode, BackBuffer& out) override
    {
        if (!bound_) {
            return kNotBound;
        }
        Mode request = mode;
        // Only the first dword of each output is known; Pimax passes room for more, so do we.
        uint64_t tag[4] = {};
        uint64_t shared[4] = {};
        const int ret = call(CreateBackBuffer, arg(&handle_), arg(&request), arg(tag), arg(shared));
        if (ret != 0) {
            return ret;
        }
        if (shared[0] == 0) {
            return kBadOutput;
        }
        out.tag = static_cast<uint32_t>(tag[0]);
        out.shared = reinterpret_cast<HANDLE>(static_cast<uintptr_t>(static_cast<uint32_t>(shared[0])));
        return 0;
    }

    int set_display_mode(const Mode& mode) override
    {
        if (!bound_) {
            return kNotBound;
        }
        Mode request = mode;
        return call(SetDisplayMode, arg(&handle_), arg(&request));
    }

    int present(uint64_t tag) override
    {
        if (!bound_) {
            return kNotBound;
        }
        return call(Present, arg(&handle_), static_cast<uintptr_t>(tag), 1);
    }

    int present_stats(RefreshStats& out) override
    {
        if (!bound_) {
            return kNotBound;
        }
        RawStats stats;
        const int ret = call(GetPresentStats, arg(&handle_), arg(&stats));
        if (ret != 0) {
            return ret;
        }
        out.refresh_num = stats.refresh_num;
        out.refresh_den = stats.refresh_den;
        out.counter = stats.frame_counter;
        out.qpc_ns = qpc_ticks_to_ns(static_cast<int64_t>(stats.qpc), qpc_frequency_);
        return 0;
    }

private:
    CallFn function(Id id) const
    {
        for (size_t i = 0; i < std::size(kAllIds); i++) {
            if (kAllIds[i].id == id) {
                return reinterpret_cast<CallFn>(fns_[i]);
            }
        }
        return nullptr;
    }

    int call(Id id, uintptr_t a = 0, uintptr_t b = 0, uintptr_t c = 0, uintptr_t d = 0, uintptr_t e = 0,
             uintptr_t f = 0) const
    {
        const CallFn fn = function(id);
        return fn != nullptr ? fn(a, b, c, d, e, f) : kNoEntry;
    }

    bool read_edid_of_display(uint32_t display_id, std::vector<uint8_t>& out)
    {
        void* gpu = nullptr;
        if (call(GetPhysicalGpuFromDisplayId, display_id, arg(&gpu)) != 0 || gpu == nullptr) {
            return false;
        }
        return read_edid_pages(gpu, display_id, out);
    }

    bool read_edid_of_output(uint32_t display_id, std::vector<uint8_t>& out)
    {
        void* gpu = nullptr;
        uint32_t output_id = 0;
        if (call(GetGpuAndOutputIdFromDisplayId, display_id, arg(&gpu), arg(&output_id)) != 0 || gpu == nullptr) {
            return false;
        }
        return read_edid_pages(gpu, output_id, out);
    }

    bool read_edid_pages(void* gpu, uint32_t output, std::vector<uint8_t>& out)
    {
        uint32_t total = 0;
        uint32_t edid_id = 0;
        for (uint32_t page = 0; page < kEdidMaxPages; page++) {
            EdidBuffer buffer{};
            buffer.page.version = kEdidPageVersion;
            buffer.page.offset = page * kEdidPage;
            if (call(GetEDID, arg(gpu), output, arg(&buffer)) != 0) {
                return false;
            }
            if (page == 0) {
                total = buffer.page.size;
                edid_id = buffer.page.id;
                if (total < kEdidBlock || total > kEdidMaxPages * kEdidPage) {
                    return false;
                }
            } else if (buffer.page.size != total || buffer.page.id != edid_id) {
                return false;
            }
            const uint32_t take = std::min(kEdidPage, total - buffer.page.offset);
            out.insert(out.end(), buffer.page.data, buffer.page.data + take);
            if (out.size() >= total) {
                break;
            }
        }
        return out.size() == total && looks_like_edid(out);
    }

    HMODULE dll_ = nullptr;
    QueryInterfaceFn query_ = nullptr;
    bool initialized_ = false;
    void* fns_[std::size(kAllIds)] = {};

    Handle handle_;
    bool bound_ = false;

    int64_t qpc_frequency_ = 1;
};

}

std::unique_ptr<Api> make_nvapi()
{
    return std::make_unique<NvapiApi>();
}

namespace {

constexpr wchar_t kDreamAirModelKey[] = L"PVR1019";

bool edid_from_model(HKEY display, const wchar_t* model_name, std::vector<uint8_t>& out)
{
    RegKey model;
    if (RegOpenKeyExW(display, model_name, 0, KEY_READ, &model.key) != ERROR_SUCCESS) {
        return false;
    }
    for (DWORD i = 0;; i++) {
        wchar_t instance_name[256];
        DWORD instance_length = static_cast<DWORD>(std::size(instance_name));
        const LONG status =
            RegEnumKeyExW(model.key, i, instance_name, &instance_length, nullptr, nullptr, nullptr, nullptr);
        if (status == ERROR_MORE_DATA) {
            continue;
        }
        if (status != ERROR_SUCCESS) {
            break;
        }
        const std::wstring subkey = std::wstring(instance_name) + L"\\Device Parameters";
        std::vector<uint8_t> edid = read_binary_value(model.key, subkey.c_str(), L"EDID");
        if (looks_like_edid(edid)) {
            out = std::move(edid);
            return true;
        }
    }
    return false;
}

}

bool read_edid_from_display_key(HKEY display, std::vector<uint8_t>& out)
{
    out.clear();

    if (edid_from_model(display, kDreamAirModelKey, out)) {
        return true;
    }

    for (DWORD m = 0;; m++) {
        wchar_t model_name[256];
        DWORD model_length = static_cast<DWORD>(std::size(model_name));
        const LONG status = RegEnumKeyExW(display, m, model_name, &model_length, nullptr, nullptr, nullptr, nullptr);
        if (status == ERROR_MORE_DATA) {
            continue;
        }
        if (status != ERROR_SUCCESS) {
            break;
        }
        if (_wcsnicmp(model_name, L"PVR", 3) != 0 || _wcsicmp(model_name, kDreamAirModelKey) == 0) {
            continue;
        }
        if (edid_from_model(display, model_name, out)) {
            return true;
        }
    }
    return false;
}

bool read_edid_from_registry(std::vector<uint8_t>& out)
{
    out.clear();
    RegKey display;
    if (RegOpenKeyExW(HKEY_LOCAL_MACHINE, L"SYSTEM\\CurrentControlSet\\Enum\\DISPLAY", 0, KEY_READ, &display.key) !=
        ERROR_SUCCESS) {
        return false;
    }
    return read_edid_from_display_key(display.key, out);
}

}
