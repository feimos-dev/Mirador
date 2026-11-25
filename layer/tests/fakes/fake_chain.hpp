// Copyright 2026, Finn Zeruhn
// SPDX-License-Identifier: BSL-1.0

#pragma once

#include <windows.h>

#include <algorithm>
#include <chrono>
#include <cstdint>
#include <cstdio>
#include <cstring>
#include <deque>
#include <functional>
#include <iterator>
#include <map>
#include <memory>
#include <mutex>
#include <set>
#include <string>
#include <thread>
#include <vector>

#include <vulkan/vulkan.h>
#include <vulkan/vk_layer.h>

#include "core/display_session.hpp"
#include "fakes/fake_api.hpp"
#include "fakes/fake_clock.hpp"
#include "fakes/layer_dll.hpp"
#include "layer/log.hpp"

namespace dvl_test {

struct Dispatchable {
    void* table;
};

inline constexpr size_t kGpuSlots = 3;
inline constexpr size_t kDeviceSlots = 4;

inline int g_instance_table;
inline int g_device_tables[kDeviceSlots];
inline int g_queue_table;
inline Dispatchable g_instance{&g_instance_table};
inline Dispatchable g_gpus[kGpuSlots] = {{&g_instance_table}, {&g_instance_table}, {&g_instance_table}};
inline Dispatchable g_devices[kDeviceSlots] = {
    {&g_device_tables[0]}, {&g_device_tables[1]}, {&g_device_tables[2]}, {&g_device_tables[3]}};

inline VkDisplayKHR driver_display(size_t gpu, uint32_t i)
{
    return reinterpret_cast<VkDisplayKHR>(static_cast<uintptr_t>(0xD1500000u + gpu * 16 + i));
}
inline VkDisplayModeKHR driver_mode(size_t gpu, uint32_t i)
{
    return reinterpret_cast<VkDisplayModeKHR>(static_cast<uintptr_t>(0xD1600000u + gpu * 16 + i));
}
inline const VkSurfaceKHR kPeekSurface = reinterpret_cast<VkSurfaceKHR>(static_cast<uintptr_t>(0x5EE0F000u));

template <class T>
uint64_t handle_value(T handle)
{
    return static_cast<uint64_t>(reinterpret_cast<uintptr_t>(handle));
}

struct FakeGpu {
    uint32_t vendor = 0x10de;
    LUID luid{};
    bool luid_valid = true;
    std::vector<VkQueueFamilyProperties> families;
    std::vector<std::string> extensions;
    uint32_t displays = 0;
    uint32_t planes = 0;
};

inline VkQueueFamilyProperties queue_family(VkQueueFlags flags, uint32_t count)
{
    VkQueueFamilyProperties f{};
    f.queueFlags = flags;
    f.queueCount = count;
    return f;
}

inline std::vector<VkQueueFamilyProperties> nvidia_families()
{
    return {queue_family(VK_QUEUE_GRAPHICS_BIT | VK_QUEUE_COMPUTE_BIT | VK_QUEUE_TRANSFER_BIT, 16),
            queue_family(VK_QUEUE_TRANSFER_BIT, 2),
            queue_family(VK_QUEUE_COMPUTE_BIT | VK_QUEUE_TRANSFER_BIT, 8)};
}

inline std::vector<std::string> nvidia_extensions()
{
    return {"VK_KHR_swapchain",
            "VK_KHR_present_id",
            "VK_KHR_present_wait",
            "VK_KHR_external_memory",
            "VK_KHR_external_memory_win32",
            "VK_KHR_dedicated_allocation",
            "VK_KHR_get_memory_requirements2",
            "VK_KHR_image_format_list"};
}

inline LUID session_luid()
{
    return LUID{0x1234, 0x56};
}

inline std::vector<FakeGpu> default_gpus()
{
    FakeGpu ours;
    ours.luid = session_luid();
    ours.families = nvidia_families();
    ours.extensions = nvidia_extensions();
    FakeGpu other = ours;
    other.luid = LUID{0x7777, 0};
    FakeGpu intel;
    intel.vendor = 0x8086;
    intel.luid = LUID{0x4242, 0};
    intel.families = {queue_family(VK_QUEUE_GRAPHICS_BIT | VK_QUEUE_COMPUTE_BIT | VK_QUEUE_TRANSFER_BIT, 1)};
    intel.extensions = {"VK_KHR_swapchain"};
    return {ours, other, intel};
}

inline VkPhysicalDeviceMemoryProperties fake_memory_properties()
{
    VkPhysicalDeviceMemoryProperties m{};
    m.memoryHeapCount = 2;
    m.memoryHeaps[0] = {16ull << 30, VK_MEMORY_HEAP_DEVICE_LOCAL_BIT};
    m.memoryHeaps[1] = {32ull << 30, 0};
    m.memoryTypeCount = 3;
    m.memoryTypes[0] = {VK_MEMORY_PROPERTY_HOST_VISIBLE_BIT | VK_MEMORY_PROPERTY_HOST_COHERENT_BIT, 1};
    m.memoryTypes[1] = {VK_MEMORY_PROPERTY_DEVICE_LOCAL_BIT, 0};
    m.memoryTypes[2] = {VK_MEMORY_PROPERTY_DEVICE_LOCAL_BIT | VK_MEMORY_PROPERTY_HOST_VISIBLE_BIT, 0};
    return m;
}

inline VkImage driver_image(size_t n)
{
    return reinterpret_cast<VkImage>(static_cast<uintptr_t>(0x1A000000u + n));
}
inline VkDeviceMemory driver_memory(size_t n)
{
    return reinterpret_cast<VkDeviceMemory>(static_cast<uintptr_t>(0xE0000000u + n));
}

struct FakeDriver {
    std::vector<FakeGpu> gpus;
    VkResult create_device_result = VK_SUCCESS;
    VkResult set_loader_data_result = VK_SUCCESS;
    std::function<void()> on_device_wait_idle;

    std::map<VkFormat, VkExternalMemoryFeatureFlags> import_features;
    VkPhysicalDeviceMemoryProperties memory = fake_memory_properties();
    uint32_t image_memory_bits = 0b111;
    uint32_t handle_memory_bits = 0b100;

    void fail_call(const std::string& name, size_t nth, VkResult result)
    {
        std::lock_guard<std::mutex> lock(mutex);
        failures_[name][nth] = result;
    }
    void fail_next_call(const std::string& name, VkResult result)
    {
        std::lock_guard<std::mutex> lock(mutex);
        failures_[name][seen_[name] + 1] = result;
    }
    VkResult scripted(const std::string& name)
    {
        std::lock_guard<std::mutex> lock(mutex);
        const size_t nth = ++seen_[name];
        const auto per_name = failures_.find(name);
        if (per_name == failures_.end()) {
            return VK_SUCCESS;
        }
        const auto it = per_name->second.find(nth);
        return it == per_name->second.end() ? VK_SUCCESS : it->second;
    }

    struct FormatQuery {
        VkFormat format;
        VkImageType type;
        VkImageTiling tiling;
        VkImageUsageFlags usage;
        VkImageCreateFlags flags;
        bool external = false;
        VkExternalMemoryHandleTypeFlagBits handle_type{};
        bool format_list = false;
        std::vector<VkFormat> view_formats;
    };
    struct ImageMade {
        VkImage image;
        VkDevice device;
        VkImageCreateInfo info;
        bool external = false;
        VkExternalMemoryHandleTypeFlags handle_types = 0;
        bool format_list = false;
        std::vector<VkFormat> view_formats;
    };
    struct HandleQuery {
        VkExternalMemoryHandleTypeFlagBits type;
        HANDLE handle;
    };
    struct Allocation {
        VkDeviceMemory memory;
        VkDeviceSize size;
        uint32_t type;
        bool imported = false;
        VkExternalMemoryHandleTypeFlagBits handle_type{};
        HANDLE handle = nullptr;
        LPCWSTR name = nullptr;
        bool dedicated = false;
        VkImage dedicated_image = VK_NULL_HANDLE;
        VkBuffer dedicated_buffer = VK_NULL_HANDLE;
    };
    struct Bind {
        VkImage image;
        VkDeviceMemory memory;
        VkDeviceSize offset;
    };
    std::vector<FormatQuery> format_queries;
    std::vector<ImageMade> images_made;
    std::vector<HandleQuery> handle_queries;
    std::vector<Allocation> allocations;
    std::vector<Bind> binds;
    std::set<uint64_t> live_images;
    std::set<uint64_t> live_memory;
    size_t images_destroyed = 0;
    size_t memory_freed = 0;

    std::vector<std::string> import_calls() const
    {
        static const std::set<std::string> names = {
            "vkGetPhysicalDeviceImageFormatProperties2KHR", "vkCreateImage", "vkGetImageMemoryRequirements",
            "vkGetMemoryWin32HandlePropertiesKHR", "vkAllocateMemory", "vkBindImageMemory", "vkDestroyImage",
            "vkFreeMemory"};
        std::lock_guard<std::mutex> lock(mutex);
        std::vector<std::string> out;
        for (const Call& c : calls_) {
            if (names.count(c.name) != 0) {
                out.push_back(c.name);
            }
        }
        return out;
    }

    struct Call {
        std::string name;
        uint64_t handle = 0;
        uint32_t index = 0;
    };

