// Copyright 2026, Finn Zeruhn
// SPDX-License-Identifier: BSL-1.0

#include "layer/swapchain.hpp"

#include <algorithm>
#include <chrono>
#include <cinttypes>
#include <cstdio>
#include <optional>
#include <string>
#include <unordered_map>

#include "core/display_session.hpp"
#include "layer/counted.hpp"
#include "layer/display.hpp"
#include "layer/env.hpp"
#include "layer/exit.hpp"
#include "layer/import.hpp"
#include "layer/import_rules.hpp"
#include "layer/intercepts.hpp"
#include "layer/surface.hpp"

namespace dvl {
namespace {

constexpr int64_t kMaxWaitSliceNs = 100'000'000;
constexpr int64_t kOutstandingFenceWaitNs = 100'000'000;
constexpr int64_t kLeaveSlackNs = 2'000'000;
constexpr int64_t kFlushBeforeWaitNs = 1'000'000;
constexpr size_t kTraceRecords = size_t{1} << 20;
constexpr size_t kConsoleHookTraceRecords = size_t{1} << 16;

template <class T>
T as_handle(uint64_t value)
{
    return reinterpret_cast<T>(static_cast<uintptr_t>(value));
}

unsigned long long ull(uint64_t value)
{
    return static_cast<unsigned long long>(value);
}

struct SwapchainRegistry {
    std::mutex mutex;
    std::unordered_map<const void*, std::shared_ptr<SwapchainRecord>> records;
};

SwapchainRegistry& registry()
{
    static SwapchainRegistry* const r = new SwapchainRegistry;
    return *r;
}

const void* key(VkSwapchainKHR swapchain)
{
    return reinterpret_cast<const void*>(swapchain);
}

std::mutex& presenter_mutex()
{
    static std::mutex* const m = new std::mutex;
    return *m;
}

std::atomic<Presenter*> g_presenter{nullptr};

int64_t refresh_period(const nvdm::Product& product)
{
    return product.refresh_mhz != 0 ? 1'000'000'000'000LL / product.refresh_mhz : 11'111'111;
}

nvdm::FrameModelConfig model_config(int64_t refresh_ns)
{
    nvdm::FrameModelConfig config;
    config.refresh_ns = refresh_ns;
    return config;
}

const char* present_mode_name(VkPresentModeKHR mode)
{
    switch (mode) {
    case VK_PRESENT_MODE_IMMEDIATE_KHR: return "IMMEDIATE";
    case VK_PRESENT_MODE_MAILBOX_KHR: return "MAILBOX";
    case VK_PRESENT_MODE_FIFO_KHR: return "FIFO";
    case VK_PRESENT_MODE_FIFO_RELAXED_KHR: return "FIFO_RELAXED";
    default: return "unknown";
    }
}

void keep_timer_resolution()
{
    static std::atomic<bool> done{false};
    if (done.exchange(true)) {
        return;
    }
    PROCESS_POWER_THROTTLING_STATE state{};
    state.Version = PROCESS_POWER_THROTTLING_CURRENT_VERSION;
    state.ControlMask = PROCESS_POWER_THROTTLING_IGNORE_TIMER_RESOLUTION;
    state.StateMask = 0;
    if (SetProcessInformation(GetCurrentProcess(), ProcessPowerThrottling, &state, sizeof(state))) {
        log(Level::Info, "process: timer resolution kept when Monado's console is hidden (Windows 11 power "
                         "throttling off for timer resolution)");
    } else {
        log(Level::Info, "process: could not keep the timer resolution (%lu); Windows before 11 does not throttle it",
            GetLastError());
    }
}

void start_display_thread(Presenter& p, DeviceData& device)
{
    keep_timer_resolution();
    auto ops = std::make_shared<LayerFenceOps>(device, p.clock);
    p.past_ops.push_back(ops);
    p.fence_ops = ops;

    nvdm::DisplayLoopConfig config;
    for (uint32_t i = 0; i < kImageCount; ++i) {
        config.tags.push_back(p.session.back_buffer(i).tag);
    }
    config.refresh_ns = p.refresh_ns;
    Presenter* const pp = &p;
    config.log = [pp](const std::string& line) {
        pp->pending.flush();
        log(Level::Info, "%s", line.c_str());
    };
    config.refresh_seen = [pp](int64_t qpc_ns) {
        pp->events.on_refresh(qpc_ns);
        pp->changed.notify_all();
    };
    config.events_pending = [pp] { return pp->events.pending(); };
    config.trace = p.trace.get();
    nvdm::FenceOps* const fences = ops.get();
    p.thread->start([pp, fences, config] {
        return std::make_unique<nvdm::DisplayLoop>(pp->session.api(), pp->model, pp->lock, pp->clock, pp->waiter,
                                                   *fences, pp->stats, config);
    });
    set_console_stop_hook([pp] {
        pp->thread->stop_and_join(static_cast<int64_t>(kConsoleHookWaitMs) * 1'000'000);
        pp->write_trace(kConsoleHookTraceRecords);
    });
}

void stop_display_thread(Presenter& p)
{
    const bool joined = p.thread->stop_and_join(nvdm::kJoinTimeoutNs);
    set_console_stop_hook({});
    p.fence_ops.reset();
    if (!joined) {
        Locked lk(p);
        p.warnings.on_thread_left_behind();
    }
}

void attach_device_locked(Presenter& p, DeviceData& device)
{
    if (p.device == &device) {
        return;
    }
    if (p.device != nullptr) {
        char line[160];
        std::snprintf(line, sizeof(line), "the frames of device %p end: device %p presents from now on",
                      static_cast<void*>(p.device->handle), static_cast<void*>(device.handle));
        p.pending.add(Level::Info, line);
        p.model.device_destroyed();
    }
    p.model.set_pool(&device.fence_pool);
    p.device = &device;
}

VkResult refuse_swapchain(const std::string& why)
{
    log(Level::Error, "vkCreateSwapchainKHR on the layer's surface refused: %s", why.c_str());
    return VK_ERROR_INITIALIZATION_FAILED;
}

ImportConfig swapchain_config(const VkSwapchainCreateInfoKHR& ci, const SurfaceSpec& spec)
{
    ImportConfig config{};
    config.format = ci.imageFormat == VK_FORMAT_R8G8B8A8_SRGB ? VK_FORMAT_R8G8B8A8_SRGB : VK_FORMAT_R8G8B8A8_UNORM;
    config.usage = ci.imageUsage & spec.usage;
    if (config.usage == 0) {
        config.usage = config.format == VK_FORMAT_R8G8B8A8_SRGB ? kGraphicsConfig.usage : kComputeConfig.usage;
    }
    return config;
}

std::string describe_device(const DeviceData& device)
{
    char text[32];
    std::snprintf(text, sizeof(text), "%p", static_cast<void*>(device.handle));
    return text;
}

VkResult create_our_swapchain(VkDevice device, const VkSwapchainCreateInfoKHR& ci, const SurfaceRecord& surface,
                              VkSwapchainKHR* out)
{
    DeviceData& d = device_data(device);
    nvdm::DisplaySession& session = display_session();
    if (!session.is_open()) {
        return refuse_swapchain("the display session is not open");
    }
    Presenter& p = presenter();
    std::lock_guard<std::mutex> life(p.lifecycle);

    if (d.instance == nullptr || !display_listed_now(*d.instance, d.physical_device)) {
        return refuse_swapchain("device " + describe_device(d) +
                                " is not on the GPU that drives the headset (its instance did not list the display "
                                "on the device's GPU)");
    }
    if (!d.missing_import_extensions.empty()) {
        return refuse_swapchain("device " + describe_device(d) +
                                " was made without extensions the back-buffer import needs (the driver lacks them)");
    }
    if (d.private_queue == VK_NULL_HANDLE) {
        return refuse_swapchain("device " + describe_device(d) +
                                " has no private queue to signal acquire with (no queue family had room)");
    }
    if (!trial_import_at_swapchain(d)) {
        return refuse_swapchain("the trial import failed on device " + describe_device(d));
    }
    const SwapchainCheck check = check_swapchain_parameters(ci, surface.spec);
    if (check.refused) {
        return refuse_swapchain(check.reason);
    }
    bool other_device = false;
    {
        Locked lk(p);
        other_device = p.swapchains > 0 && p.device != &d;
    }
    if (other_device) {
        return refuse_swapchain("device " + describe_device(d) +
                                " is not the device that has the layer's swapchains; one device at a time");
    }
    for (const std::string& note : check.notes) {
        log(Level::Warn, "vkCreateSwapchainKHR: %s; the swapchain is made from the surface's capabilities",
            note.c_str());
    }
    if (ci.presentMode != VK_PRESENT_MODE_FIFO_KHR) {
        log(Level::Info, "vkCreateSwapchainKHR: present mode %s is paced as FIFO (one frame per refresh)",
            present_mode_name(ci.presentMode));
    }

    const ImportConfig config = swapchain_config(ci, surface.spec);
    auto record = std::make_shared<SwapchainRecord>();
    record->format = config.format;
    record->usage = config.usage;
    record->device = &d;
    record->surface = ci.surface;
    record->present_mode = ci.presentMode;
    record->present_at = present_at_enabled();

    const std::shared_ptr<SwapchainRecord> old = find_swapchain(ci.oldSwapchain);
    bool moved = false;
    if (old && old->device == &d && old->format == config.format && old->usage == config.usage) {
        Locked lk(p);
        if (old->alive && !old->images_moved) {
            record->images = old->images;
            old->images_moved = true;
            moved = true;
        }
    }
    if (!moved) {
        const VkExtent2D extent{session.product().width, session.product().height};
        for (uint32_t i = 0; i < kImageCount; ++i) {
            const VkResult r = import_back_buffer(d, session.back_buffer(i).shared, extent, config, record->images[i]);
            if (r != VK_SUCCESS) {
                for (ImportedImage& image : record->images) {
                    release_imported(d, image);
                }
                log(Level::Error,
                    "vkCreateSwapchainKHR: importing back-buffer %u as %s usage 0x%x on device %p failed (%d) after "
                    "the trial import passed; a bug: please report this log",
                    i, format_name(config.format).c_str(), static_cast<unsigned>(config.usage),
                    static_cast<void*>(device), static_cast<int>(r));
                return r;
            }
        }
    }

    bool first = false;
    {
        Locked lk(p);
        attach_device_locked(p, d);
        record->gen = p.model.add_swapchain();
        p.timings.add_swapchain(record->gen);
        first = p.swapchains++ == 0;
        p.warnings.set_display_control(d.display_control);
        p.warnings.set_present_mode(ci.presentMode);
        p.warnings.set_memory_fallback(d.memory_fallback_logged.load());
    }
    const VkSwapchainKHR handle = add_swapchain(record);
    if (first) {
        start_display_thread(p, d);
    }
    log(Level::Info,
        "vkCreateSwapchainKHR: swapchain %p (generation %llu) on the %s: %u images as %s usage 0x%x, present mode "
        "%s, %s, present at a time %s%s",
        static_cast<void*>(handle), ull(record->gen), session.product().name, kImageCount,
        format_name(config.format).c_str(), static_cast<unsigned>(config.usage), present_mode_name(ci.presentMode),
        moved ? "images moved from the old swapchain" : "back-buffers imported",
        record->present_at ? "on" : "off (MIRADOR_DIRECT_MODE_NO_PRESENT_AT)", first ? "; display thread started" : "");
    *out = handle;
    return VK_SUCCESS;
}

bool destroy_our_swapchain(Presenter& p, VkSwapchainKHR handle, const std::shared_ptr<SwapchainRecord>& record)
{
    bool last = false;
    bool release = false;
    {
        Locked lk(p);
        if (!record->alive) {
            return false;
        }
        record->alive = false;
        p.model.remove_swapchain(record->gen);
        p.timings.remove_swapchain(record->gen);
        release = !record->images_moved;
        record->images_moved = true;
        record->acquired = {};
        last = p.swapchains > 0 && --p.swapchains == 0;
        p.changed.notify_all();
    }
    remove_swapchain(handle);
    if (release) {
        for (ImportedImage& image : record->images) {
            release_imported(*record->device, image);
        }
    }
    if (last) {
        stop_display_thread(p);
    }
    log(Level::Info, "vkDestroySwapchainKHR: swapchain %p (generation %llu)%s%s", static_cast<void*>(handle),
        ull(record->gen), release ? ", images released" : ", images kept by the newer swapchain",
        last ? "; the last one: display thread stopped" : "");
    return last;
}

}

VkSwapchainKHR add_swapchain(std::shared_ptr<SwapchainRecord> record)
{
    const VkSwapchainKHR handle = reinterpret_cast<VkSwapchainKHR>(record.get());
    SwapchainRegistry& r = registry();
    std::lock_guard<std::mutex> lock(r.mutex);
    r.records[key(handle)] = std::move(record);
    return handle;
}

std::shared_ptr<SwapchainRecord> find_swapchain(VkSwapchainKHR swapchain)
{
    if (swapchain == VK_NULL_HANDLE) {
        return nullptr;
    }
    SwapchainRegistry& r = registry();
    std::lock_guard<std::mutex> lock(r.mutex);
    const auto it = r.records.find(key(swapchain));
    return it == r.records.end() ? nullptr : it->second;
}

std::shared_ptr<SwapchainRecord> remove_swapchain(VkSwapchainKHR swapchain)
{
    SwapchainRegistry& r = registry();
    std::lock_guard<std::mutex> lock(r.mutex);
    const auto it = r.records.find(key(swapchain));
    if (it == r.records.end()) {
        return nullptr;
    }
    std::shared_ptr<SwapchainRecord> record = std::move(it->second);
    r.records.erase(it);
    return record;
}

std::vector<std::pair<VkSwapchainKHR, std::shared_ptr<SwapchainRecord>>> swapchains_of(const DeviceData& device)
{
    std::vector<std::pair<VkSwapchainKHR, std::shared_ptr<SwapchainRecord>>> out;
    SwapchainRegistry& r = registry();
    std::lock_guard<std::mutex> lock(r.mutex);
    for (const auto& entry : r.records) {
        if (entry.second->device == &device) {
            out.emplace_back(reinterpret_cast<VkSwapchainKHR>(const_cast<void*>(entry.first)), entry.second);
        }
    }
    return out;
}

bool is_our_object(VkObjectType type, uint64_t handle)
{
    switch (type) {
    case VK_OBJECT_TYPE_DISPLAY_KHR: return is_our_display(as_handle<VkDisplayKHR>(handle));
    case VK_OBJECT_TYPE_DISPLAY_MODE_KHR: return is_our_mode(as_handle<VkDisplayModeKHR>(handle));
    case VK_OBJECT_TYPE_SURFACE_KHR: return find_surface(as_handle<VkSurfaceKHR>(handle)) != nullptr;
    case VK_OBJECT_TYPE_SWAPCHAIN_KHR: return find_swapchain(as_handle<VkSwapchainKHR>(handle)) != nullptr;
    default: return false;
    }
}

bool is_our_debug_report_object(VkDebugReportObjectTypeEXT type, uint64_t handle)
{
    switch (type) {
    case VK_DEBUG_REPORT_OBJECT_TYPE_DISPLAY_KHR_EXT: return is_our_object(VK_OBJECT_TYPE_DISPLAY_KHR, handle);
    case VK_DEBUG_REPORT_OBJECT_TYPE_DISPLAY_MODE_KHR_EXT:
        return is_our_object(VK_OBJECT_TYPE_DISPLAY_MODE_KHR, handle);
    case VK_DEBUG_REPORT_OBJECT_TYPE_SURFACE_KHR_EXT: return is_our_object(VK_OBJECT_TYPE_SURFACE_KHR, handle);
    case VK_DEBUG_REPORT_OBJECT_TYPE_SWAPCHAIN_KHR_EXT: return is_our_object(VK_OBJECT_TYPE_SWAPCHAIN_KHR, handle);
    default: return false;
    }
}

void PendingLines::add(Level level, std::string line)
{
    std::lock_guard<std::mutex> hold(mutex_);
    lines_.emplace_back(level, std::move(line));
}

void PendingLines::flush()
{
    std::vector<std::pair<Level, std::string>> lines;
    {
        std::lock_guard<std::mutex> hold(mutex_);
        lines.swap(lines_);
    }
    for (const auto& line : lines) {
        log(line.first, "%s", line.second.c_str());
    }
}

LayerWaiter::LayerWaiter(nvdm::Clock& clock, PendingLines& pending) : clock_(clock), inner_(clock), pending_(pending) {}

bool LayerWaiter::wait_frame_or_until(int64_t deadline_ns)
{
    if (deadline_ns - clock_.now_ns() >= kFlushBeforeWaitNs) {
        pending_.flush();
    }
    return inner_.wait_frame_or_until(deadline_ns);
}

bool LayerWaiter::wait_frame_or_until_coarse(int64_t deadline_ns)
{
    if (deadline_ns - clock_.now_ns() >= kFlushBeforeWaitNs) {
        pending_.flush();
    }
    return inner_.wait_frame_or_until_coarse(deadline_ns);
}

void LayerWaiter::signal_frame_queued()
{
    inner_.signal_frame_queued();
}

LayerFenceOps::LayerFenceOps(DeviceData& device, nvdm::Clock& clock) : device_(&device), clock_(clock) {}

DeviceData* LayerFenceOps::enter(nvdm::FenceSlot slot, VkFence& fence)
{
    std::lock_guard<std::mutex> hold(mutex_);
    if (device_ == nullptr || slot >= device_->fences.size()) {
        return nullptr;
    }
    fence = device_->fences[slot];
    if (fence == VK_NULL_HANDLE) {
        return nullptr;
    }
    in_call_ = true;
    return device_;
}

void LayerFenceOps::exit()
{
    std::lock_guard<std::mutex> hold(mutex_);
    in_call_ = false;
    idle_.notify_all();
}

bool LayerFenceOps::wait(nvdm::FenceSlot slot, int64_t deadline_ns)
{
    VkFence fence = VK_NULL_HANDLE;
    DeviceData* const d = enter(slot, fence);
    if (d == nullptr) {
        return false;
    }
    const int64_t now = clock_.now_ns();
    const uint64_t timeout = deadline_ns > now ? static_cast<uint64_t>(deadline_ns - now) : 0;
    const VkResult r =
        d->WaitForFences != nullptr ? d->WaitForFences(d->handle, 1, &fence, VK_TRUE, timeout) : VK_ERROR_UNKNOWN;
    exit();
    return r == VK_SUCCESS;
}

bool LayerFenceOps::status(nvdm::FenceSlot slot)
{
    VkFence fence = VK_NULL_HANDLE;
    DeviceData* const d = enter(slot, fence);
    if (d == nullptr) {
        return false;
    }
    const VkResult r = d->GetFenceStatus != nullptr ? d->GetFenceStatus(d->handle, fence) : VK_ERROR_UNKNOWN;
    exit();
    return r == VK_SUCCESS;
}

DeviceData* LayerFenceOps::device()
{
    std::lock_guard<std::mutex> hold(mutex_);
    return device_;
}

bool LayerFenceOps::leave(int64_t timeout_ns)
{
    std::unique_lock<std::mutex> hold(mutex_);
    device_ = nullptr;
    return idle_.wait_for(hold, std::chrono::nanoseconds(std::max<int64_t>(timeout_ns, 0)),
                          [this] { return !in_call_; });
}

Presenter::Presenter(nvdm::DisplaySession& s)
    : session(s), clock(s.clock()), lock(s.lock()), refresh_ns(refresh_period(s.product())),
      model(model_config(refresh_ns), nullptr),
      thread(new nvdm::DisplayThread([](const std::string& line) { log(Level::Info, "%s", line.c_str()); })),
      waiter(clock, pending)
{
    model.set_log([this](const std::string& line) { pending.add(Level::Info, line); });
    model.set_report([this](const nvdm::FrameReport& report) { timings.on_report(report); });
    if (const std::optional<std::wstring> folder =
            trace_folder(env_get(L"MIRADOR_DIRECT_MODE_TRACE"), process_logger().dir())) {
        if (folder->empty()) {
            log(Level::Warn, "trace: MIRADOR_DIRECT_MODE_TRACE names no folder to write to (1 is the log's folder, and "
                             "there is none: %%LOCALAPPDATA%% is not set); no trace");
        } else {
            trace_path = *folder + L"\\mirador-trace-" + std::to_wstring(GetCurrentProcessId()) + L".csv";
            trace = std::make_unique<nvdm::Trace>(kTraceRecords, [this] {
                try {
                    pending.add(Level::Info, "trace: the array of " + std::to_string(kTraceRecords) +
                                                 " records is full; later records are dropped, and " +
                                                 to_utf8(trace_path) + " gets the ones held");
                } catch (...) {
                }
            });
            log(Level::Info, "trace: on, appended to %s each time the display thread stops",
                to_utf8(trace_path).c_str());
        }
    }
    if (waiter.high_resolution()) {
        log(Level::Info, "display thread: high-resolution waitable timer; refresh period %lld ns",
            static_cast<long long>(refresh_ns));
    } else {
        log(Level::Warn, "display thread: this Windows has no high-resolution waitable timer, so the display thread's "
                         "waits may end late");
    }
}

void Presenter::write_trace(size_t newest_at_most)
{
    if (!trace) {
        return;
    }
    const nvdm::Trace::Written written = trace->append_csv(trace_path, newest_at_most);
    if (!written.ok) {
        log(Level::Warn, "trace: cannot write %s", to_utf8(trace_path).c_str());
        return;
    }
    if (written.records != 0) {
        log(Level::Info, "trace: %zu records written to %s (%llu dropped so far)", written.records,
            to_utf8(trace_path).c_str(), static_cast<unsigned long long>(trace->dropped()));
    }
    if (written.skipped != 0) {
        log(Level::Info, "trace: %zu older records not written yet are left out (the console handler gives its hook "
                         "%lu ms)",
            written.skipped, static_cast<unsigned long>(kConsoleHookWaitMs));
    }
}

void Presenter::wait_changed(std::unique_lock<std::mutex>& hold, int64_t until_ns)
{
    const int64_t now = clock.now_ns();
    if (until_ns <= now) {
        return;
    }
    changed.wait_for(hold, std::chrono::nanoseconds(std::min(until_ns - now, kMaxWaitSliceNs)));
}

Presenter& presenter()
{
    if (Presenter* p = g_presenter.load(std::memory_order_acquire)) {
        return *p;
    }
    std::lock_guard<std::mutex> hold(presenter_mutex());
    Presenter* p = g_presenter.load(std::memory_order_acquire);
    if (p == nullptr) {
        p = new Presenter(display_session());
        g_presenter.store(p, std::memory_order_release);
    }
    return *p;
}

Presenter* presenter_if_any()
{
    return g_presenter.load(std::memory_order_acquire);
}

void reset_presenter_for_tests()
{
    std::lock_guard<std::mutex> hold(presenter_mutex());
    if (Presenter* p = g_presenter.exchange(nullptr)) {
        std::lock_guard<std::mutex> life(p->lifecycle);
        p->thread->stop_and_join(nvdm::kJoinTimeoutNs);
        set_console_stop_hook({});
    }
    SwapchainRegistry& r = registry();
    std::lock_guard<std::mutex> lock(r.mutex);
    r.records.clear();
}

Locked::Locked(Presenter& p, Flush flush) : hold(p.lock), p_(p), flush_(flush) {}

Locked::~Locked()
{
    try {
        if (hold.owns_lock()) {
            for (std::string& line : p_.warnings.take_lines()) {
                p_.pending.add(Level::Warn, std::move(line));
            }
            hold.unlock();
        }
        if (flush_ == Flush::Now) {
            p_.pending.flush();
        }
    } catch (...) {
    }
}

int64_t deadline_after(nvdm::Clock& clock, uint64_t timeout_ns)
{
    const int64_t now = clock.now_ns();
    if (timeout_ns >= static_cast<uint64_t>(INT64_MAX - std::max<int64_t>(now, 0))) {
        return INT64_MAX;
    }
    return now + static_cast<int64_t>(timeout_ns);
}

void before_device_destroyed(DeviceData& device)
{
    Presenter* const p = presenter_if_any();
    if (p == nullptr) {
        return;
    }
    std::vector<std::shared_ptr<LayerFenceOps>> epochs;
    bool stopped = false;
    {
        std::lock_guard<std::mutex> life(p->lifecycle);
        for (const auto& entry : swapchains_of(device)) {
            log(Level::Warn, "vkDestroyDevice: swapchain %p of device %p was not destroyed first (an application "
                             "error); destroyed now",
                static_cast<void*>(entry.first), static_cast<void*>(device.handle));
            stopped = destroy_our_swapchain(*p, entry.first, entry.second) || stopped;
        }
        epochs = p->past_ops;
    }
    if (stopped) {
        p->write_trace();
    }
    for (const std::shared_ptr<LayerFenceOps>& ops : epochs) {
        if (ops->device() == &device && !ops->leave(p->refresh_ns + kLeaveSlackNs)) {
            log(Level::Error, "vkDestroyDevice: the display thread is still in a fence call on device %p after a "
                              "refresh period; going on",
                static_cast<void*>(device.handle));
        }
    }
    std::vector<VkFence> outstanding;
    {
        Locked lk(*p);
        for (nvdm::FenceSlot slot = 0; slot < device.fence_pool.created(); ++slot) {
            const nvdm::FenceState state = device.fence_pool.state(slot);
            if ((state == nvdm::FenceState::Attached || state == nvdm::FenceState::LeftOver) &&
                device.fences[slot] != VK_NULL_HANDLE) {
                outstanding.push_back(device.fences[slot]);
            }
        }
    }
    if (!outstanding.empty() && device.WaitForFences != nullptr) {
        const VkResult r = device.WaitForFences(device.handle, static_cast<uint32_t>(outstanding.size()),
                                                outstanding.data(), VK_TRUE, kOutstandingFenceWaitNs);
        if (r != VK_SUCCESS) {
            log(Level::Warn, "vkDestroyDevice: %zu frame fences of device %p not signalled after 100 ms (%d); "
                             "destroyed anyway",
                outstanding.size(), static_cast<void*>(device.handle), static_cast<int>(r));
        }
    }
}

void device_destroyed(DeviceData& device)
{
    if (Presenter* const p = presenter_if_any()) {
        Locked lk(*p);
        if (p->device == &device) {
            p->model.device_destroyed();
            p->device = nullptr;
            p->changed.notify_all();
        }
        for (const VkFence fence : p->events.fences_of(device.handle)) {
            p->warnings.on_event_gone(handle_key(fence));
        }
        p->events.remove_device(device.handle);
        p->event_count.store(p->events.size());
    }
    for (VkFence& fence : device.fences) {
        if (fence != VK_NULL_HANDLE && device.DestroyFence != nullptr) {
            device.DestroyFence(device.handle, fence, nullptr);
        }
        fence = VK_NULL_HANDLE;
    }
}

VKAPI_ATTR VkResult VKAPI_CALL layer_CreateSwapchainKHR(VkDevice device, const VkSwapchainCreateInfoKHR* ci,
                                                        const VkAllocationCallbacks* alloc, VkSwapchainKHR* out)
{
    return guarded("vkCreateSwapchainKHR", [&] {
        if (const SurfaceRecord* surface = find_surface(ci->surface)) {
            return create_our_swapchain(device, *ci, *surface, out);
        }
        DeviceData& data = device_data(device);
        if (find_swapchain(ci->oldSwapchain)) {
            log(Level::Warn, "vkCreateSwapchainKHR: oldSwapchain %p is the layer's, on another surface; not passed "
                             "down",
                static_cast<void*>(ci->oldSwapchain));
            VkSwapchainCreateInfoKHR down = *ci;
            down.oldSwapchain = VK_NULL_HANDLE;
            return call_down(data.CreateSwapchainKHR, "vkCreateSwapchainKHR", device, &down, alloc, out);
        }
        return call_down(data.CreateSwapchainKHR, "vkCreateSwapchainKHR", device, ci, alloc, out);
    });
}

VKAPI_ATTR void VKAPI_CALL layer_DestroySwapchainKHR(VkDevice device, VkSwapchainKHR swapchain,
                                                     const VkAllocationCallbacks* alloc)
{
    guarded_void("vkDestroySwapchainKHR", [&] {
        if (std::shared_ptr<SwapchainRecord> record = find_swapchain(swapchain)) {
            Presenter& p = presenter();
            bool stopped = false;
            {
                std::lock_guard<std::mutex> life(p.lifecycle);
                stopped = destroy_our_swapchain(p, swapchain, record);
            }
            if (stopped) {
                p.write_trace();
            }
            return;
        }
        if (swapchain == VK_NULL_HANDLE) {
            return;
        }
        DeviceData& data = device_data(device);
        if (data.DestroySwapchainKHR == nullptr) {
            missing_below("vkDestroySwapchainKHR");
            return;
        }
        data.DestroySwapchainKHR(device, swapchain, alloc);
    });
}

VKAPI_ATTR VkResult VKAPI_CALL layer_GetSwapchainImagesKHR(VkDevice device, VkSwapchainKHR swapchain, uint32_t* count,
                                                           VkImage* images)
{
    return guarded("vkGetSwapchainImagesKHR", [&] {
        if (std::shared_ptr<SwapchainRecord> record = find_swapchain(swapchain)) {
            return fill_list(count, images, kImageCount,
                             [&](uint32_t i, VkImage& image) { image = record->images[i].image; });
        }
        DeviceData& data = device_data(device);
        return call_down(data.GetSwapchainImagesKHR, "vkGetSwapchainImagesKHR", device, swapchain, count, images);
    });
}

VKAPI_ATTR VkResult VKAPI_CALL layer_GetSwapchainStatusKHR(VkDevice device, VkSwapchainKHR swapchain)
{
    return guarded("vkGetSwapchainStatusKHR", [&] {
        if (find_swapchain(swapchain)) {
            return VK_SUCCESS;
        }
        DeviceData& data = device_data(device);
        return call_down(data.GetSwapchainStatusKHR, "vkGetSwapchainStatusKHR", device, swapchain);
    });
}

}
