// Copyright 2026, Finn Zeruhn
// SPDX-License-Identifier: BSL-1.0

#include <catch2/catch_test_macros.hpp>

#include <cstdint>
#include <memory>
#include <string>
#include <vector>

#include "core/api.hpp"
#include "core/clock.hpp"
#include "fakes/fake_api.hpp"
#include "fakes/fake_clock.hpp"

namespace {

using nvdm::FakeApi;
using nvdm::FakeClock;
using Fn = nvdm::FakeApi::Fn;

constexpr int64_t kRefreshNs = nvdm::FakeApi::kDefaultRefreshNs;

nvdm::RefreshStats poll(FakeApi& api)
{
    nvdm::RefreshStats stats{};
    REQUIRE(api.present_stats(stats) == 0);
    return stats;
}

}

TEST_CASE("qpc_ticks_to_ns converts without overflowing")
{
    CHECK(nvdm::qpc_ticks_to_ns(10'000'000, 10'000'000) == 1'000'000'000);
    CHECK(nvdm::qpc_ticks_to_ns(1, 10'000'000) == 100);
    CHECK(nvdm::qpc_ticks_to_ns(0, 10'000'000) == 0);
    CHECK(nvdm::qpc_ticks_to_ns(1, 3) == 333'333'333);
    CHECK(nvdm::qpc_ticks_to_ns(4, 3) == 1'333'333'333);
    CHECK(nvdm::qpc_ticks_to_ns(1'000'000'000'000'000, 10'000'000) == 100'000'000'000'000'000);
    CHECK(nvdm::qpc_ticks_to_ns(5, 0) == 0);
}

TEST_CASE("QpcClock is on the timebase of QueryPerformanceCounter, like present_stats' qpc")
{
    LARGE_INTEGER frequency{};
    REQUIRE(QueryPerformanceFrequency(&frequency));

    nvdm::QpcClock clock;
    const int64_t before = clock.now_ns();
    LARGE_INTEGER counter{};
    REQUIRE(QueryPerformanceCounter(&counter));
    const int64_t between = nvdm::qpc_ticks_to_ns(counter.QuadPart, frequency.QuadPart);
    const int64_t after = clock.now_ns();

    CHECK(before > 0);
    CHECK(before <= between);
    CHECK(between <= after);
}

TEST_CASE("read_edid_from_registry returns a base-block EDID or nothing")
{
    std::vector<uint8_t> edid{0xaa, 0xbb};
    if (nvdm::read_edid_from_registry(edid)) {
        REQUIRE(edid.size() >= 128);
        const uint8_t header[8] = {0x00, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0x00};
        CHECK(std::vector<uint8_t>(edid.begin(), edid.begin() + 8) == std::vector<uint8_t>(header, header + 8));
    } else {
        CHECK(edid.empty());
    }
}

TEST_CASE("bind_device refuses a NULL device without calling the driver")
{
    FakeClock clock;
    FakeApi api(clock);
    IUnknown* const device = reinterpret_cast<IUnknown*>(0x1000);

    api.queue(Fn::BindDevice, -5);
    CHECK(api.bind_device(1, nullptr, nvdm::kVendorPvr) == nvdm::kBadArgument);
    CHECK(api.bound_device == nullptr);
    CHECK(api.bind_device(1, device, nvdm::kVendorPvr) == -5);

    const std::unique_ptr<nvdm::Api> real = nvdm::make_nvapi();
    CHECK(real->bind_device(1, nullptr, nvdm::kVendorPvr) == nvdm::kBadArgument);
}

namespace {

class ScratchDisplayKey {
public:
    ScratchDisplayKey()
        : path_(L"Software\\MiradorDirectModeTests\\" + std::to_wstring(GetCurrentProcessId()))
    {
        const LONG status = RegCreateKeyExW(HKEY_CURRENT_USER, path_.c_str(), 0, nullptr, 0, KEY_ALL_ACCESS, nullptr,
                                            &root_, nullptr);
        REQUIRE(status == ERROR_SUCCESS);
    }
    ScratchDisplayKey(const ScratchDisplayKey&) = delete;
    ScratchDisplayKey& operator=(const ScratchDisplayKey&) = delete;
    ~ScratchDisplayKey()
    {
        if (root_ != nullptr) {
            RegCloseKey(root_);
        }
        RegDeleteTreeW(HKEY_CURRENT_USER, path_.c_str());
        RegDeleteKeyW(HKEY_CURRENT_USER, L"Software\\MiradorDirectModeTests");
    }

    HKEY key() const { return root_; }

    void add(const wchar_t* model, const wchar_t* instance, const std::vector<uint8_t>& edid)
    {
        const std::wstring sub = std::wstring(model) + L"\\" + instance + L"\\Device Parameters";
        HKEY parameters = nullptr;
        REQUIRE(RegCreateKeyExW(root_, sub.c_str(), 0, nullptr, 0, KEY_ALL_ACCESS, nullptr, &parameters, nullptr) ==
                ERROR_SUCCESS);
        REQUIRE(RegSetValueExW(parameters, L"EDID", 0, REG_BINARY, edid.data(), static_cast<DWORD>(edid.size())) ==
                ERROR_SUCCESS);
        RegCloseKey(parameters);
    }

    void remove_model(const wchar_t* model) { RegDeleteTreeW(root_, model); }

private:
    std::wstring path_;
    HKEY root_ = nullptr;
};

uint16_t product_of(const std::vector<uint8_t>& edid)
{
    REQUIRE(edid.size() >= 12);
    return static_cast<uint16_t>(edid[10] | edid[11] << 8);
}

}

TEST_CASE("the registry scan prefers the PVR1019 key, then any other PVR key")
{
    ScratchDisplayKey scratch;
    scratch.add(L"ABC0001", L"inst", FakeApi::make_edid(0x0421, 0x0001));
    scratch.add(L"PVR0001", L"inst", FakeApi::make_edid(0x42d2, 0x0002));
    scratch.add(L"PVR1019", L"inst", FakeApi::make_edid(0x42d2, 0x1019));
    scratch.add(L"PVR2000", L"inst", FakeApi::make_edid(0x42d2, 0x2000));

    std::vector<uint8_t> edid{0xaa};
    REQUIRE(nvdm::read_edid_from_display_key(scratch.key(), edid));
    CHECK(product_of(edid) == 0x1019);

    scratch.remove_model(L"PVR1019");
    REQUIRE(nvdm::read_edid_from_display_key(scratch.key(), edid));
    const uint16_t other = product_of(edid);
    CHECK((other == 0x0002 || other == 0x2000));

    scratch.remove_model(L"PVR0001");
    scratch.remove_model(L"PVR2000");
    CHECK_FALSE(nvdm::read_edid_from_display_key(scratch.key(), edid));
    CHECK(edid.empty());
}

TEST_CASE("the registry scan skips a PVR1019 instance with no usable EDID")
{
    ScratchDisplayKey scratch;
    scratch.add(L"PVR1019", L"broken", std::vector<uint8_t>(128, 0x11));
    scratch.add(L"PVR0001", L"inst", FakeApi::make_edid(0x42d2, 0x0002));

    std::vector<uint8_t> edid;
    REQUIRE(nvdm::read_edid_from_display_key(scratch.key(), edid));
    CHECK(product_of(edid) == 0x0002);

    scratch.add(L"PVR1019", L"good", FakeApi::make_edid(0x42d2, 0x1019));
    REQUIRE(nvdm::read_edid_from_display_key(scratch.key(), edid));
    CHECK(product_of(edid) == 0x1019);
}