    std::vector<VkDeviceQueueCreateInfo> queues;
    std::vector<std::vector<float>> priorities;
    std::vector<std::string> device_extensions;
    std::vector<VkLayerFunction> device_pnext_loader_structs;
    size_t devices_made = 0;

    std::deque<Dispatchable> queue_objects;
    std::vector<void*> loader_data_set;

    struct FakeFence {
        bool signalled = false;
        bool held = false;
    };
    struct Submit {
        VkQueue queue;
        uint32_t batches;
        uint32_t waits;
        uint32_t signals;
        std::vector<VkPipelineStageFlags> stages;
        VkFence fence;
    };
    std::map<uint64_t, FakeFence> fences;
    size_t fences_made = 0;
    bool hold_fences = false;
    std::vector<Submit> submits;
    size_t bad_fence_uses = 0;

    std::vector<Submit> submits_on(VkQueue queue) const
    {
        std::lock_guard<std::mutex> lock(mutex);
        std::vector<Submit> out;
        for (const Submit& s : submits) {
            if (s.queue == queue) {
                out.push_back(s);
            }
        }
        return out;
    }
    void signal_held()
    {
        std::lock_guard<std::mutex> lock(mutex);
        for (auto& entry : fences) {
            if (entry.second.held) {
                entry.second.held = false;
                entry.second.signalled = true;
            }
        }
    }
    size_t live_fences() const
    {
        std::lock_guard<std::mutex> lock(mutex);
        return fences.size();
    }
    size_t count_on(const std::string& name, uint64_t handle) const
    {
        std::lock_guard<std::mutex> lock(mutex);
        size_t n = 0;
        for (const Call& c : calls_) {
            n += c.name == name && c.handle == handle ? 1 : 0;
        }
        return n;
    }
    std::vector<std::string> names(const std::set<std::string>& only = {}) const
    {
        std::lock_guard<std::mutex> lock(mutex);
        std::vector<std::string> out;
        for (const Call& c : calls_) {
            if (only.empty() || only.count(c.name) != 0) {
                out.push_back(c.name);
            }
        }
        return out;
    }
    size_t swapchains_made = 0;

    struct Calibration {
        VkTimeDomainKHR domain;
        uint64_t swapchain;
        bool chained;
    };
    std::vector<std::vector<Calibration>> calibrations;

    bool offer_signal_semaphore_khr = true;

    void reset(std::vector<FakeGpu> new_gpus)
    {
        std::lock_guard<std::mutex> lock(mutex);
        gpus = std::move(new_gpus);
        create_device_result = VK_SUCCESS;
        set_loader_data_result = VK_SUCCESS;
        on_device_wait_idle = nullptr;
        calls_.clear();
        queues.clear();
        priorities.clear();
        device_extensions.clear();
        device_pnext_loader_structs.clear();
        devices_made = 0;
        queue_objects.clear();
        loader_data_set.clear();
        import_features.clear();
        memory = fake_memory_properties();
        image_memory_bits = 0b111;
        handle_memory_bits = 0b100;
        failures_.clear();
        seen_.clear();
        format_queries.clear();
        images_made.clear();
        handle_queries.clear();
        allocations.clear();
        binds.clear();
        live_images.clear();
        live_memory.clear();
        images_destroyed = 0;
        memory_freed = 0;
        images_counter_ = 0;
        memory_counter_ = 0;
        fences.clear();
        fences_made = 0;
        hold_fences = false;
        submits.clear();
        bad_fence_uses = 0;
        swapchains_made = 0;
        calibrations.clear();
        offer_signal_semaphore_khr = true;
    }

    VkImage next_image() { return driver_image(images_counter_++); }
    VkDeviceMemory next_memory() { return driver_memory(memory_counter_++); }

    void record(const char* name, uint64_t handle = 0, uint32_t index = 0)
    {
        std::lock_guard<std::mutex> lock(mutex);
        calls_.push_back(Call{name, handle, index});
    }

    std::vector<Call> calls() const
    {
        std::lock_guard<std::mutex> lock(mutex);
        return calls_;
    }

    void clear_calls()
    {
        std::lock_guard<std::mutex> lock(mutex);
        calls_.clear();
    }

    size_t count(const std::string& name) const
    {
        std::lock_guard<std::mutex> lock(mutex);
        size_t n = 0;
        for (const Call& c : calls_) {
            n += c.name == name ? 1 : 0;
        }
        return n;
    }

    Call last(const std::string& name) const
    {
        std::lock_guard<std::mutex> lock(mutex);
        for (auto it = calls_.rbegin(); it != calls_.rend(); ++it) {
            if (it->name == name) {
                return *it;
            }
        }
        return Call{};
    }

    int gpu_index(VkPhysicalDevice pd) const
    {
        for (size_t i = 0; i < kGpuSlots; i++) {
            if (reinterpret_cast<VkPhysicalDevice>(&g_gpus[i]) == pd) {
                return i < gpus.size() ? static_cast<int>(i) : -1;
            }
        }
        return -1;
    }

