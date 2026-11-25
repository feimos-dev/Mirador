// Copyright 2026, Finn Zeruhn
// SPDX-License-Identifier: BSL-1.0

#include <catch2/catch_test_macros.hpp>

#include <algorithm>
#include <set>
#include <sstream>
#include <string>
#include <string_view>
#include <utility>
#include <vector>

#include "fakes/layer_dll.hpp"
#include "layer/handled_commands.hpp"

namespace {

bool listed(std::string_view name, std::string_view extension = {})
{
    for (const dvl::HandleCommand& c : dvl::handle_commands()) {
        if (c.name == name && (extension.empty() || c.extension == extension)) {
            return true;
        }
    }
    return false;
}

bool classified(std::string_view name)
{
    return dvl::handled_command(name) || dvl::not_applicable(name) != nullptr;
}

bool contains(const std::string& text, const char* part)
{
    return text.find(part) != std::string::npos;
}

}

TEST_CASE("every_handle_command_is_classified")
{
    std::set<std::string> unclassified;
    for (const dvl::HandleCommand& c : dvl::handle_commands()) {
        if (!classified(c.name)) {
            unclassified.emplace(c.name);
        }
    }
    std::ostringstream names;
    for (const std::string& n : unclassified) {
        names << "\n  " << n;
    }
    INFO(unclassified.size() << " commands take our handles and are neither handled nor not applicable "
                             << "(layer/handled_commands.cpp):" << names.str());
    CHECK(unclassified.empty());
}

TEST_CASE("generated_list_has_the_commands_the_design_names")
{
    for (const char* name : {"vkGetPhysicalDevicePresentRectanglesKHR", "vkGetDeviceGroupSurfacePresentModesKHR",
                             "vkCreateSharedSwapchainsKHR", "vkGetPastPresentationTimingGOOGLE", "vkSetHdrMetadataEXT",
                             "vkAcquireFullScreenExclusiveModeEXT", "vkReleaseFullScreenExclusiveModeEXT",
                             "vkReleaseSwapchainImagesEXT", "vkSetDebugUtilsObjectNameEXT",
                             "vkSetDebugUtilsObjectTagEXT", "vkDebugMarkerSetObjectNameEXT"}) {
        INFO(name);
        CHECK(listed(name));
    }
    for (const char* name :
         {"vkGetPhysicalDeviceDisplayPropertiesKHR", "vkGetPhysicalDeviceDisplayProperties2KHR",
          "vkGetPhysicalDeviceDisplayPlanePropertiesKHR", "vkGetDisplayPlaneSupportedDisplaysKHR",
          "vkGetDisplayModePropertiesKHR", "vkCreateDisplayModeKHR", "vkGetDisplayPlaneCapabilitiesKHR",
          "vkGetDisplayPlaneCapabilities2KHR", "vkCreateDisplayPlaneSurfaceKHR", "vkDestroySurfaceKHR",
          "vkGetPhysicalDeviceSurfaceSupportKHR", "vkGetPhysicalDeviceSurfaceCapabilitiesKHR",
          "vkGetPhysicalDeviceSurfaceFormatsKHR", "vkGetPhysicalDeviceSurfacePresentModesKHR",
          "vkGetPhysicalDeviceSurfaceCapabilities2KHR", "vkGetPhysicalDeviceSurfaceFormats2KHR",
          "vkGetPhysicalDeviceSurfaceCapabilities2EXT", "vkCreateSwapchainKHR", "vkDestroySwapchainKHR",
          "vkGetSwapchainImagesKHR", "vkAcquireNextImageKHR", "vkAcquireNextImage2KHR", "vkQueuePresentKHR",
          "vkWaitForPresentKHR", "vkGetSwapchainStatusKHR", "vkRegisterDisplayEventEXT", "vkDisplayPowerControlEXT",
          "vkGetSwapchainCounterEXT", "vkGetRefreshCycleDurationGOOGLE", "vkDebugMarkerSetObjectTagEXT"}) {
        INFO(name);
        CHECK(listed(name));
    }
    for (const char* name : {"vkCreateInstance", "vkRegisterDeviceEventEXT", "vkWaitForFences", "vkQueueSubmit",
                             "vkGetDeviceGroupPresentCapabilitiesKHR", "vkGetPhysicalDeviceWin32PresentationSupportKHR",
                             "vkCreateGraphicsPipelines"}) {
        INFO(name);
        CHECK_FALSE(listed(name));
    }
}

