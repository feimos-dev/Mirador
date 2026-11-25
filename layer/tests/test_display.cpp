// Copyright 2026, Finn Zeruhn
// SPDX-License-Identifier: BSL-1.0

#include <catch2/catch_test_macros.hpp>

#include <cstring>
#include <memory>
#include <string>
#include <vector>

#include "fakes/fake_chain.hpp"
#include "layer/display.hpp"

namespace {

using dvl_test::ChainRig;
using dvl_test::g_driver;

uint32_t display_count(ChainRig& rig, size_t gpu)
{
    uint32_t n = 0;
    const auto query = rig.fn<PFN_vkGetPhysicalDeviceDisplayPropertiesKHR>("vkGetPhysicalDeviceDisplayPropertiesKHR");
    REQUIRE(query(ChainRig::gpu(gpu), &n, nullptr) == VK_SUCCESS);
    return n;
}

std::vector<VkDisplayPropertiesKHR> displays(ChainRig& rig, size_t gpu)
{
    const auto query = rig.fn<PFN_vkGetPhysicalDeviceDisplayPropertiesKHR>("vkGetPhysicalDeviceDisplayPropertiesKHR");
    uint32_t n = 0;
    REQUIRE(query(ChainRig::gpu(gpu), &n, nullptr) == VK_SUCCESS);
    std::vector<VkDisplayPropertiesKHR> list(n);
    REQUIRE(query(ChainRig::gpu(gpu), &n, list.data()) == VK_SUCCESS);
    list.resize(n);
    return list;
}

struct Ours {
    VkDisplayKHR display = VK_NULL_HANDLE;
    VkDisplayModeKHR mode = VK_NULL_HANDLE;
};

Ours find_ours(ChainRig& rig, size_t gpu = 0)
{
    Ours ours;
    const std::vector<VkDisplayPropertiesKHR> list = displays(rig, gpu);
    REQUIRE(!list.empty());
    ours.display = list[0].display;
    const auto modes = rig.fn<PFN_vkGetDisplayModePropertiesKHR>("vkGetDisplayModePropertiesKHR");
    uint32_t n = 1;
    VkDisplayModePropertiesKHR mode{};
    REQUIRE(modes(ChainRig::gpu(gpu), ours.display, &n, &mode) == VK_SUCCESS);
    ours.mode = mode.displayMode;
    return ours;
}

bool lines_contain(const std::vector<std::string>& lines, const char* text)
{
    for (const std::string& line : lines) {
        if (line.find(text) != std::string::npos) {
            return true;
        }
    }
    return false;
}

}

TEST_CASE("display_properties_values")
{
    VkDisplayPropertiesKHR p{};
    dvl::fill_display_properties(nvdm::kDreamAir, p);
    CHECK(p.display == dvl::our_display());
    REQUIRE(p.displayName != nullptr);
    CHECK(std::string(p.displayName) == "Pimax Dream Air");
    CHECK(p.physicalResolution.width == 7104);
    CHECK(p.physicalResolution.height == 3840);
    CHECK(p.supportedTransforms == VK_SURFACE_TRANSFORM_IDENTITY_BIT_KHR);
    CHECK(p.planeReorderPossible == VK_FALSE);
    CHECK(p.persistentContent == VK_FALSE);

    const VkDisplayModeParametersKHR mode = dvl::mode_parameters(nvdm::kDreamAir);
    CHECK(mode.visibleRegion.width == 7104);
    CHECK(mode.visibleRegion.height == 3840);
    CHECK(mode.refreshRate == 90000);
    CHECK(dvl::is_product_mode(nvdm::kDreamAir, mode));
    CHECK_FALSE(dvl::is_product_mode(nvdm::kDreamAir, VkDisplayModeParametersKHR{{7104, 3840}, 60000}));
    CHECK_FALSE(dvl::is_product_mode(nvdm::kDreamAir, VkDisplayModeParametersKHR{{3552, 3840}, 90000}));

    auto name = std::make_unique<std::string>("Some Headset");
    nvdm::Product other = nvdm::kDreamAir;
    other.name = name->c_str();
    VkDisplayPropertiesKHR q{};
    dvl::fill_display_properties(other, q);
    name.reset();
    CHECK(std::string(q.displayName) == "Some Headset");
    VkDisplayPropertiesKHR again{};
    dvl::fill_display_properties(nvdm::kDreamAir, again);
    CHECK(again.displayName == p.displayName);

    const VkDisplayPlanePropertiesKHR plane = dvl::our_plane_properties();
    CHECK(plane.currentDisplay == dvl::our_display());
    CHECK(plane.currentStackIndex == 0);
    CHECK(dvl::is_our_display(dvl::our_display()));
    CHECK(dvl::is_our_mode(dvl::our_mode()));
    CHECK_FALSE(dvl::is_our_display(VK_NULL_HANDLE));
    CHECK_FALSE(dvl::is_our_mode(VK_NULL_HANDLE));
    CHECK_FALSE(dvl::is_our_display(dvl_test::driver_display(0, 0)));
    CHECK_FALSE(dvl::is_our_mode(reinterpret_cast<VkDisplayModeKHR>(dvl::our_display())));
}