    mutable std::mutex mutex;

private:
    std::vector<Call> calls_;
    std::map<std::string, std::map<size_t, VkResult>> failures_;
    std::map<std::string, size_t> seen_;
    size_t images_counter_ = 0;
    size_t memory_counter_ = 0;
};

inline FakeDriver g_driver;

namespace fake {

template <class T>
VkResult write_list(uint32_t* count, T* out, const std::vector<T>& items)
{
    if (out == nullptr) {
        *count = static_cast<uint32_t>(items.size());
        return VK_SUCCESS;
    }
    const uint32_t n = *count < items.size() ? *count : static_cast<uint32_t>(items.size());
    for (uint32_t i = 0; i < n; i++) {
        out[i] = items[i];
    }
    *count = n;
    return n < items.size() ? VK_INCOMPLETE : VK_SUCCESS;
}

template <class T, class Set>
VkResult fill_list(uint32_t* count, T* out, uint32_t total, Set&& set)
{
    if (out == nullptr) {
        *count = total;
        return VK_SUCCESS;
    }
    const uint32_t n = *count < total ? *count : total;
    for (uint32_t i = 0; i < n; i++) {
        set(i, out[i]);
    }
    *count = n;
    return n < total ? VK_INCOMPLETE : VK_SUCCESS;
}

inline const FakeGpu& gpu(VkPhysicalDevice pd)
{
    static const FakeGpu none{};
    const int i = g_driver.gpu_index(pd);
    return i < 0 ? none : g_driver.gpus[static_cast<size_t>(i)];
}

inline size_t gpu_slot(VkPhysicalDevice pd)
{
    const int i = g_driver.gpu_index(pd);
    return i < 0 ? 0 : static_cast<size_t>(i);
}

inline VkSurfaceCapabilitiesKHR driver_caps()
{
    VkSurfaceCapabilitiesKHR c{};
    c.minImageCount = 2;
    c.maxImageCount = 8;
    c.currentExtent = {640, 480};
    c.minImageExtent = {1, 1};
    c.maxImageExtent = {4096, 4096};
    c.maxImageArrayLayers = 1;
    c.supportedTransforms = VK_SURFACE_TRANSFORM_IDENTITY_BIT_KHR;
    c.currentTransform = VK_SURFACE_TRANSFORM_IDENTITY_BIT_KHR;
    c.supportedCompositeAlpha = VK_COMPOSITE_ALPHA_OPAQUE_BIT_KHR;
    c.supportedUsageFlags = VK_IMAGE_USAGE_COLOR_ATTACHMENT_BIT;
    return c;
}

inline VkResult VKAPI_CALL CreateInstance(const VkInstanceCreateInfo*, const VkAllocationCallbacks*, VkInstance* out)
{
    g_driver.record("vkCreateInstance");
    *out = reinterpret_cast<VkInstance>(&g_instance);
    return VK_SUCCESS;
}

inline void VKAPI_CALL DestroyInstance(VkInstance instance, const VkAllocationCallbacks*)
{
    g_driver.record("vkDestroyInstance", handle_value(instance));
}

inline void VKAPI_CALL GetPhysicalDeviceProperties(VkPhysicalDevice pd, VkPhysicalDeviceProperties* out)
{
    g_driver.record("vkGetPhysicalDeviceProperties");
    *out = VkPhysicalDeviceProperties{};
    out->vendorID = gpu(pd).vendor;
    std::snprintf(out->deviceName, sizeof(out->deviceName), "fake GPU");
}

inline void VKAPI_CALL GetPhysicalDeviceProperties2KHR(VkPhysicalDevice pd, VkPhysicalDeviceProperties2* out)
{
    g_driver.record("vkGetPhysicalDeviceProperties2KHR");
    out->properties = VkPhysicalDeviceProperties{};
    out->properties.vendorID = gpu(pd).vendor;
    for (auto* s = static_cast<VkBaseOutStructure*>(out->pNext); s != nullptr; s = s->pNext) {
        if (s->sType == VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_ID_PROPERTIES) {
            auto* id = reinterpret_cast<VkPhysicalDeviceIDProperties*>(s);
            std::memcpy(id->deviceLUID, &gpu(pd).luid, VK_LUID_SIZE);
            id->deviceLUIDValid = gpu(pd).luid_valid ? VK_TRUE : VK_FALSE;
        }
    }
}

inline void VKAPI_CALL GetPhysicalDeviceQueueFamilyProperties(VkPhysicalDevice pd, uint32_t* count,
                                                              VkQueueFamilyProperties* out)
{
    g_driver.record("vkGetPhysicalDeviceQueueFamilyProperties");
    write_list(count, out, gpu(pd).families);
}

inline VkResult VKAPI_CALL EnumerateDeviceExtensionProperties(VkPhysicalDevice pd, const char* layer, uint32_t* count,
                                                              VkExtensionProperties* out)
{
    g_driver.record("vkEnumerateDeviceExtensionProperties");
    std::vector<VkExtensionProperties> list;
    if (layer == nullptr) {
        for (const std::string& name : gpu(pd).extensions) {
            VkExtensionProperties p{};
            std::snprintf(p.extensionName, sizeof(p.extensionName), "%s", name.c_str());
            p.specVersion = 1;
            list.push_back(p);
        }
    }
    return write_list(count, out, list);
}

inline VkResult VKAPI_CALL CreateDevice(VkPhysicalDevice, const VkDeviceCreateInfo* ci, const VkAllocationCallbacks*,
                                        VkDevice* out)
{
    g_driver.record("vkCreateDevice");
    g_driver.queues.assign(ci->pQueueCreateInfos, ci->pQueueCreateInfos + ci->queueCreateInfoCount);
    g_driver.priorities.clear();
    for (const VkDeviceQueueCreateInfo& q : g_driver.queues) {
        g_driver.priorities.emplace_back(q.pQueuePriorities, q.pQueuePriorities + q.queueCount);
    }
    g_driver.device_extensions.clear();
    for (uint32_t i = 0; i < ci->enabledExtensionCount; i++) {
        g_driver.device_extensions.emplace_back(ci->ppEnabledExtensionNames[i]);
    }
    g_driver.device_pnext_loader_structs.clear();
    for (auto* s = static_cast<const VkBaseInStructure*>(ci->pNext); s != nullptr; s = s->pNext) {
        if (s->sType == VK_STRUCTURE_TYPE_LOADER_DEVICE_CREATE_INFO) {
            g_driver.device_pnext_loader_structs.push_back(reinterpret_cast<const VkLayerDeviceCreateInfo*>(s)->function);
        }
    }
    if (g_driver.create_device_result != VK_SUCCESS) {
        return g_driver.create_device_result;
    }
    *out = reinterpret_cast<VkDevice>(&g_devices[g_driver.devices_made++ % kDeviceSlots]);
    return VK_SUCCESS;
}

inline VkResult VKAPI_CALL SetDeviceLoaderData(VkDevice device, void* object)
{
    g_driver.record("vkSetDeviceLoaderData");
    g_driver.loader_data_set.push_back(object);
    if (g_driver.set_loader_data_result != VK_SUCCESS) {
        return g_driver.set_loader_data_result;
    }
    *static_cast<void**>(object) = *reinterpret_cast<void**>(device);
    return VK_SUCCESS;
}

inline VkResult VKAPI_CALL GetPhysicalDeviceDisplayPropertiesKHR(VkPhysicalDevice pd, uint32_t* count,
                                                                 VkDisplayPropertiesKHR* out)
{
    g_driver.record("vkGetPhysicalDeviceDisplayPropertiesKHR");
    return fill_list(count, out, gpu(pd).displays, [&](uint32_t i, VkDisplayPropertiesKHR& p) {
        p = VkDisplayPropertiesKHR{};
        p.display = driver_display(gpu_slot(pd), i);
        p.displayName = "driver display";
        p.physicalResolution = {1920, 1080};
    });
}

inline VkResult VKAPI_CALL GetPhysicalDeviceDisplayProperties2KHR(VkPhysicalDevice pd, uint32_t* count,
                                                                  VkDisplayProperties2KHR* out)
{
    g_driver.record("vkGetPhysicalDeviceDisplayProperties2KHR");
    return fill_list(count, out, gpu(pd).displays, [&](uint32_t i, VkDisplayProperties2KHR& p) {
        p.displayProperties = VkDisplayPropertiesKHR{};
        p.displayProperties.display = driver_display(gpu_slot(pd), i);
        p.displayProperties.displayName = "driver display";
    });
}

inline VkResult VKAPI_CALL GetPhysicalDeviceDisplayPlanePropertiesKHR(VkPhysicalDevice pd, uint32_t* count,
                                                                      VkDisplayPlanePropertiesKHR* out)
{
    g_driver.record("vkGetPhysicalDeviceDisplayPlanePropertiesKHR");
    return fill_list(count, out, gpu(pd).planes, [&](uint32_t i, VkDisplayPlanePropertiesKHR& p) {
        p.currentDisplay = gpu(pd).displays > 0 ? driver_display(gpu_slot(pd), 0) : VK_NULL_HANDLE;
        p.currentStackIndex = i;
    });
}

inline VkResult VKAPI_CALL GetPhysicalDeviceDisplayPlaneProperties2KHR(VkPhysicalDevice pd, uint32_t* count,
                                                                       VkDisplayPlaneProperties2KHR* out)
{
    g_driver.record("vkGetPhysicalDeviceDisplayPlaneProperties2KHR");
    return fill_list(count, out, gpu(pd).planes, [&](uint32_t i, VkDisplayPlaneProperties2KHR& p) {
        p.displayPlaneProperties.currentDisplay =
            gpu(pd).displays > 0 ? driver_display(gpu_slot(pd), 0) : VK_NULL_HANDLE;
        p.displayPlaneProperties.currentStackIndex = i;
    });
}

inline VkResult VKAPI_CALL GetDisplayPlaneSupportedDisplaysKHR(VkPhysicalDevice pd, uint32_t plane, uint32_t* count,
                                                               VkDisplayKHR* out)
{
    g_driver.record("vkGetDisplayPlaneSupportedDisplaysKHR", 0, plane);
    return fill_list(count, out, gpu(pd).displays,
                     [&](uint32_t i, VkDisplayKHR& d) { d = driver_display(gpu_slot(pd), i); });
}

inline VkResult VKAPI_CALL GetDisplayModePropertiesKHR(VkPhysicalDevice pd, VkDisplayKHR display, uint32_t* count,
                                                       VkDisplayModePropertiesKHR* out)
{
    g_driver.record("vkGetDisplayModePropertiesKHR", handle_value(display));
    return fill_list(count, out, 1, [&](uint32_t, VkDisplayModePropertiesKHR& m) {
        m.displayMode = driver_mode(gpu_slot(pd), 0);
        m.parameters = {{1920, 1080}, 60000};
    });
}

inline VkResult VKAPI_CALL GetDisplayModeProperties2KHR(VkPhysicalDevice pd, VkDisplayKHR display, uint32_t* count,
                                                        VkDisplayModeProperties2KHR* out)
{
    g_driver.record("vkGetDisplayModeProperties2KHR", handle_value(display));
    return fill_list(count, out, 1, [&](uint32_t, VkDisplayModeProperties2KHR& m) {
        m.displayModeProperties.displayMode = driver_mode(gpu_slot(pd), 0);
        m.displayModeProperties.parameters = {{1920, 1080}, 60000};
    });
}

inline VkResult VKAPI_CALL CreateDisplayModeKHR(VkPhysicalDevice pd, VkDisplayKHR display,
                                                const VkDisplayModeCreateInfoKHR*, const VkAllocationCallbacks*,
                                                VkDisplayModeKHR* out)
{
    g_driver.record("vkCreateDisplayModeKHR", handle_value(display));
    *out = driver_mode(gpu_slot(pd), 1);
    return VK_SUCCESS;
}

inline VkDisplayPlaneCapabilitiesKHR driver_plane_caps()
{
    VkDisplayPlaneCapabilitiesKHR c{};
    c.supportedAlpha = VK_DISPLAY_PLANE_ALPHA_PER_PIXEL_BIT_KHR;
    c.maxSrcExtent = {1920, 1080};
    c.maxDstExtent = {1920, 1080};
    return c;
}

inline VkResult VKAPI_CALL GetDisplayPlaneCapabilitiesKHR(VkPhysicalDevice, VkDisplayModeKHR mode, uint32_t plane,
                                                          VkDisplayPlaneCapabilitiesKHR* out)
{
    g_driver.record("vkGetDisplayPlaneCapabilitiesKHR", handle_value(mode), plane);
    *out = driver_plane_caps();
    return VK_SUCCESS;
}

inline VkResult VKAPI_CALL GetDisplayPlaneCapabilities2KHR(VkPhysicalDevice, const VkDisplayPlaneInfo2KHR* info,
                                                           VkDisplayPlaneCapabilities2KHR* out)
{
    g_driver.record("vkGetDisplayPlaneCapabilities2KHR", handle_value(info->mode), info->planeIndex);
    out->capabilities = driver_plane_caps();
    return VK_SUCCESS;
}

inline VkResult VKAPI_CALL CreateDisplayPlaneSurfaceKHR(VkInstance, const VkDisplaySurfaceCreateInfoKHR* ci,
                                                        const VkAllocationCallbacks*, VkSurfaceKHR* out)
{
    g_driver.record("vkCreateDisplayPlaneSurfaceKHR", handle_value(ci->displayMode), ci->planeIndex);
    *out = reinterpret_cast<VkSurfaceKHR>(static_cast<uintptr_t>(0x5EE00001u));
    return VK_SUCCESS;
}

inline void VKAPI_CALL DestroySurfaceKHR(VkInstance, VkSurfaceKHR surface, const VkAllocationCallbacks*)
{
    g_driver.record("vkDestroySurfaceKHR", handle_value(surface));
}

inline VkResult VKAPI_CALL ReleaseDisplayEXT(VkPhysicalDevice, VkDisplayKHR display)
{
    g_driver.record("vkReleaseDisplayEXT", handle_value(display));
    return VK_SUCCESS;
}

inline VkResult VKAPI_CALL AcquireWinrtDisplayNV(VkPhysicalDevice, VkDisplayKHR display)
{
    g_driver.record("vkAcquireWinrtDisplayNV", handle_value(display));
    return VK_SUCCESS;
}

inline VkResult VKAPI_CALL GetWinrtDisplayNV(VkPhysicalDevice pd, uint32_t id, VkDisplayKHR* out)
{
    g_driver.record("vkGetWinrtDisplayNV", 0, id);
    *out = driver_display(gpu_slot(pd), 0);
    return VK_SUCCESS;
}

inline VkResult VKAPI_CALL GetPhysicalDeviceSurfaceSupportKHR(VkPhysicalDevice, uint32_t family, VkSurfaceKHR surface,
                                                              VkBool32* out)
{
    g_driver.record("vkGetPhysicalDeviceSurfaceSupportKHR", handle_value(surface), family);
    *out = VK_TRUE;
    return VK_SUCCESS;
}

inline VkResult VKAPI_CALL GetPhysicalDeviceSurfaceCapabilitiesKHR(VkPhysicalDevice, VkSurfaceKHR surface,
                                                                   VkSurfaceCapabilitiesKHR* out)
{
    g_driver.record("vkGetPhysicalDeviceSurfaceCapabilitiesKHR", handle_value(surface));
    *out = driver_caps();
    return VK_SUCCESS;
}

inline VkResult VKAPI_CALL GetPhysicalDeviceSurfaceFormatsKHR(VkPhysicalDevice, VkSurfaceKHR surface, uint32_t* count,
                                                              VkSurfaceFormatKHR* out)
{
    g_driver.record("vkGetPhysicalDeviceSurfaceFormatsKHR", handle_value(surface));
    return write_list(count, out,
                      std::vector<VkSurfaceFormatKHR>{{VK_FORMAT_B8G8R8A8_UNORM, VK_COLOR_SPACE_SRGB_NONLINEAR_KHR}});
}

inline VkResult VKAPI_CALL GetPhysicalDeviceSurfacePresentModesKHR(VkPhysicalDevice, VkSurfaceKHR surface,
                                                                   uint32_t* count, VkPresentModeKHR* out)
{
    g_driver.record("vkGetPhysicalDeviceSurfacePresentModesKHR", handle_value(surface));
    return write_list(count, out, std::vector<VkPresentModeKHR>{VK_PRESENT_MODE_FIFO_KHR});
}

inline VkResult VKAPI_CALL GetPhysicalDeviceSurfaceCapabilities2KHR(VkPhysicalDevice,
                                                                    const VkPhysicalDeviceSurfaceInfo2KHR* info,
                                                                    VkSurfaceCapabilities2KHR* out)
{
    g_driver.record("vkGetPhysicalDeviceSurfaceCapabilities2KHR", handle_value(info->surface));
    out->surfaceCapabilities = driver_caps();
    return VK_SUCCESS;
}

inline VkResult VKAPI_CALL GetPhysicalDeviceSurfaceFormats2KHR(VkPhysicalDevice,
                                                               const VkPhysicalDeviceSurfaceInfo2KHR* info,
                                                               uint32_t* count, VkSurfaceFormat2KHR* out)
{
    g_driver.record("vkGetPhysicalDeviceSurfaceFormats2KHR", handle_value(info->surface));
    return fill_list(count, out, 1, [](uint32_t, VkSurfaceFormat2KHR& f) {
        f.surfaceFormat = {VK_FORMAT_B8G8R8A8_UNORM, VK_COLOR_SPACE_SRGB_NONLINEAR_KHR};
    });
}

inline VkResult VKAPI_CALL GetPhysicalDeviceSurfaceCapabilities2EXT(VkPhysicalDevice, VkSurfaceKHR surface,
                                                                    VkSurfaceCapabilities2EXT* out)
{
    g_driver.record("vkGetPhysicalDeviceSurfaceCapabilities2EXT", handle_value(surface));
    const VkSurfaceCapabilitiesKHR c = driver_caps();
    out->minImageCount = c.minImageCount;
    out->maxImageCount = c.maxImageCount;
    out->currentExtent = c.currentExtent;
    out->supportedSurfaceCounters = 0;
    return VK_SUCCESS;
}

inline VkResult VKAPI_CALL GetPhysicalDevicePresentRectanglesKHR(VkPhysicalDevice, VkSurfaceKHR surface,
                                                                 uint32_t* count, VkRect2D* out)
{
    g_driver.record("vkGetPhysicalDevicePresentRectanglesKHR", handle_value(surface));
    return write_list(count, out, std::vector<VkRect2D>{{{0, 0}, {640, 480}}});
}

inline VkResult VKAPI_CALL GetPhysicalDeviceSurfacePresentModes2EXT(VkPhysicalDevice,
                                                                    const VkPhysicalDeviceSurfaceInfo2KHR* info,
                                                                    uint32_t* count, VkPresentModeKHR* out)
{
    g_driver.record("vkGetPhysicalDeviceSurfacePresentModes2EXT", handle_value(info->surface));
    return write_list(count, out, std::vector<VkPresentModeKHR>{VK_PRESENT_MODE_FIFO_KHR});
}

inline void VKAPI_CALL GetPhysicalDeviceMemoryProperties(VkPhysicalDevice, VkPhysicalDeviceMemoryProperties* out)
{
    g_driver.record("vkGetPhysicalDeviceMemoryProperties");
    *out = g_driver.memory;
}

inline const VkImageFormatListCreateInfo* format_list_in(const void* next)
{
    for (auto* s = static_cast<const VkBaseInStructure*>(next); s != nullptr; s = s->pNext) {
        if (s->sType == VK_STRUCTURE_TYPE_IMAGE_FORMAT_LIST_CREATE_INFO) {
            return reinterpret_cast<const VkImageFormatListCreateInfo*>(s);
        }
    }
    return nullptr;
}

inline std::vector<VkFormat> view_formats_of(const VkImageFormatListCreateInfo* list)
{
    if (list == nullptr || list->pViewFormats == nullptr) {
        return {};
    }
    return std::vector<VkFormat>(list->pViewFormats, list->pViewFormats + list->viewFormatCount);
}

inline VkResult VKAPI_CALL GetPhysicalDeviceImageFormatProperties2KHR(VkPhysicalDevice,
                                                                      const VkPhysicalDeviceImageFormatInfo2* info,
                                                                      VkImageFormatProperties2* out)
{
    g_driver.record("vkGetPhysicalDeviceImageFormatProperties2KHR", 0, static_cast<uint32_t>(info->format));
    FakeDriver::FormatQuery q{info->format, info->type, info->tiling, info->usage, info->flags};
    for (auto* s = static_cast<const VkBaseInStructure*>(info->pNext); s != nullptr; s = s->pNext) {
        if (s->sType == VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_EXTERNAL_IMAGE_FORMAT_INFO) {
            q.external = true;
            q.handle_type = reinterpret_cast<const VkPhysicalDeviceExternalImageFormatInfo*>(s)->handleType;
        }
    }
    const VkImageFormatListCreateInfo* list = format_list_in(info->pNext);
    q.format_list = list != nullptr;
    q.view_formats = view_formats_of(list);
    g_driver.format_queries.push_back(q);

    const VkResult result = g_driver.scripted("vkGetPhysicalDeviceImageFormatProperties2KHR");
    if (result != VK_SUCCESS) {
        return result;
    }
    VkImageFormatProperties& p = out->imageFormatProperties;
    p = VkImageFormatProperties{};
    p.maxExtent = {16384, 16384, 1};
    p.maxMipLevels = 15;
    p.maxArrayLayers = 2048;
    p.sampleCounts = VK_SAMPLE_COUNT_1_BIT;
    p.maxResourceSize = 1ull << 40;
    for (auto* s = static_cast<VkBaseOutStructure*>(out->pNext); s != nullptr; s = s->pNext) {
        if (s->sType == VK_STRUCTURE_TYPE_EXTERNAL_IMAGE_FORMAT_PROPERTIES) {
            VkExternalMemoryProperties& e =
                reinterpret_cast<VkExternalImageFormatProperties*>(s)->externalMemoryProperties;
            const auto it = g_driver.import_features.find(info->format);
            e.externalMemoryFeatures =
                it != g_driver.import_features.end() ? it->second : VK_EXTERNAL_MEMORY_FEATURE_IMPORTABLE_BIT;
            e.exportFromImportedHandleTypes = 0;
            e.compatibleHandleTypes = VK_EXTERNAL_MEMORY_HANDLE_TYPE_D3D11_TEXTURE_KMT_BIT;
        }
    }
    return VK_SUCCESS;
}

inline void VKAPI_CALL DestroyDevice(VkDevice device, const VkAllocationCallbacks*)
{
    g_driver.record("vkDestroyDevice", handle_value(device));
}

inline VkResult VKAPI_CALL CreateImage(VkDevice device, const VkImageCreateInfo* ci, const VkAllocationCallbacks*,
                                       VkImage* out)
{
    const VkResult result = g_driver.scripted("vkCreateImage");
    const VkImage image = result == VK_SUCCESS ? g_driver.next_image() : VK_NULL_HANDLE;
    g_driver.record("vkCreateImage", handle_value(image), static_cast<uint32_t>(ci->format));
    FakeDriver::ImageMade made{image, device, *ci};
    made.info.pNext = nullptr;
    made.info.pQueueFamilyIndices = nullptr;
    for (auto* s = static_cast<const VkBaseInStructure*>(ci->pNext); s != nullptr; s = s->pNext) {
        if (s->sType == VK_STRUCTURE_TYPE_EXTERNAL_MEMORY_IMAGE_CREATE_INFO) {
            made.external = true;
            made.handle_types = reinterpret_cast<const VkExternalMemoryImageCreateInfo*>(s)->handleTypes;
        }
    }
    const VkImageFormatListCreateInfo* list = format_list_in(ci->pNext);
    made.format_list = list != nullptr;
    made.view_formats = view_formats_of(list);
    g_driver.images_made.push_back(made);
    if (result != VK_SUCCESS) {
        return result;
    }
    g_driver.live_images.insert(handle_value(image));
    *out = image;
    return VK_SUCCESS;
}

inline void VKAPI_CALL DestroyImage(VkDevice, VkImage image, const VkAllocationCallbacks*)
{
    g_driver.record("vkDestroyImage", handle_value(image));
    g_driver.images_destroyed++;
    g_driver.live_images.erase(handle_value(image));
}

inline void VKAPI_CALL GetImageMemoryRequirements(VkDevice, VkImage image, VkMemoryRequirements* out)
{
    g_driver.record("vkGetImageMemoryRequirements", handle_value(image));
    out->size = 7104ull * 3840 * 4;
    out->alignment = 65536;
    out->memoryTypeBits = g_driver.image_memory_bits;
}

inline VkResult VKAPI_CALL GetMemoryWin32HandlePropertiesKHR(VkDevice, VkExternalMemoryHandleTypeFlagBits type,
                                                             HANDLE handle, VkMemoryWin32HandlePropertiesKHR* out)
{
    g_driver.record("vkGetMemoryWin32HandlePropertiesKHR", handle_value(handle), static_cast<uint32_t>(type));
    g_driver.handle_queries.push_back(FakeDriver::HandleQuery{type, handle});
    const VkResult result = g_driver.scripted("vkGetMemoryWin32HandlePropertiesKHR");
    if (result != VK_SUCCESS) {
        return result;
    }
    out->memoryTypeBits = g_driver.handle_memory_bits;
    return VK_SUCCESS;
}

inline VkResult VKAPI_CALL AllocateMemory(VkDevice, const VkMemoryAllocateInfo* info, const VkAllocationCallbacks*,
                                          VkDeviceMemory* out)
{
    const VkResult result = g_driver.scripted("vkAllocateMemory");
    const VkDeviceMemory memory = result == VK_SUCCESS ? g_driver.next_memory() : VK_NULL_HANDLE;
    g_driver.record("vkAllocateMemory", handle_value(memory), info->memoryTypeIndex);
    FakeDriver::Allocation a{memory, info->allocationSize, info->memoryTypeIndex};
    for (auto* s = static_cast<const VkBaseInStructure*>(info->pNext); s != nullptr; s = s->pNext) {
        if (s->sType == VK_STRUCTURE_TYPE_IMPORT_MEMORY_WIN32_HANDLE_INFO_KHR) {
            const auto* import = reinterpret_cast<const VkImportMemoryWin32HandleInfoKHR*>(s);
            a.imported = true;
            a.handle_type = import->handleType;
            a.handle = import->handle;
            a.name = import->name;
        } else if (s->sType == VK_STRUCTURE_TYPE_MEMORY_DEDICATED_ALLOCATE_INFO) {
            const auto* dedicated = reinterpret_cast<const VkMemoryDedicatedAllocateInfo*>(s);
            a.dedicated = true;
            a.dedicated_image = dedicated->image;
            a.dedicated_buffer = dedicated->buffer;
        }
    }
    g_driver.allocations.push_back(a);
    if (result != VK_SUCCESS) {
        return result;
    }
    g_driver.live_memory.insert(handle_value(memory));
    *out = memory;
    return VK_SUCCESS;
}

inline void VKAPI_CALL FreeMemory(VkDevice, VkDeviceMemory memory, const VkAllocationCallbacks*)
{
    g_driver.record("vkFreeMemory", handle_value(memory));
    g_driver.memory_freed++;
    g_driver.live_memory.erase(handle_value(memory));
}

inline VkResult VKAPI_CALL BindImageMemory(VkDevice, VkImage image, VkDeviceMemory memory, VkDeviceSize offset)
{
    g_driver.record("vkBindImageMemory", handle_value(image));
    g_driver.binds.push_back(FakeDriver::Bind{image, memory, offset});
    return g_driver.scripted("vkBindImageMemory");
}

inline VkFence driver_fence(size_t n)
{
    return reinterpret_cast<VkFence>(static_cast<uintptr_t>(0xFE000000u + n));
}

inline VkResult VKAPI_CALL CreateFence(VkDevice, const VkFenceCreateInfo* ci, const VkAllocationCallbacks*,
                                       VkFence* out)
{
    VkFence fence;
    {
        std::lock_guard<std::mutex> lock(g_driver.mutex);
        fence = driver_fence(g_driver.fences_made++);
        FakeDriver::FakeFence f;
        f.signalled = (ci->flags & VK_FENCE_CREATE_SIGNALED_BIT) != 0;
        g_driver.fences[handle_value(fence)] = f;
    }
    g_driver.record("vkCreateFence", handle_value(fence));
    *out = fence;
    return VK_SUCCESS;
}

inline void VKAPI_CALL DestroyFence(VkDevice, VkFence fence, const VkAllocationCallbacks*)
{
    {
        std::lock_guard<std::mutex> lock(g_driver.mutex);
        if (g_driver.fences.erase(handle_value(fence)) == 0) {
            g_driver.bad_fence_uses++;
        }
    }
    g_driver.record("vkDestroyFence", handle_value(fence));
}

inline VkResult VKAPI_CALL ResetFences(VkDevice, uint32_t count, const VkFence* fences)
{
    for (uint32_t i = 0; i < count; i++) {
        {
            std::lock_guard<std::mutex> lock(g_driver.mutex);
            const auto it = g_driver.fences.find(handle_value(fences[i]));
            if (it == g_driver.fences.end()) {
                g_driver.bad_fence_uses++;
            } else {
                it->second.signalled = false;
            }
        }
        g_driver.record("vkResetFences", handle_value(fences[i]));
    }
    return VK_SUCCESS;
}

inline VkResult VKAPI_CALL GetFenceStatus(VkDevice, VkFence fence)
{
    bool signalled = false;
    {
        std::lock_guard<std::mutex> lock(g_driver.mutex);
        const auto it = g_driver.fences.find(handle_value(fence));
        if (it == g_driver.fences.end()) {
            g_driver.bad_fence_uses++;
        } else {
            signalled = it->second.signalled;
        }
    }
    g_driver.record("vkGetFenceStatus", handle_value(fence));
    return signalled ? VK_SUCCESS : VK_NOT_READY;
}

inline VkResult VKAPI_CALL WaitForFences(VkDevice, uint32_t count, const VkFence* fences, VkBool32 wait_all,
                                         uint64_t timeout)
{
    for (uint32_t i = 0; i < count; i++) {
        g_driver.record("vkWaitForFences", handle_value(fences[i]));
    }
    const auto limit = std::chrono::nanoseconds(std::min<uint64_t>(timeout, 2'000'000'000ull));
    const auto start = std::chrono::steady_clock::now();
    for (;;) {
        uint32_t done = 0;
        {
            std::lock_guard<std::mutex> lock(g_driver.mutex);
            for (uint32_t i = 0; i < count; i++) {
                const auto it = g_driver.fences.find(handle_value(fences[i]));
                if (it == g_driver.fences.end()) {
                    g_driver.bad_fence_uses++;
                    return VK_ERROR_DEVICE_LOST;
                }
                done += it->second.signalled ? 1 : 0;
            }
        }
        if (wait_all ? done == count : done > 0) {
            return VK_SUCCESS;
        }
        if (std::chrono::steady_clock::now() - start >= limit) {
            return VK_TIMEOUT;
        }
        std::this_thread::sleep_for(std::chrono::microseconds(100));
    }
}

inline VkResult VKAPI_CALL QueueSubmit(VkQueue queue, uint32_t count, const VkSubmitInfo* infos, VkFence fence)
{
    const VkResult scripted = g_driver.scripted("vkQueueSubmit");
    if (scripted != VK_SUCCESS) {
        g_driver.record("vkQueueSubmit", handle_value(fence));
        return scripted;
    }
    FakeDriver::Submit s{queue, count, 0, 0, {}, fence};
    for (uint32_t i = 0; i < count; i++) {
        s.waits += infos[i].waitSemaphoreCount;
        s.signals += infos[i].signalSemaphoreCount;
        for (uint32_t w = 0; w < infos[i].waitSemaphoreCount; w++) {
            s.stages.push_back(infos[i].pWaitDstStageMask[w]);
        }
    }
    {
        std::lock_guard<std::mutex> lock(g_driver.mutex);
        g_driver.submits.push_back(s);
        if (fence != VK_NULL_HANDLE) {
            const auto it = g_driver.fences.find(handle_value(fence));
            if (it == g_driver.fences.end()) {
                g_driver.bad_fence_uses++;
            } else if (g_driver.hold_fences) {
                it->second.held = true;
            } else {
                it->second.signalled = true;
            }
        }
    }
    g_driver.record("vkQueueSubmit", handle_value(fence));
    return VK_SUCCESS;
}

inline VkResult VKAPI_CALL QueueWaitIdle(VkQueue queue)
{
    const VkResult scripted = g_driver.scripted("vkQueueWaitIdle");
    if (scripted != VK_SUCCESS) {
        g_driver.record("vkQueueWaitIdle", handle_value(queue));
        return scripted;
    }
    g_driver.signal_held();
    g_driver.record("vkQueueWaitIdle", handle_value(queue));
    return VK_SUCCESS;
}

inline VkResult VKAPI_CALL SignalSemaphore(VkDevice, const VkSemaphoreSignalInfo* info)
{
    g_driver.record("vkSignalSemaphore", handle_value(info->semaphore), static_cast<uint32_t>(info->value));
    return VK_SUCCESS;
}

inline VkResult VKAPI_CALL SignalSemaphoreKHR(VkDevice, const VkSemaphoreSignalInfo* info)
{
    g_driver.record("vkSignalSemaphoreKHR", handle_value(info->semaphore), static_cast<uint32_t>(info->value));
    return VK_SUCCESS;
}

inline VkSwapchainKHR driver_swapchain(size_t n)
{
    return reinterpret_cast<VkSwapchainKHR>(static_cast<uintptr_t>(0x5C000000u + n));
}

inline VkResult VKAPI_CALL CreateSwapchainKHR(VkDevice, const VkSwapchainCreateInfoKHR* ci,
                                              const VkAllocationCallbacks*, VkSwapchainKHR* out)
{
    VkSwapchainKHR made;
    {
        std::lock_guard<std::mutex> lock(g_driver.mutex);
        made = driver_swapchain(g_driver.swapchains_made++);
    }
    g_driver.record("vkCreateSwapchainKHR", handle_value(ci->surface));
    g_driver.record("vkCreateSwapchainKHR.oldSwapchain", handle_value(ci->oldSwapchain));
    *out = made;
    return VK_SUCCESS;
}

inline void VKAPI_CALL DestroySwapchainKHR(VkDevice, VkSwapchainKHR swapchain, const VkAllocationCallbacks*)
{
    g_driver.record("vkDestroySwapchainKHR", handle_value(swapchain));
}

inline VkResult VKAPI_CALL GetSwapchainImagesKHR(VkDevice, VkSwapchainKHR swapchain, uint32_t* count, VkImage* out)
{
    g_driver.record("vkGetSwapchainImagesKHR", handle_value(swapchain));
    return write_list(count, out, std::vector<VkImage>{driver_image(900), driver_image(901)});
}

inline VkResult VKAPI_CALL AcquireNextImageKHR(VkDevice, VkSwapchainKHR swapchain, uint64_t, VkSemaphore, VkFence,
                                               uint32_t* index)
{
    g_driver.record("vkAcquireNextImageKHR", handle_value(swapchain));
    *index = 1;
    return VK_SUCCESS;
}

inline VkResult VKAPI_CALL QueuePresentKHR(VkQueue, const VkPresentInfoKHR* info)
{
    for (uint32_t i = 0; i < info->swapchainCount; i++) {
        g_driver.record("vkQueuePresentKHR", handle_value(info->pSwapchains[i]));
    }
    return VK_SUCCESS;
}

inline VkResult VKAPI_CALL WaitForPresentKHR(VkDevice, VkSwapchainKHR swapchain, uint64_t, uint64_t)
{
    g_driver.record("vkWaitForPresentKHR", handle_value(swapchain));
    return VK_SUCCESS;
}

inline VkResult VKAPI_CALL WaitForPresent2KHR(VkDevice, VkSwapchainKHR swapchain, const VkPresentWait2InfoKHR*)
{
    g_driver.record("vkWaitForPresent2KHR", handle_value(swapchain));
    return VK_SUCCESS;
}

inline VkResult VKAPI_CALL ReleaseSwapchainImagesKHR(VkDevice, const VkReleaseSwapchainImagesInfoKHR* info)
{
    g_driver.record("vkReleaseSwapchainImagesKHR", handle_value(info->swapchain));
    return VK_SUCCESS;
}

inline VkResult VKAPI_CALL SetSwapchainPresentTimingQueueSizeEXT(VkDevice, VkSwapchainKHR swapchain, uint32_t)
{
    g_driver.record("vkSetSwapchainPresentTimingQueueSizeEXT", handle_value(swapchain));
    return VK_SUCCESS;
}

inline VkResult VKAPI_CALL GetSwapchainTimingPropertiesEXT(VkDevice, VkSwapchainKHR swapchain,
                                                           VkSwapchainTimingPropertiesEXT* properties, uint64_t* counter)
{
    g_driver.record("vkGetSwapchainTimingPropertiesEXT", handle_value(swapchain));
    properties->refreshDuration = 16'666'667;
    properties->refreshInterval = 16'666'667;
    if (counter != nullptr) {
        *counter = 7;
    }
    return VK_SUCCESS;
}

inline VkResult VKAPI_CALL GetSwapchainTimeDomainPropertiesEXT(VkDevice, VkSwapchainKHR swapchain,
                                                               VkSwapchainTimeDomainPropertiesEXT* properties,
                                                               uint64_t* counter)
{
    g_driver.record("vkGetSwapchainTimeDomainPropertiesEXT", handle_value(swapchain));
    properties->timeDomainCount = 0;
    if (counter != nullptr) {
        *counter = 7;
    }
    return VK_SUCCESS;
}

inline VkResult VKAPI_CALL GetPastPresentationTimingEXT(VkDevice, const VkPastPresentationTimingInfoEXT* info,
                                                        VkPastPresentationTimingPropertiesEXT* properties)
{
    g_driver.record("vkGetPastPresentationTimingEXT", handle_value(info->swapchain));
    properties->presentationTimingCount = 0;
    return VK_SUCCESS;
}

inline uint64_t fake_timestamp(VkTimeDomainKHR domain)
{
    return 1'000'000ULL * (static_cast<uint64_t>(domain) + 1);
}
inline constexpr uint64_t kFakeMaxDeviation = 1234;

inline VkResult calibrated_timestamps(const char* name, uint32_t count, const VkCalibratedTimestampInfoKHR* infos,
                                      uint64_t* timestamps, uint64_t* max_deviation)
{
    std::vector<FakeDriver::Calibration> call;
    for (uint32_t i = 0; i < count; i++) {
        uint64_t swapchain = 0;
        for (auto* s = static_cast<const VkBaseInStructure*>(infos[i].pNext); s != nullptr; s = s->pNext) {
            if (s->sType == VK_STRUCTURE_TYPE_SWAPCHAIN_CALIBRATED_TIMESTAMP_INFO_EXT) {
                swapchain = handle_value(reinterpret_cast<const VkSwapchainCalibratedTimestampInfoEXT*>(s)->swapchain);
            }
        }
        call.push_back(FakeDriver::Calibration{infos[i].timeDomain, swapchain, infos[i].pNext != nullptr});
        timestamps[i] = fake_timestamp(infos[i].timeDomain);
    }
    *max_deviation = kFakeMaxDeviation;
    {
        std::lock_guard<std::mutex> lock(g_driver.mutex);
        g_driver.calibrations.push_back(std::move(call));
    }
    g_driver.record(name);
    return VK_SUCCESS;
}

inline VkResult VKAPI_CALL GetCalibratedTimestampsKHR(VkDevice, uint32_t count, const VkCalibratedTimestampInfoKHR* infos,
                                                      uint64_t* timestamps, uint64_t* max_deviation)
{
    return calibrated_timestamps("vkGetCalibratedTimestampsKHR", count, infos, timestamps, max_deviation);
}

inline VkResult VKAPI_CALL GetCalibratedTimestampsEXT(VkDevice, uint32_t count, const VkCalibratedTimestampInfoEXT* infos,
                                                      uint64_t* timestamps, uint64_t* max_deviation)
{
    return calibrated_timestamps("vkGetCalibratedTimestampsEXT", count, infos, timestamps, max_deviation);
}

inline VkResult VKAPI_CALL SetDebugUtilsObjectNameEXT(VkDevice, const VkDebugUtilsObjectNameInfoEXT* info)
{
    g_driver.record("vkSetDebugUtilsObjectNameEXT", info->objectHandle);
    return VK_SUCCESS;
}

inline VkResult VKAPI_CALL SetPrivateData(VkDevice, VkObjectType, uint64_t handle, VkPrivateDataSlot, uint64_t)
{
    g_driver.record("vkSetPrivateData", handle);
    return VK_SUCCESS;
}

inline void VKAPI_CALL GetPrivateData(VkDevice, VkObjectType, uint64_t handle, VkPrivateDataSlot, uint64_t* out)
{
    g_driver.record("vkGetPrivateData", handle);
    *out = 77;
}

inline void VKAPI_CALL GetDeviceQueue(VkDevice, uint32_t family, uint32_t index, VkQueue* out)
{
    g_driver.record("vkGetDeviceQueue", family, index);
    g_driver.queue_objects.push_back(Dispatchable{&g_queue_table});
    *out = reinterpret_cast<VkQueue>(&g_driver.queue_objects.back());
}

inline VkResult VKAPI_CALL DeviceWaitIdle(VkDevice device)
{
    g_driver.record("vkDeviceWaitIdle", handle_value(device));
    if (g_driver.on_device_wait_idle) {
        g_driver.on_device_wait_idle();
    }
    return VK_SUCCESS;
}

inline VkResult VKAPI_CALL GetDeviceGroupSurfacePresentModesKHR(VkDevice, VkSurfaceKHR surface,
                                                                VkDeviceGroupPresentModeFlagsKHR* out)
{
    g_driver.record("vkGetDeviceGroupSurfacePresentModesKHR", handle_value(surface));
    *out = VK_DEVICE_GROUP_PRESENT_MODE_REMOTE_BIT_KHR;
    return VK_SUCCESS;
}

inline VkResult VKAPI_CALL GetDeviceGroupSurfacePresentModes2EXT(VkDevice, const VkPhysicalDeviceSurfaceInfo2KHR* info,
                                                                 VkDeviceGroupPresentModeFlagsKHR* out)
{
    g_driver.record("vkGetDeviceGroupSurfacePresentModes2EXT", handle_value(info->surface));
    *out = VK_DEVICE_GROUP_PRESENT_MODE_REMOTE_BIT_KHR;
    return VK_SUCCESS;
}

struct Entry {
    const char* name;
    PFN_vkVoidFunction fn;
};

#define DVL_FAKE_ENTRY(name) Entry{"vk" #name, reinterpret_cast<PFN_vkVoidFunction>(&name)}

inline PFN_vkVoidFunction VKAPI_CALL GetDeviceProcAddr(VkDevice, const char* name)
{
    static const Entry table[] = {
        DVL_FAKE_ENTRY(DestroyDevice),
        DVL_FAKE_ENTRY(GetDeviceQueue),
        DVL_FAKE_ENTRY(DeviceWaitIdle),
        DVL_FAKE_ENTRY(GetDeviceGroupSurfacePresentModesKHR),
        DVL_FAKE_ENTRY(GetDeviceGroupSurfacePresentModes2EXT),
        DVL_FAKE_ENTRY(CreateImage),
        DVL_FAKE_ENTRY(DestroyImage),
        DVL_FAKE_ENTRY(GetImageMemoryRequirements),
        DVL_FAKE_ENTRY(GetMemoryWin32HandlePropertiesKHR),
        DVL_FAKE_ENTRY(AllocateMemory),
        DVL_FAKE_ENTRY(FreeMemory),
        DVL_FAKE_ENTRY(BindImageMemory),
        DVL_FAKE_ENTRY(CreateFence),
        DVL_FAKE_ENTRY(DestroyFence),
        DVL_FAKE_ENTRY(ResetFences),
        DVL_FAKE_ENTRY(GetFenceStatus),
        DVL_FAKE_ENTRY(WaitForFences),
        DVL_FAKE_ENTRY(QueueSubmit),
        DVL_FAKE_ENTRY(QueueWaitIdle),
        DVL_FAKE_ENTRY(CreateSwapchainKHR),
        DVL_FAKE_ENTRY(DestroySwapchainKHR),
        DVL_FAKE_ENTRY(GetSwapchainImagesKHR),
        DVL_FAKE_ENTRY(AcquireNextImageKHR),
        DVL_FAKE_ENTRY(QueuePresentKHR),
        DVL_FAKE_ENTRY(WaitForPresentKHR),
        DVL_FAKE_ENTRY(WaitForPresent2KHR),
        DVL_FAKE_ENTRY(ReleaseSwapchainImagesKHR),
        DVL_FAKE_ENTRY(SetSwapchainPresentTimingQueueSizeEXT),
        DVL_FAKE_ENTRY(GetSwapchainTimingPropertiesEXT),
        DVL_FAKE_ENTRY(GetSwapchainTimeDomainPropertiesEXT),
        DVL_FAKE_ENTRY(GetPastPresentationTimingEXT),
        DVL_FAKE_ENTRY(GetCalibratedTimestampsKHR),
        DVL_FAKE_ENTRY(GetCalibratedTimestampsEXT),
        DVL_FAKE_ENTRY(SetDebugUtilsObjectNameEXT),
        DVL_FAKE_ENTRY(SetPrivateData),
        DVL_FAKE_ENTRY(GetPrivateData),
        DVL_FAKE_ENTRY(SignalSemaphore),
        DVL_FAKE_ENTRY(SignalSemaphoreKHR),
    };
    if (!g_driver.offer_signal_semaphore_khr && std::strcmp(name, "vkSignalSemaphoreKHR") == 0) {
        return nullptr;
    }
    for (const Entry& e : table) {
        if (std::strcmp(e.name, name) == 0) {
            return e.fn;
        }
    }
    return nullptr;
}

inline PFN_vkVoidFunction VKAPI_CALL GetInstanceProcAddr(VkInstance, const char* name)
{
    static const Entry table[] = {
        DVL_FAKE_ENTRY(CreateInstance),
        DVL_FAKE_ENTRY(DestroyInstance),
        DVL_FAKE_ENTRY(CreateDevice),
        DVL_FAKE_ENTRY(GetDeviceProcAddr),
        DVL_FAKE_ENTRY(GetPhysicalDeviceProperties),
        DVL_FAKE_ENTRY(GetPhysicalDeviceProperties2KHR),
        DVL_FAKE_ENTRY(GetPhysicalDeviceQueueFamilyProperties),
        DVL_FAKE_ENTRY(EnumerateDeviceExtensionProperties),
        DVL_FAKE_ENTRY(GetPhysicalDeviceDisplayPropertiesKHR),
        DVL_FAKE_ENTRY(GetPhysicalDeviceDisplayProperties2KHR),
        DVL_FAKE_ENTRY(GetPhysicalDeviceDisplayPlanePropertiesKHR),
        DVL_FAKE_ENTRY(GetPhysicalDeviceDisplayPlaneProperties2KHR),
        DVL_FAKE_ENTRY(GetDisplayPlaneSupportedDisplaysKHR),
        DVL_FAKE_ENTRY(GetDisplayModePropertiesKHR),
        DVL_FAKE_ENTRY(GetDisplayModeProperties2KHR),
        DVL_FAKE_ENTRY(CreateDisplayModeKHR),
        DVL_FAKE_ENTRY(GetDisplayPlaneCapabilitiesKHR),
        DVL_FAKE_ENTRY(GetDisplayPlaneCapabilities2KHR),
        DVL_FAKE_ENTRY(CreateDisplayPlaneSurfaceKHR),
        DVL_FAKE_ENTRY(DestroySurfaceKHR),
        DVL_FAKE_ENTRY(ReleaseDisplayEXT),
        DVL_FAKE_ENTRY(AcquireWinrtDisplayNV),
        DVL_FAKE_ENTRY(GetWinrtDisplayNV),
        DVL_FAKE_ENTRY(GetPhysicalDeviceSurfaceSupportKHR),
        DVL_FAKE_ENTRY(GetPhysicalDeviceSurfaceCapabilitiesKHR),
        DVL_FAKE_ENTRY(GetPhysicalDeviceSurfaceFormatsKHR),
        DVL_FAKE_ENTRY(GetPhysicalDeviceSurfacePresentModesKHR),
        DVL_FAKE_ENTRY(GetPhysicalDeviceSurfaceCapabilities2KHR),
        DVL_FAKE_ENTRY(GetPhysicalDeviceSurfaceFormats2KHR),
        DVL_FAKE_ENTRY(GetPhysicalDeviceSurfaceCapabilities2EXT),
        DVL_FAKE_ENTRY(GetPhysicalDevicePresentRectanglesKHR),
        DVL_FAKE_ENTRY(GetPhysicalDeviceSurfacePresentModes2EXT),
        DVL_FAKE_ENTRY(GetPhysicalDeviceMemoryProperties),
        DVL_FAKE_ENTRY(GetPhysicalDeviceImageFormatProperties2KHR),
    };
    for (const Entry& e : table) {
        if (std::strcmp(e.name, name) == 0) {
            return e.fn;
        }
    }
    return GetDeviceProcAddr(VK_NULL_HANDLE, name);
}

#undef DVL_FAKE_ENTRY

}

class ChainRig {
public:
    explicit ChainRig(std::vector<FakeGpu> gpus = default_gpus(), bool running_clock = false)
        : clock_owner(running_clock ? std::make_unique<nvdm::RunningClock>() : std::make_unique<nvdm::FakeClock>()),
          clock(*clock_owner), api(clock)
    {
        g_driver.reset(std::move(gpus));
        api.strict_bind = true;

        nvdm::SessionDeps deps{api, clock};
        deps.make_d3d11 = [this](const LUID&) {
            api.note("make_d3d11");
            return reinterpret_cast<IUnknown*>(static_cast<uintptr_t>(0x7000));
        };
        deps.processes = [this]() { return running; };
        deps.pin_module = []() {};
        deps.board_present = []() { return false; };
        deps.wait_ms = 10000;
        deps.sleep = [this](int64_t ns) { clock.advance(ns); };
        deps.log = [this](const std::string& line) { session_log.push_back(line); };
        session = std::make_unique<nvdm::DisplaySession>(std::move(deps));

        loaded = dll.loaded();
        const NegotiatedLayer n = negotiate(dll, 2);
        set_session = dll.proc<void (*)(nvdm::DisplaySession*)>("dvl_test_set_display_session");
        swapchain_trial = dll.proc<int (*)(VkDevice)>("dvl_test_trial_import_at_swapchain");
        direct_trial = dll.proc<int (*)(VkDevice)>("dvl_test_trial_import");
        set_log_sink = dll.proc<void (*)(dvl::LogSink, void*)>("dvl_test_set_log_sink");
        add_pending = dll.proc<int (*)(const char*)>("dvl_test_add_pending_line");
        console_event = dll.proc<int (*)(unsigned long)>("dvl_test_console_event");
        fill_trace_fn = dll.proc<int (*)()>("dvl_test_fill_trace");
        if (!loaded || n.result != VK_SUCCESS || set_session == nullptr) {
            return;
        }
        gipa = n.iface.pfnGetInstanceProcAddr;
        gdpa = n.iface.pfnGetDeviceProcAddr;
        set_session(session.get());
        create_instance();
    }

    void new_instance()
    {
        if (instance != VK_NULL_HANDLE) {
            fn<PFN_vkDestroyInstance>("vkDestroyInstance")(instance, nullptr);
            instance = VK_NULL_HANDLE;
        }
        create_instance();
    }

private:
    void create_instance()
    {
        VkLayerInstanceLink link{nullptr, fake::GetInstanceProcAddr, nullptr};
        VkLayerInstanceCreateInfo chain{};
        chain.sType = VK_STRUCTURE_TYPE_LOADER_INSTANCE_CREATE_INFO;
        chain.function = VK_LAYER_LINK_INFO;
        chain.u.pLayerInfo = &link;
        const char* extensions[] = {"VK_KHR_surface",
                                    "VK_KHR_display",
                                    "VK_KHR_get_physical_device_properties2",
                                    "VK_KHR_external_memory_capabilities",
                                    "VK_KHR_get_surface_capabilities2",
                                    "VK_KHR_get_display_properties2",
                                    "VK_EXT_display_surface_counter"};
        VkInstanceCreateInfo ci{};
        ci.sType = VK_STRUCTURE_TYPE_INSTANCE_CREATE_INFO;
        ci.pNext = &chain;
        ci.enabledExtensionCount = static_cast<uint32_t>(std::size(extensions));
        ci.ppEnabledExtensionNames = extensions;
        const auto create = reinterpret_cast<PFN_vkCreateInstance>(gipa(VK_NULL_HANDLE, "vkCreateInstance"));
        if (create != nullptr && create(&ci, nullptr, &instance) == VK_SUCCESS) {
            g_driver.clear_calls();
        }
    }

public:
    ~ChainRig()
    {
        if (instance != VK_NULL_HANDLE) {
            fn<PFN_vkDestroyInstance>("vkDestroyInstance")(instance, nullptr);
        }
        if (set_session != nullptr) {
            set_session(nullptr);
        }
    }

    ChainRig(const ChainRig&) = delete;
    ChainRig& operator=(const ChainRig&) = delete;

    bool ready() const { return loaded && gipa != nullptr && instance != VK_NULL_HANDLE; }

    template <class Fn>
    Fn fn(const char* name) const
    {
        return reinterpret_cast<Fn>(gipa(instance, name));
    }
    template <class Fn>
    Fn device_fn(VkDevice device, const char* name) const
    {
        return reinterpret_cast<Fn>(gdpa(device, name));
    }

    static VkPhysicalDevice gpu(size_t i) { return reinterpret_cast<VkPhysicalDevice>(&g_gpus[i]); }

    VkResult create_device(size_t gpu_index, const std::vector<VkDeviceQueueCreateInfo>& queues,
                           const std::vector<const char*>& extensions, VkDevice* out, bool with_callback = true)
    {
        VkLayerDeviceLink link{nullptr, fake::GetInstanceProcAddr, fake::GetDeviceProcAddr};
        VkLayerDeviceCreateInfo callback{};
        callback.sType = VK_STRUCTURE_TYPE_LOADER_DEVICE_CREATE_INFO;
        callback.function = VK_LOADER_DATA_CALLBACK;
        callback.u.pfnSetDeviceLoaderData = fake::SetDeviceLoaderData;
        VkLayerDeviceCreateInfo chain{};
        chain.sType = VK_STRUCTURE_TYPE_LOADER_DEVICE_CREATE_INFO;
        chain.pNext = with_callback ? &callback : nullptr;
        chain.function = VK_LAYER_LINK_INFO;
        chain.u.pLayerInfo = &link;
        VkDeviceCreateInfo ci{};
        ci.sType = VK_STRUCTURE_TYPE_DEVICE_CREATE_INFO;
        ci.pNext = &chain;
        ci.queueCreateInfoCount = static_cast<uint32_t>(queues.size());
        ci.pQueueCreateInfos = queues.data();
        ci.enabledExtensionCount = static_cast<uint32_t>(extensions.size());
        ci.ppEnabledExtensionNames = extensions.data();
        const auto create = fn<PFN_vkCreateDevice>("vkCreateDevice");
        return create(gpu(gpu_index), &ci, nullptr, out);
    }

    void destroy_device(VkDevice device) { device_fn<PFN_vkDestroyDevice>(device, "vkDestroyDevice")(device, nullptr); }

    VkQueue get_queue(VkDevice device, uint32_t family, uint32_t index)
    {
        VkQueue queue = VK_NULL_HANDLE;
        device_fn<PFN_vkGetDeviceQueue>(device, "vkGetDeviceQueue")(device, family, index, &queue);
        if (queue != VK_NULL_HANDLE) {
            *reinterpret_cast<void**>(queue) = *reinterpret_cast<void**>(device);
        }
        return queue;
    }

    int trial_import_at_swapchain(VkDevice device) const
    {
        return swapchain_trial != nullptr ? swapchain_trial(device) : -1;
    }
    int trial_import(VkDevice device) const { return direct_trial != nullptr ? direct_trial(device) : -1; }

    int add_pending_line(const char* line) const { return add_pending != nullptr ? add_pending(line) : -1; }

    int console(DWORD event) const { return console_event != nullptr ? console_event(event) : -1; }

    int fill_trace() const { return fill_trace_fn != nullptr ? fill_trace_fn() : -1; }

    std::unique_ptr<nvdm::FakeClock> clock_owner;
    nvdm::FakeClock& clock;
    nvdm::FakeApi api;
    std::vector<std::string> running;
    std::vector<std::string> session_log;
    std::unique_ptr<nvdm::DisplaySession> session;

    ScopedEnv processes{L"MIRADOR_DIRECT_MODE_PROCESSES", L"dvl_tests.exe"};
    ScopedEnv quiet{L"MIRADOR_DIRECT_MODE_LOG", L"off"};
    LayerDll dll{DVL_LAYER_TEST_DLL};
    bool loaded = false;
    void (*set_session)(nvdm::DisplaySession*) = nullptr;
    int (*swapchain_trial)(VkDevice) = nullptr;
    int (*direct_trial)(VkDevice) = nullptr;
    void (*set_log_sink)(dvl::LogSink, void*) = nullptr;
    int (*add_pending)(const char*) = nullptr;
    int (*console_event)(unsigned long) = nullptr;
    int (*fill_trace_fn)() = nullptr;
    PFN_vkGetInstanceProcAddr gipa = nullptr;
    PFN_vkGetDeviceProcAddr gdpa = nullptr;
    VkInstance instance = VK_NULL_HANDLE;
};

class LogCapture {
public:
    struct Line {
        dvl::Level level;
        std::string text;
        DWORD thread;
    };

    explicit LogCapture(const ChainRig& rig) : set_(rig.set_log_sink)
    {
        if (set_ != nullptr) {
            set_(&LogCapture::take, this);
        }
    }
    ~LogCapture()
    {
        if (set_ != nullptr) {
            set_(nullptr, nullptr);
        }
    }
    LogCapture(const LogCapture&) = delete;
    LogCapture& operator=(const LogCapture&) = delete;

    bool ready() const { return set_ != nullptr; }

    size_t count(const std::string& part, DWORD thread = 0) const
    {
        std::lock_guard<std::mutex> hold(mutex_);
        return static_cast<size_t>(std::count_if(lines_.begin(), lines_.end(), [&](const Line& l) {
            return l.text.find(part) != std::string::npos && (thread == 0 || l.thread == thread);
        }));
    }

    std::vector<Line> lines() const
    {
        std::lock_guard<std::mutex> hold(mutex_);
        return lines_;
    }

private:
    static void take(dvl::Level level, const char* text, void* self)
    {
        auto* capture = static_cast<LogCapture*>(self);
        std::lock_guard<std::mutex> hold(capture->mutex_);
        capture->lines_.push_back(Line{level, text, GetCurrentThreadId()});
    }

    void (*set_)(dvl::LogSink, void*);
    mutable std::mutex mutex_;
    std::vector<Line> lines_;
};

struct MonadoQueue {
    VkDeviceQueueGlobalPriorityCreateInfoKHR priority{VK_STRUCTURE_TYPE_DEVICE_QUEUE_GLOBAL_PRIORITY_CREATE_INFO_KHR,
                                                      nullptr, VK_QUEUE_GLOBAL_PRIORITY_REALTIME_KHR};
    float priorities[1] = {0.5f};
    VkDeviceQueueCreateInfo info{VK_STRUCTURE_TYPE_DEVICE_QUEUE_CREATE_INFO, &priority, 0, 2, 1, priorities};
    MonadoQueue() = default;
    MonadoQueue(const MonadoQueue&) = delete;
    MonadoQueue& operator=(const MonadoQueue&) = delete;
};

}