TEST_CASE("generated_list_has_a_line_for_each_extension_that_requires_a_command")
{
    for (const char* name : {"vkAcquireNextImage2KHR", "vkGetDeviceGroupSurfacePresentModesKHR",
                             "vkGetPhysicalDevicePresentRectanglesKHR"}) {
        INFO(name);
        CHECK(listed(name, "VK_KHR_swapchain"));
        CHECK(listed(name, "VK_KHR_device_group"));
    }
    CHECK(listed("vkCreateImage", "VK_VERSION_1_0"));
    CHECK(listed("vkBindImageMemory2", "VK_VERSION_1_1"));
    CHECK(listed("vkBindImageMemory2KHR", "VK_KHR_bind_memory2"));
    CHECK(listed("vkSetPrivateData", "VK_VERSION_1_3"));
    CHECK(listed("vkSetPrivateDataEXT", "VK_EXT_private_data"));
    CHECK(listed("vkGetPhysicalDeviceSurfaceCapabilities2EXT", "VK_EXT_display_surface_counter"));
    CHECK(listed("vkDisplayPowerControlEXT", "VK_EXT_display_control"));
}

TEST_CASE("classification_is_consistent")
{
    std::set<std::string> in_list;
    for (const dvl::HandleCommand& c : dvl::handle_commands()) {
        in_list.emplace(c.name);
    }

    std::set<std::string> seen_na;
    for (const dvl::NotApplicableCommand& n : dvl::not_applicable_commands()) {
        const std::string name(n.name);
        INFO(name);
        CHECK(seen_na.insert(name).second);
        CHECK(in_list.count(name) == 1);
        CHECK(std::string(n.reason).size() > 10);
        CHECK(dvl::not_applicable(name) == n.reason);
        CHECK_FALSE(dvl::handled_command(name));
    }

    for (const std::string_view name : dvl::intercepted_commands()) {
        INFO(name);
        CHECK(dvl::handled_command(name));
        CHECK(dvl::not_applicable(name) == nullptr);
    }
}

TEST_CASE("commands_a_windows_driver_can_receive_our_handles_in_are_handled")
{
    for (const char* name :
         {"vkAcquireWinrtDisplayNV", "vkGetWinrtDisplayNV", "vkReleaseDisplayEXT",
          "vkGetPhysicalDeviceSurfacePresentModes2EXT", "vkGetDeviceGroupSurfacePresentModes2EXT",
          "vkSetPrivateData", "vkGetPrivateData", "vkSetPrivateDataEXT", "vkGetPrivateDataEXT", "vkCreateImage",
          "vkBindImageMemory2", "vkBindImageMemory2KHR", "vkGetDeviceImageMemoryRequirements",
          "vkGetDeviceImageMemoryRequirementsKHR", "vkGetDeviceImageSparseMemoryRequirements",
          "vkGetDeviceImageSparseMemoryRequirementsKHR", "vkGetDeviceImageSubresourceLayout",
          "vkGetDeviceImageSubresourceLayoutKHR", "vkSetLatencySleepModeNV", "vkLatencySleepNV",
          "vkSetLatencyMarkerNV", "vkGetLatencyTimingsNV", "vkSetLocalDimmingAMD", "vkGetRefreshCycleDurationGOOGLE",
          "vkGetPastPresentationTimingGOOGLE", "vkAcquireFullScreenExclusiveModeEXT",
          "vkReleaseFullScreenExclusiveModeEXT"}) {
        INFO(name);
        CHECK(dvl::handled_command(name));
        CHECK(dvl::not_applicable(name) == nullptr);
    }
    for (const char* name : {"vkAcquireXlibDisplayEXT", "vkGetRandROutputDisplayEXT", "vkAcquireDrmDisplayEXT",
                             "vkGetDrmDisplayEXT", "vkDebugReportMessageEXT", "vkSubmitDebugUtilsMessageEXT"}) {
        INFO(name);
        CHECK(dvl::not_applicable(name) != nullptr);
        CHECK_FALSE(dvl::handled_command(name));
    }
}