TEST_CASE("plane_alpha_opaque_and_global")
{
    const VkDisplayPlaneCapabilitiesKHR caps = dvl::plane_capabilities(nvdm::kDreamAir);
    CHECK(caps.supportedAlpha == (VK_DISPLAY_PLANE_ALPHA_OPAQUE_BIT_KHR | VK_DISPLAY_PLANE_ALPHA_GLOBAL_BIT_KHR));
    for (const VkExtent2D& e : {caps.minSrcExtent, caps.maxSrcExtent, caps.minDstExtent, caps.maxDstExtent}) {
        CHECK(e.width == 7104);
        CHECK(e.height == 3840);
    }
    for (const VkOffset2D& o : {caps.minSrcPosition, caps.maxSrcPosition, caps.minDstPosition, caps.maxDstPosition}) {
        CHECK(o.x == 0);
        CHECK(o.y == 0);
    }
}

TEST_CASE("plane_shift")
{
    const dvl::PlaneMap map{2};
    CHECK_FALSE(map.to_driver(0).has_value());
    CHECK(map.to_driver(1) == 0u);
    CHECK(map.to_driver(2) == 1u);
    CHECK_FALSE(map.to_driver(3).has_value());
    CHECK(map.count() == 3);
    const dvl::PlaneMap none{0};
    CHECK(none.count() == 1);
    CHECK_FALSE(none.to_driver(1).has_value());
}

TEST_CASE("display_listed_only_for_matching_luid")
{
    ChainRig rig;
    REQUIRE(rig.ready());
    CHECK(display_count(rig, 1) == 0);
    CHECK_FALSE(rig.session->is_open());
    CHECK(lines_contain(rig.session_log, "Luid:"));
    CHECK(display_count(rig, 0) == 1);
    CHECK(rig.session->is_open());
    CHECK(display_count(rig, 1) == 0);

    const size_t calls = rig.api.calls().size();
    const size_t lines = rig.session_log.size();
    CHECK(display_count(rig, 2) == 0);
    CHECK(rig.api.calls().size() == calls);
    CHECK(rig.session_log.size() == lines);

    CHECK(g_driver.count("vkGetPhysicalDeviceDisplayPropertiesKHR") == 4);
}

