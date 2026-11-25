// Copyright 2026, Finn Zeruhn
// SPDX-License-Identifier: BSL-1.0

#include "layer/handled_commands.hpp"

#include <algorithm>
#include <initializer_list>
#include <iterator>
#include <mutex>
#include <set>

#include "layer/log.hpp"

namespace dvl {
namespace {

const std::initializer_list<std::string_view> kIntercepted = {
    "vkGetInstanceProcAddr", "vkGetDeviceProcAddr", "vkCreateInstance", "vkDestroyInstance", "vkCreateDevice",
    "vkDestroyDevice",
    "vkDeviceWaitIdle",
    "vkGetPhysicalDeviceDisplayPropertiesKHR", "vkGetPhysicalDeviceDisplayProperties2KHR",
    "vkGetPhysicalDeviceDisplayPlanePropertiesKHR", "vkGetPhysicalDeviceDisplayPlaneProperties2KHR",
    "vkGetDisplayPlaneSupportedDisplaysKHR", "vkGetDisplayModePropertiesKHR", "vkGetDisplayModeProperties2KHR",
    "vkCreateDisplayModeKHR", "vkGetDisplayPlaneCapabilitiesKHR", "vkGetDisplayPlaneCapabilities2KHR",
    "vkReleaseDisplayEXT",
    "vkAcquireWinrtDisplayNV", "vkGetWinrtDisplayNV",
    "vkCreateDisplayPlaneSurfaceKHR", "vkDestroySurfaceKHR", "vkGetPhysicalDeviceSurfaceSupportKHR",
    "vkGetPhysicalDeviceSurfaceCapabilitiesKHR", "vkGetPhysicalDeviceSurfaceFormatsKHR",
    "vkGetPhysicalDeviceSurfacePresentModesKHR", "vkGetPhysicalDeviceSurfaceCapabilities2KHR",
    "vkGetPhysicalDeviceSurfaceFormats2KHR", "vkGetPhysicalDeviceSurfaceCapabilities2EXT",
    "vkGetPhysicalDevicePresentRectanglesKHR", "vkGetDeviceGroupSurfacePresentModesKHR",
    "vkGetPhysicalDeviceSurfacePresentModes2EXT", "vkGetDeviceGroupSurfacePresentModes2EXT",
    "vkCreateSwapchainKHR", "vkDestroySwapchainKHR", "vkGetSwapchainImagesKHR", "vkGetSwapchainStatusKHR",
    "vkAcquireNextImageKHR", "vkAcquireNextImage2KHR", "vkQueuePresentKHR", "vkWaitForPresentKHR",
    "vkRegisterDisplayEventEXT", "vkDisplayPowerControlEXT", "vkGetSwapchainCounterEXT", "vkRegisterDeviceEventEXT",
    "vkWaitForFences", "vkGetFenceStatus", "vkResetFences", "vkDestroyFence",
    "vkSetHdrMetadataEXT", "vkCreateSharedSwapchainsKHR", "vkAcquireFullScreenExclusiveModeEXT",
    "vkReleaseFullScreenExclusiveModeEXT", "vkReleaseSwapchainImagesEXT", "vkGetRefreshCycleDurationGOOGLE",
    "vkGetPastPresentationTimingGOOGLE", "vkSetDebugUtilsObjectNameEXT", "vkSetDebugUtilsObjectTagEXT",
    "vkDebugMarkerSetObjectNameEXT", "vkDebugMarkerSetObjectTagEXT",
    "vkSetPrivateData", "vkGetPrivateData", "vkSetPrivateDataEXT", "vkGetPrivateDataEXT",
    "vkSetLatencySleepModeNV", "vkLatencySleepNV", "vkSetLatencyMarkerNV", "vkGetLatencyTimingsNV",
    "vkSetLocalDimmingAMD",
    "vkCreateImage", "vkBindImageMemory2", "vkBindImageMemory2KHR", "vkGetDeviceImageMemoryRequirements",
    "vkGetDeviceImageMemoryRequirementsKHR", "vkGetDeviceImageSparseMemoryRequirements",
    "vkGetDeviceImageSparseMemoryRequirementsKHR", "vkGetDeviceImageSubresourceLayout",
    "vkGetDeviceImageSubresourceLayoutKHR",
    "vkWaitForPresent2KHR", "vkReleaseSwapchainImagesKHR",
    "vkSetSwapchainPresentTimingQueueSizeEXT", "vkGetSwapchainTimingPropertiesEXT",
    "vkGetSwapchainTimeDomainPropertiesEXT", "vkGetPastPresentationTimingEXT",
    "vkGetCalibratedTimestampsKHR", "vkGetCalibratedTimestampsEXT",
};

const std::initializer_list<NotApplicableCommand> kNotApplicable = {
    {"vkCreateAndroidSurfaceKHR", "Android window surface: Android only, and it returns the driver's surface, not the layer's"},
    {"vkCreateDirectFBSurfaceEXT", "DirectFB surface: Linux only, and it returns the driver's surface, not the layer's"},
    {"vkCreateHeadlessSurfaceEXT", "headless surface made by the loader, tied to no display: never the layer's"},
    {"vkCreateIOSSurfaceMVK", "iOS surface: Apple only, and it returns the driver's surface, not the layer's"},
    {"vkCreateImagePipeSurfaceFUCHSIA", "Fuchsia surface: Fuchsia only, and it returns the driver's surface, not the layer's"},
    {"vkCreateMacOSSurfaceMVK", "macOS surface: Apple only, and it returns the driver's surface, not the layer's"},
    {"vkCreateMetalSurfaceEXT", "Metal surface: Apple only, and it returns the driver's surface, not the layer's"},
    {"vkCreateScreenSurfaceQNX", "QNX Screen surface: QNX only, and it returns the driver's surface, not the layer's"},
    {"vkCreateStreamDescriptorSurfaceGGP", "Google Stadia (GGP) surface: GGP only, and it returns the driver's surface, not the layer's"},
    {"vkCreateSurfaceOHOS", "OpenHarmony surface: OpenHarmony only, and it returns the driver's surface, not the layer's"},
    {"vkCreateUbmSurfaceSEC", "Samsung UBM surface: Linux only, and it returns the driver's surface, not the layer's"},
    {"vkCreateViSurfaceNN", "Nintendo VI surface: Nintendo Switch only, and it returns the driver's surface, not the layer's"},
    {"vkCreateWaylandSurfaceKHR", "Wayland surface: Linux only, and it returns the driver's surface, not the layer's"},
    {"vkCreateWin32SurfaceKHR", "window surface for an application's own window (Monado's peek window): returns the loader's surface, never the layer's; it goes down"},
    {"vkCreateXcbSurfaceKHR", "XCB surface: Linux only, and it returns the driver's surface, not the layer's"},
    {"vkCreateXlibSurfaceKHR", "Xlib surface: Linux only, and it returns the driver's surface, not the layer's"},

    {"vkAcquireXlibDisplayEXT", "acquires a display through X11 RandR: Linux only, and the layer's display is not an X11 output"},
    {"vkGetRandROutputDisplayEXT", "finds the display of an X11 RandR output: Linux only, so a driver display, never the layer's"},
    {"vkAcquireDrmDisplayEXT", "acquires a display through a DRM file descriptor: Linux only, and the layer's display is not a DRM connector"},
    {"vkGetDrmDisplayEXT", "finds the display of a DRM connector: Linux only, so a driver display, never the layer's"},

    {"vkSetBufferCollectionImageConstraintsFUCHSIA", "Fuchsia sysmem buffer collections (VK_FUCHSIA_buffer_collection): Fuchsia only"},

    {"vkDebugReportMessageEXT", "object handles are only forwarded as values to message callbacks, never dereferenced by the loader or driver"},
    {"vkSubmitDebugUtilsMessageEXT", "object handles are only forwarded as values to message callbacks, never dereferenced by the loader or driver"},
};

const HandleCommand kGenerated[] = {
#define DVL_HANDLE_COMMAND(name, extension) {name, extension},
#define DVL_KNOWN_EXTENSION(name)
#define DVL_REGISTRY_VERSION(version)
#include "handle_commands.inc"
#undef DVL_HANDLE_COMMAND
#undef DVL_KNOWN_EXTENSION
#undef DVL_REGISTRY_VERSION
};

const std::string_view kKnownExtensions[] = {
#define DVL_HANDLE_COMMAND(name, extension)
#define DVL_KNOWN_EXTENSION(name) name,
#define DVL_REGISTRY_VERSION(version)
#include "handle_commands.inc"
#undef DVL_HANDLE_COMMAND
#undef DVL_KNOWN_EXTENSION
#undef DVL_REGISTRY_VERSION
};

constexpr std::string_view kRegistryVersion =
#define DVL_HANDLE_COMMAND(name, extension)
#define DVL_KNOWN_EXTENSION(name)
#define DVL_REGISTRY_VERSION(version) version
#include "handle_commands.inc"
#undef DVL_HANDLE_COMMAND
#undef DVL_KNOWN_EXTENSION
#undef DVL_REGISTRY_VERSION
    ;

bool classified_command(std::string_view name)
{
    return handled_command(name) || not_applicable(name) != nullptr;
}

}

const std::vector<HandleCommand>& handle_commands()
{
    static const std::vector<HandleCommand>* const list =
        new std::vector<HandleCommand>(std::begin(kGenerated), std::end(kGenerated));
    return *list;
}

const std::vector<std::string_view>& known_extensions()
{
    static const std::vector<std::string_view>* const list =
        new std::vector<std::string_view>(std::begin(kKnownExtensions), std::end(kKnownExtensions));
    return *list;
}

std::string_view registry_version()
{
    return kRegistryVersion;
}

const std::vector<std::string_view>& intercepted_commands()
{
    static const std::vector<std::string_view>* const list =
        new std::vector<std::string_view>(kIntercepted.begin(), kIntercepted.end());
    return *list;
}

bool handled_command(std::string_view name)
{
    for (const std::string_view& n : kIntercepted) {
        if (n == name) {
            return true;
        }
    }
    return false;
}

const char* not_applicable(std::string_view name)
{
    for (const NotApplicableCommand& n : kNotApplicable) {
        if (n.name == name) {
            return n.reason;
        }
    }
    return nullptr;
}

const std::vector<NotApplicableCommand>& not_applicable_commands()
{
    static const std::vector<NotApplicableCommand>* const list =
        new std::vector<NotApplicableCommand>(kNotApplicable.begin(), kNotApplicable.end());
    return *list;
}

std::vector<std::string> coverage_errors(const std::vector<HandleCommand>& list, const char* const* extensions,
                                         uint32_t count, const std::function<bool(std::string_view)>& classified,
                                         std::string_view where)
{
    std::vector<std::string> lines;
    for (uint32_t i = 0; i < count; i++) {
        const std::string_view extension = extensions[i];
        std::string commands;
        for (const HandleCommand& command : list) {
            if (command.extension == extension && !classified(command.name)) {
                commands += (commands.empty() ? "" : ", ");
                commands += command.name;
            }
        }
        if (!commands.empty()) {
            lines.push_back(std::string(where) + ": extension " + std::string(extension) +
                            " is enabled, but the layer neither handles nor lists as not applicable its command(s) " +
                            commands + ", which can be handed the layer's own display, mode, surface or swapchain "
                            "(layer/handled_commands.cpp)");
        }
    }
    return lines;
}

std::vector<std::string> unknown_extension_warnings(const std::vector<std::string_view>& known,
                                                    std::string_view version, const char* const* extensions,
                                                    uint32_t count)
{
    std::vector<std::string> lines;
    for (uint32_t i = 0; i < count; i++) {
        const std::string_view extension = extensions[i];
        if (std::find(known.begin(), known.end(), extension) != known.end()) {
            continue;
        }
        std::string line = std::string(extension) + ": unknown to the layer's Vulkan registry (" + std::string(version) +
                           "); its commands are not checked for the layer's handles";
        if (std::find(lines.begin(), lines.end(), line) == lines.end()) {
            lines.push_back(std::move(line));
        }
    }
    return lines;
}

void check_command_coverage(const char* const* extensions, uint32_t count, const char* where) noexcept
{
    try {
        static std::mutex* const mutex = new std::mutex;
        static std::set<std::string>* const written = new std::set<std::string>;
        const auto write_once = [](Level level, const std::string& line) {
            bool first;
            {
                std::lock_guard<std::mutex> lock(*mutex);
                first = written->insert(line).second;
            }
            if (first) {
                log(level, "%s", line.c_str());
            }
        };
        for (const std::string& line :
             coverage_errors(handle_commands(), extensions, count, classified_command, where)) {
            write_once(Level::Error, line);
        }
        for (const std::string& line :
             unknown_extension_warnings(known_extensions(), registry_version(), extensions, count)) {
            write_once(Level::Warn, line);
        }
    } catch (...) {
    }
}

}