TEST_CASE("handled_commands_match_the_layers_proc_address_tables")
{
    dvl_test::ScopedEnv processes(L"MIRADOR_DIRECT_MODE_PROCESSES", L"dvl_tests.exe");
    dvl_test::ScopedEnv quiet(L"MIRADOR_DIRECT_MODE_LOG", L"off");
    dvl_test::LayerDll dll;
    REQUIRE(dll.loaded());
    auto n = dvl_test::negotiate(dll, 2);
    REQUIRE(n.result == VK_SUCCESS);
    const PFN_vkGetInstanceProcAddr gipa = n.iface.pfnGetInstanceProcAddr;

    for (const std::string_view name : dvl::intercepted_commands()) {
        INFO(name << " is listed as intercepted, but the layer's tables do not have it");
        CHECK(gipa(VK_NULL_HANDLE, std::string(name).c_str()) != nullptr);
    }
}

TEST_CASE("guard_names_the_enabled_extension_with_an_unclassified_command")
{
    const std::vector<dvl::HandleCommand> list = {
        {"vkFooKHR", "VK_KHR_foo"}, {"vkBarKHR", "VK_KHR_foo"}, {"vkBazEXT", "VK_EXT_baz"}, {"vkOkKHR", "VK_KHR_ok"}};
    const auto done = [](std::string_view name) { return name == "vkBarKHR" || name == "vkOkKHR"; };
    const char* enabled[] = {"VK_KHR_foo", "VK_KHR_ok", "VK_KHR_unrelated"};

    const std::vector<std::string> lines = dvl::coverage_errors(list, enabled, 3, done, "vkCreateDevice");
    REQUIRE(lines.size() == 1);
    CHECK(contains(lines[0], "vkCreateDevice"));
    CHECK(contains(lines[0], "VK_KHR_foo"));
    CHECK(contains(lines[0], "vkFooKHR"));
    CHECK_FALSE(contains(lines[0], "vkBarKHR"));
    CHECK_FALSE(contains(lines[0], "vkOkKHR"));
    CHECK_FALSE(contains(lines[0], "vkBazEXT"));
    CHECK_FALSE(contains(lines[0], "VK_EXT_baz"));
}

TEST_CASE("guard_gives_one_line_per_extension")
{
    const std::vector<dvl::HandleCommand> list = {{"vkAKHR", "VK_KHR_a"}, {"vkBKHR", "VK_KHR_a"}, {"vkCEXT", "VK_EXT_c"}};
    const auto none_done = [](std::string_view) { return false; };
    const char* enabled[] = {"VK_KHR_a", "VK_EXT_c"};
    const std::vector<std::string> lines = dvl::coverage_errors(list, enabled, 2, none_done, "vkCreateInstance");
    REQUIRE(lines.size() == 2);
    CHECK((contains(lines[0], "vkAKHR") && contains(lines[0], "vkBKHR") && !contains(lines[0], "vkCEXT")));
    CHECK((contains(lines[1], "vkCEXT") && !contains(lines[1], "vkAKHR")));
}