TEST_CASE("our_display_first")
{
    std::vector<dvl_test::FakeGpu> gpus = dvl_test::default_gpus();
    gpus[0].displays = 1;
    ChainRig rig(gpus);
    REQUIRE(rig.ready());

    const std::vector<VkDisplayPropertiesKHR> list = displays(rig, 0);
    REQUIRE(list.size() == 2);
    CHECK(std::string(list[0].displayName) == "Pimax Dream Air");
    CHECK(list[0].physicalResolution.width == 7104);
    CHECK(list[1].display == dvl_test::driver_display(0, 0));
    CHECK(std::string(list[1].displayName) == "driver display");

    const auto query = rig.fn<PFN_vkGetPhysicalDeviceDisplayPropertiesKHR>("vkGetPhysicalDeviceDisplayPropertiesKHR");
    uint32_t n = 1;
    VkDisplayPropertiesKHR one{};
    CHECK(query(ChainRig::gpu(0), &n, &one) == VK_INCOMPLETE);
    CHECK(n == 1);
    CHECK(one.display == list[0].display);

    const auto query2 =
        rig.fn<PFN_vkGetPhysicalDeviceDisplayProperties2KHR>("vkGetPhysicalDeviceDisplayProperties2KHR");
    n = 0;
    REQUIRE(query2(ChainRig::gpu(0), &n, nullptr) == VK_SUCCESS);
    REQUIRE(n == 2);
    std::vector<VkDisplayProperties2KHR> list2(2, VkDisplayProperties2KHR{VK_STRUCTURE_TYPE_DISPLAY_PROPERTIES_2_KHR});
    REQUIRE(query2(ChainRig::gpu(0), &n, list2.data()) == VK_SUCCESS);
    CHECK(list2[0].sType == VK_STRUCTURE_TYPE_DISPLAY_PROPERTIES_2_KHR);
    CHECK(list2[0].displayProperties.display == list[0].display);
    CHECK(std::string(list2[0].displayProperties.displayName) == "Pimax Dream Air");
    CHECK(list2[1].displayProperties.display == dvl_test::driver_display(0, 0));
}

TEST_CASE("display_not_listed_while_pimax_holds_it")
{
    ChainRig rig;
    REQUIRE(rig.ready());
    rig.running = {"pi_server.exe", "explorer.exe"};
    CHECK(display_count(rig, 0) == 0);
    CHECK(rig.api.calls().empty());
    REQUIRE(rig.session_log.size() == 1);
    CHECK(lines_contain(rig.session_log, "pi_server.exe"));

    const auto planes =
        rig.fn<PFN_vkGetPhysicalDeviceDisplayPlanePropertiesKHR>("vkGetPhysicalDeviceDisplayPlanePropertiesKHR");
    uint32_t n = 0;
    CHECK(planes(ChainRig::gpu(0), &n, nullptr) == VK_SUCCESS);
    CHECK(n == 0);
    CHECK(rig.session_log.size() == 1);
    CHECK(g_driver.count("vkGetPhysicalDeviceDisplayPlanePropertiesKHR") == 1);
}

TEST_CASE("display_session_opened_by_the_first_query_once")
{
    ChainRig rig;
    REQUIRE(rig.ready());
    CHECK(display_count(rig, 0) == 1);
    const size_t calls = rig.api.calls().size();
    CHECK(rig.api.count("enable_direct_mode") == 2);
    CHECK(display_count(rig, 0) == 1);
    find_ours(rig);
    const auto planes =
        rig.fn<PFN_vkGetPhysicalDeviceDisplayPlanePropertiesKHR>("vkGetPhysicalDeviceDisplayPlanePropertiesKHR");
    uint32_t n = 0;
    CHECK(planes(ChainRig::gpu(0), &n, nullptr) == VK_SUCCESS);
    CHECK(n == 1);
    CHECK(rig.api.calls().size() == calls);
}

TEST_CASE("mode_properties_of_our_display")
{
    ChainRig rig;
    REQUIRE(rig.ready());
    const Ours ours = find_ours(rig);
    CHECK(ours.mode != VK_NULL_HANDLE);
    g_driver.clear_calls();

    const auto modes2 = rig.fn<PFN_vkGetDisplayModeProperties2KHR>("vkGetDisplayModeProperties2KHR");
    uint32_t n = 0;
    REQUIRE(modes2(ChainRig::gpu(0), ours.display, &n, nullptr) == VK_SUCCESS);
    CHECK(n == 1);
    VkDisplayModeStereoPropertiesNV stereo{VK_STRUCTURE_TYPE_DISPLAY_MODE_STEREO_PROPERTIES_NV};
    stereo.hdmi3DSupported = VK_TRUE;
    VkDisplayModeProperties2KHR mode{VK_STRUCTURE_TYPE_DISPLAY_MODE_PROPERTIES_2_KHR, &stereo};
    REQUIRE(modes2(ChainRig::gpu(0), ours.display, &n, &mode) == VK_SUCCESS);
    CHECK(mode.pNext == &stereo);
    CHECK(mode.displayModeProperties.displayMode == ours.mode);
    CHECK(mode.displayModeProperties.parameters.visibleRegion.width == 7104);
    CHECK(mode.displayModeProperties.parameters.visibleRegion.height == 3840);
    CHECK(mode.displayModeProperties.parameters.refreshRate == 90000);
    CHECK(stereo.hdmi3DSupported == VK_FALSE);

    const auto supported =
        rig.fn<PFN_vkGetDisplayPlaneSupportedDisplaysKHR>("vkGetDisplayPlaneSupportedDisplaysKHR");
    VkDisplayKHR display = VK_NULL_HANDLE;
    n = 1;
    REQUIRE(supported(ChainRig::gpu(0), 0, &n, &display) == VK_SUCCESS);
    CHECK(n == 1);
    CHECK(display == ours.display);
    const auto planes2 =
        rig.fn<PFN_vkGetPhysicalDeviceDisplayPlaneProperties2KHR>("vkGetPhysicalDeviceDisplayPlaneProperties2KHR");
    VkDisplayPlaneProperties2KHR plane{VK_STRUCTURE_TYPE_DISPLAY_PLANE_PROPERTIES_2_KHR};
    n = 1;
    REQUIRE(planes2(ChainRig::gpu(0), &n, &plane) == VK_SUCCESS);
    CHECK(plane.displayPlaneProperties.currentDisplay == ours.display);
    CHECK(plane.displayPlaneProperties.currentStackIndex == 0);
    CHECK(g_driver.count("vkGetPhysicalDeviceDisplayPlaneProperties2KHR") == 1);
    g_driver.clear_calls();

    const auto caps = rig.fn<PFN_vkGetDisplayPlaneCapabilitiesKHR>("vkGetDisplayPlaneCapabilitiesKHR");
    VkDisplayPlaneCapabilitiesKHR c{};
    REQUIRE(caps(ChainRig::gpu(0), ours.mode, 0, &c) == VK_SUCCESS);
    CHECK(c.supportedAlpha == (VK_DISPLAY_PLANE_ALPHA_OPAQUE_BIT_KHR | VK_DISPLAY_PLANE_ALPHA_GLOBAL_BIT_KHR));
    CHECK(c.maxDstExtent.width == 7104);
    const auto caps2 = rig.fn<PFN_vkGetDisplayPlaneCapabilities2KHR>("vkGetDisplayPlaneCapabilities2KHR");
    VkDisplayPlaneInfo2KHR info{VK_STRUCTURE_TYPE_DISPLAY_PLANE_INFO_2_KHR, nullptr, ours.mode, 0};
    VkDisplayPlaneCapabilities2KHR c2{VK_STRUCTURE_TYPE_DISPLAY_PLANE_CAPABILITIES_2_KHR};
    REQUIRE(caps2(ChainRig::gpu(0), &info, &c2) == VK_SUCCESS);
    CHECK(c2.sType == VK_STRUCTURE_TYPE_DISPLAY_PLANE_CAPABILITIES_2_KHR);
    CHECK(c2.capabilities.supportedAlpha == c.supportedAlpha);

    CHECK(g_driver.calls().empty());
}