TEST_CASE("guard_is_quiet_when_nothing_is_enabled_or_everything_is_classified")
{
    const auto none_done = [](std::string_view) { return false; };
    CHECK(dvl::coverage_errors(dvl::handle_commands(), nullptr, 0, none_done, "vkCreateInstance").empty());

    std::set<std::string> extensions;
    for (const dvl::HandleCommand& c : dvl::handle_commands()) {
        extensions.emplace(c.extension);
    }
    std::vector<const char*> enabled;
    for (const std::string& e : extensions) {
        enabled.push_back(e.c_str());
    }
    const std::vector<std::string> lines = dvl::coverage_errors(dvl::handle_commands(), enabled.data(),
                                                                static_cast<uint32_t>(enabled.size()), classified,
                                                                "vkCreateDevice");
    std::ostringstream all;
    for (const std::string& line : lines) {
        all << "\n  " << line;
    }
    INFO(all.str());
    CHECK(lines.empty());
}

TEST_CASE("registry_snapshot_knows_its_extensions_and_its_version")
{
    for (const dvl::HandleCommand& c : dvl::handle_commands()) {
        if (c.extension.substr(0, 11) == "VK_VERSION_") {
            continue;
        }
        INFO(c.extension);
        CHECK(std::find(dvl::known_extensions().begin(), dvl::known_extensions().end(), c.extension) !=
              dvl::known_extensions().end());
    }
    for (const char* name : {"VK_KHR_surface", "VK_KHR_swapchain", "VK_KHR_display", "VK_KHR_present_id",
                             "VK_KHR_present_wait", "VK_EXT_display_control", "VK_EXT_display_surface_counter",
                             "VK_KHR_get_physical_device_properties2", "VK_KHR_external_memory_win32"}) {
        INFO(name);
        CHECK(std::find(dvl::known_extensions().begin(), dvl::known_extensions().end(), std::string_view(name)) !=
              dvl::known_extensions().end());
    }
    const std::string version(dvl::registry_version());
    INFO(version);
    CHECK(std::count(version.begin(), version.end(), '.') == 2);
    CHECK(version.find_first_not_of("0123456789.") == std::string::npos);
    CHECK(version.substr(0, 2) == "1.");
}

TEST_CASE("present_timing_and_its_neighbours_are_intercepted")
{
    const std::vector<std::pair<const char*, const char*>> commands = {
        {"vkSetSwapchainPresentTimingQueueSizeEXT", "VK_EXT_present_timing"},
        {"vkGetSwapchainTimingPropertiesEXT", "VK_EXT_present_timing"},
        {"vkGetSwapchainTimeDomainPropertiesEXT", "VK_EXT_present_timing"},
        {"vkGetPastPresentationTimingEXT", "VK_EXT_present_timing"},
        {"vkWaitForPresent2KHR", "VK_KHR_present_wait2"},
        {"vkReleaseSwapchainImagesKHR", "VK_KHR_swapchain_maintenance1"},
        {"vkGetCalibratedTimestampsKHR", "VK_KHR_calibrated_timestamps"},
        {"vkGetCalibratedTimestampsEXT", "VK_EXT_calibrated_timestamps"}};
    for (const auto& [name, extension] : commands) {
        INFO(name);
        CHECK(listed(name, extension));
        const std::vector<std::string_view>& intercepted = dvl::intercepted_commands();
        CHECK(std::find(intercepted.begin(), intercepted.end(), std::string_view(name)) != intercepted.end());
    }
    for (const char* name : {"vkCreateSurfaceOHOS", "vkCreateUbmSurfaceSEC"}) {
        INFO(name);
        CHECK(listed(name));
        CHECK(dvl::not_applicable(name) != nullptr);
    }
    for (const char* name : {"VK_EXT_present_timing", "VK_KHR_present_id2", "VK_KHR_present_wait2",
                             "VK_KHR_calibrated_timestamps", "VK_KHR_swapchain_maintenance1"}) {
        INFO(name);
        CHECK(std::find(dvl::known_extensions().begin(), dvl::known_extensions().end(), std::string_view(name)) !=
              dvl::known_extensions().end());
    }
}