TEST_CASE("create_display_mode_other_than_ours_fails")
{
    std::vector<dvl_test::FakeGpu> gpus = dvl_test::default_gpus();
    gpus[0].displays = 1;
    ChainRig rig(gpus);
    REQUIRE(rig.ready());
    const Ours ours = find_ours(rig);
    g_driver.clear_calls();
    const auto create = rig.fn<PFN_vkCreateDisplayModeKHR>("vkCreateDisplayModeKHR");

    VkDisplayModeCreateInfoKHR ci{VK_STRUCTURE_TYPE_DISPLAY_MODE_CREATE_INFO_KHR};
    ci.parameters = {{7104, 3840}, 60000};
    VkDisplayModeKHR mode = VK_NULL_HANDLE;
    CHECK(create(ChainRig::gpu(0), ours.display, &ci, nullptr, &mode) == VK_ERROR_INITIALIZATION_FAILED);
    ci.parameters = {{1920, 1080}, 90000};
    CHECK(create(ChainRig::gpu(0), ours.display, &ci, nullptr, &mode) == VK_ERROR_INITIALIZATION_FAILED);
    ci.parameters = {{7104, 3840}, 90000};
    REQUIRE(create(ChainRig::gpu(0), ours.display, &ci, nullptr, &mode) == VK_SUCCESS);
    CHECK(mode == ours.mode);
    CHECK(g_driver.calls().empty());

    ci.parameters = {{1920, 1080}, 60000};
    REQUIRE(create(ChainRig::gpu(0), dvl_test::driver_display(0, 0), &ci, nullptr, &mode) == VK_SUCCESS);
    CHECK(mode == dvl_test::driver_mode(0, 1));
    CHECK(g_driver.last("vkCreateDisplayModeKHR").handle == dvl_test::handle_value(dvl_test::driver_display(0, 0)));
}

TEST_CASE("plane_pairs_without_valid_call_down_answered_by_layer")
{
    std::vector<dvl_test::FakeGpu> gpus = dvl_test::default_gpus();
    gpus[0].displays = 1;
    gpus[0].planes = 1;
    ChainRig rig(gpus);
    REQUIRE(rig.ready());
    const Ours ours = find_ours(rig);
    const VkDisplayModeKHR driver_mode = dvl_test::driver_mode(0, 0);
    g_driver.clear_calls();

    const auto caps = rig.fn<PFN_vkGetDisplayPlaneCapabilitiesKHR>("vkGetDisplayPlaneCapabilitiesKHR");
    const auto caps2 = rig.fn<PFN_vkGetDisplayPlaneCapabilities2KHR>("vkGetDisplayPlaneCapabilities2KHR");
    VkDisplayPlaneCapabilitiesKHR c{};
    c.supportedAlpha = VK_DISPLAY_PLANE_ALPHA_PER_PIXEL_BIT_KHR;
    REQUIRE(caps(ChainRig::gpu(0), ours.mode, 1, &c) == VK_SUCCESS);
    CHECK(c.supportedAlpha == 0);
    c.supportedAlpha = VK_DISPLAY_PLANE_ALPHA_PER_PIXEL_BIT_KHR;
    REQUIRE(caps(ChainRig::gpu(0), driver_mode, 0, &c) == VK_SUCCESS);
    CHECK(c.supportedAlpha == 0);
    VkDisplayPlaneCapabilities2KHR c2{VK_STRUCTURE_TYPE_DISPLAY_PLANE_CAPABILITIES_2_KHR};
    VkDisplayPlaneInfo2KHR info{VK_STRUCTURE_TYPE_DISPLAY_PLANE_INFO_2_KHR, nullptr, ours.mode, 1};
    REQUIRE(caps2(ChainRig::gpu(0), &info, &c2) == VK_SUCCESS);
    CHECK(c2.capabilities.supportedAlpha == 0);
    info = {VK_STRUCTURE_TYPE_DISPLAY_PLANE_INFO_2_KHR, nullptr, driver_mode, 0};
    REQUIRE(caps2(ChainRig::gpu(0), &info, &c2) == VK_SUCCESS);
    CHECK(c2.capabilities.supportedAlpha == 0);

    const auto create_surface = rig.fn<PFN_vkCreateDisplayPlaneSurfaceKHR>("vkCreateDisplayPlaneSurfaceKHR");
    VkDisplaySurfaceCreateInfoKHR sci{VK_STRUCTURE_TYPE_DISPLAY_SURFACE_CREATE_INFO_KHR};
    sci.displayMode = ours.mode;
    sci.planeIndex = 1;
    sci.alphaMode = VK_DISPLAY_PLANE_ALPHA_OPAQUE_BIT_KHR;
    sci.imageExtent = {7104, 3840};
    VkSurfaceKHR surface = VK_NULL_HANDLE;
    CHECK(create_surface(rig.instance, &sci, nullptr, &surface) < 0);
    sci.displayMode = driver_mode;
    sci.planeIndex = 0;
    CHECK(create_surface(rig.instance, &sci, nullptr, &surface) < 0);
    CHECK(surface == VK_NULL_HANDLE);

    CHECK(g_driver.calls().empty());

    c.supportedAlpha = 0;
    REQUIRE(caps(ChainRig::gpu(0), driver_mode, 1, &c) == VK_SUCCESS);
    CHECK(c.supportedAlpha == VK_DISPLAY_PLANE_ALPHA_PER_PIXEL_BIT_KHR);
    CHECK(g_driver.last("vkGetDisplayPlaneCapabilitiesKHR").index == 0);
    CHECK(g_driver.last("vkGetDisplayPlaneCapabilitiesKHR").handle == dvl_test::handle_value(driver_mode));
    info = {VK_STRUCTURE_TYPE_DISPLAY_PLANE_INFO_2_KHR, nullptr, driver_mode, 1};
    REQUIRE(caps2(ChainRig::gpu(0), &info, &c2) == VK_SUCCESS);
    CHECK(g_driver.last("vkGetDisplayPlaneCapabilities2KHR").index == 0);
    CHECK(info.planeIndex == 1);
    sci.planeIndex = 1;
    REQUIRE(create_surface(rig.instance, &sci, nullptr, &surface) == VK_SUCCESS);
    CHECK(g_driver.last("vkCreateDisplayPlaneSurfaceKHR").index == 0);
    CHECK(sci.planeIndex == 1);
    c.supportedAlpha = VK_DISPLAY_PLANE_ALPHA_PER_PIXEL_BIT_KHR;
    g_driver.clear_calls();
    REQUIRE(caps(ChainRig::gpu(0), driver_mode, 2, &c) == VK_SUCCESS);
    CHECK(c.supportedAlpha == 0);
    CHECK(g_driver.count("vkGetDisplayPlaneCapabilitiesKHR") == 0);
}

TEST_CASE("driver_planes_follow_ours")
{
    std::vector<dvl_test::FakeGpu> gpus = dvl_test::default_gpus();
    gpus[0].displays = 1;
    gpus[0].planes = 2;
    ChainRig rig(gpus);
    REQUIRE(rig.ready());
    const Ours ours = find_ours(rig);

    const auto planes =
        rig.fn<PFN_vkGetPhysicalDeviceDisplayPlanePropertiesKHR>("vkGetPhysicalDeviceDisplayPlanePropertiesKHR");
    uint32_t n = 0;
    REQUIRE(planes(ChainRig::gpu(0), &n, nullptr) == VK_SUCCESS);
    REQUIRE(n == 3);
    std::vector<VkDisplayPlanePropertiesKHR> list(n);
    REQUIRE(planes(ChainRig::gpu(0), &n, list.data()) == VK_SUCCESS);
    CHECK(list[0].currentDisplay == ours.display);
    CHECK(list[0].currentStackIndex == 0);
    CHECK(list[1].currentDisplay == dvl_test::driver_display(0, 0));
    CHECK(list[2].currentStackIndex == 1);

    const auto supported =
        rig.fn<PFN_vkGetDisplayPlaneSupportedDisplaysKHR>("vkGetDisplayPlaneSupportedDisplaysKHR");
    g_driver.clear_calls();
    n = 0;
    REQUIRE(supported(ChainRig::gpu(0), 2, &n, nullptr) == VK_SUCCESS);
    CHECK(n == 1);
    CHECK(g_driver.last("vkGetDisplayPlaneSupportedDisplaysKHR").index == 1);
    g_driver.clear_calls();
    n = 5;
    REQUIRE(supported(ChainRig::gpu(0), 3, &n, nullptr) == VK_SUCCESS);
    CHECK(n == 0);
    CHECK(g_driver.count("vkGetDisplayPlaneSupportedDisplaysKHR") == 0);
}