TEST_CASE("guard_warns_about_an_enabled_extension_the_registry_does_not_know")
{
    const std::vector<std::string_view> known = {"VK_KHR_swapchain", "VK_KHR_display"};
    const char* enabled[] = {"VK_KHR_swapchain", "VK_EXT_newer", "VK_KHR_display", "VK_EXT_newer", "VK_KHR_newest"};
    const std::vector<std::string> lines = dvl::unknown_extension_warnings(known, "1.4.309", enabled, 5);
    REQUIRE(lines.size() == 2);
    CHECK(lines[0] == "VK_EXT_newer: unknown to the layer's Vulkan registry (1.4.309); its commands are not checked for "
                      "the layer's handles");
    CHECK(contains(lines[1], "VK_KHR_newest: unknown to the layer's Vulkan registry (1.4.309)"));
    for (const std::string& line : lines) {
        CHECK_FALSE(contains(line, "VK_KHR_swapchain"));
        CHECK_FALSE(contains(line, "VK_KHR_display"));
    }
}

TEST_CASE("guard_does_not_warn_about_known_extensions")
{
    const std::vector<std::string_view> known = {"VK_KHR_swapchain", "VK_KHR_display"};
    const char* enabled[] = {"VK_KHR_display", "VK_KHR_swapchain"};
    CHECK(dvl::unknown_extension_warnings(known, "1.4.309", enabled, 2).empty());
    CHECK(dvl::unknown_extension_warnings(known, "1.4.309", nullptr, 0).empty());

    std::vector<const char*> all;
    std::set<std::string> names;
    for (const dvl::HandleCommand& c : dvl::handle_commands()) {
        if (c.extension.substr(0, 11) != "VK_VERSION_") {
            names.emplace(c.extension);
        }
    }
    for (const std::string& n : names) {
        all.push_back(n.c_str());
    }
    CHECK(dvl::unknown_extension_warnings(dvl::known_extensions(), dvl::registry_version(), all.data(),
                                          static_cast<uint32_t>(all.size()))
              .empty());
    const char* odd[] = {"VK_NV_made_up_for_this_test"};
    CHECK(dvl::unknown_extension_warnings(dvl::known_extensions(), dvl::registry_version(), odd, 1).size() == 1);
}

TEST_CASE("the commands monado needs are intercepted")
{
    const std::vector<std::string_view>& intercepted = dvl::intercepted_commands();
    for (const char* name :
         {"vkDeviceWaitIdle", "vkAcquireWinrtDisplayNV", "vkGetWinrtDisplayNV", "vkReleaseDisplayEXT",
          "vkGetPhysicalDeviceSurfacePresentModes2EXT", "vkGetDeviceGroupSurfacePresentModes2EXT",
          "vkGetPhysicalDeviceSurfaceCapabilities2EXT", "vkCreateDisplayPlaneSurfaceKHR", "vkCreateSwapchainKHR",
          "vkDestroySwapchainKHR", "vkGetSwapchainImagesKHR", "vkGetSwapchainStatusKHR",
          "vkAcquireNextImageKHR", "vkAcquireNextImage2KHR", "vkQueuePresentKHR", "vkWaitForPresentKHR",
          "vkRegisterDisplayEventEXT", "vkDisplayPowerControlEXT", "vkGetSwapchainCounterEXT",
          "vkRegisterDeviceEventEXT", "vkWaitForFences", "vkGetFenceStatus", "vkResetFences", "vkDestroyFence",
          "vkSetHdrMetadataEXT", "vkCreateSharedSwapchainsKHR", "vkReleaseSwapchainImagesEXT",
          "vkGetPastPresentationTimingGOOGLE", "vkSetDebugUtilsObjectNameEXT", "vkSetPrivateData", "vkCreateImage",
          "vkBindImageMemory2KHR", "vkGetDeviceImageSubresourceLayoutKHR"}) {
        INFO(name);
        CHECK(std::find(intercepted.begin(), intercepted.end(), std::string_view(name)) != intercepted.end());
    }
}