TEST_CASE("foreign_display_calls_go_down_unchanged")
{
    std::vector<dvl_test::FakeGpu> gpus = dvl_test::default_gpus();
    gpus[1].displays = 1;
    gpus[1].planes = 1;
    ChainRig rig(gpus);
    REQUIRE(rig.ready());
    const VkPhysicalDevice other = ChainRig::gpu(1);
    const VkDisplayKHR display = dvl_test::driver_display(1, 0);

    CHECK(display_count(rig, 1) == 1);
    const auto planes =
        rig.fn<PFN_vkGetPhysicalDeviceDisplayPlanePropertiesKHR>("vkGetPhysicalDeviceDisplayPlanePropertiesKHR");
    uint32_t n = 0;
    REQUIRE(planes(other, &n, nullptr) == VK_SUCCESS);
    CHECK(n == 1);
    const auto supported =
        rig.fn<PFN_vkGetDisplayPlaneSupportedDisplaysKHR>("vkGetDisplayPlaneSupportedDisplaysKHR");
    REQUIRE(supported(other, 0, &n, nullptr) == VK_SUCCESS);
    CHECK(g_driver.last("vkGetDisplayPlaneSupportedDisplaysKHR").index == 0);
    const auto caps = rig.fn<PFN_vkGetDisplayPlaneCapabilitiesKHR>("vkGetDisplayPlaneCapabilitiesKHR");
    VkDisplayPlaneCapabilitiesKHR c{};
    REQUIRE(caps(other, dvl_test::driver_mode(1, 0), 0, &c) == VK_SUCCESS);
    CHECK(g_driver.last("vkGetDisplayPlaneCapabilitiesKHR").index == 0);
    CHECK(c.supportedAlpha == VK_DISPLAY_PLANE_ALPHA_PER_PIXEL_BIT_KHR);

    const auto modes = rig.fn<PFN_vkGetDisplayModePropertiesKHR>("vkGetDisplayModePropertiesKHR");
    REQUIRE(modes(other, display, &n, nullptr) == VK_SUCCESS);
    CHECK(g_driver.last("vkGetDisplayModePropertiesKHR").handle == dvl_test::handle_value(display));
    const auto release = rig.fn<PFN_vkReleaseDisplayEXT>("vkReleaseDisplayEXT");
    CHECK(release(other, display) == VK_SUCCESS);
    CHECK(g_driver.last("vkReleaseDisplayEXT").handle == dvl_test::handle_value(display));
    const auto acquire = rig.fn<PFN_vkAcquireWinrtDisplayNV>("vkAcquireWinrtDisplayNV");
    CHECK(acquire(other, display) == VK_SUCCESS);
    CHECK(g_driver.last("vkAcquireWinrtDisplayNV").handle == dvl_test::handle_value(display));
    const auto get_winrt = rig.fn<PFN_vkGetWinrtDisplayNV>("vkGetWinrtDisplayNV");
    VkDisplayKHR got = VK_NULL_HANDLE;
    CHECK(get_winrt(other, 7, &got) == VK_SUCCESS);
    CHECK(got == display);
    CHECK(g_driver.last("vkGetWinrtDisplayNV").index == 7);
}

TEST_CASE("our_display_refuses_winrt_and_ignores_release")
{
    ChainRig rig;
    REQUIRE(rig.ready());
    const Ours ours = find_ours(rig);
    g_driver.clear_calls();
    const auto acquire = rig.fn<PFN_vkAcquireWinrtDisplayNV>("vkAcquireWinrtDisplayNV");
    CHECK(acquire(ChainRig::gpu(0), ours.display) == VK_ERROR_INITIALIZATION_FAILED);
    const auto release = rig.fn<PFN_vkReleaseDisplayEXT>("vkReleaseDisplayEXT");
    CHECK(release(ChainRig::gpu(0), ours.display) == VK_SUCCESS);
    CHECK(g_driver.calls().empty());
}
