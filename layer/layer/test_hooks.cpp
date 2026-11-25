// Copyright 2026, Finn Zeruhn
// SPDX-License-Identifier: BSL-1.0

#include "layer/exit.hpp"
#include "layer/import.hpp"
#include "layer/intercepts.hpp"
#include "layer/log.hpp"
#include "layer/swapchain.hpp"

extern "C" __declspec(dllexport) void dvl_test_set_display_session(nvdm::DisplaySession* session)
{
    dvl::reset_presenter_for_tests();
    dvl::set_display_session_for_tests(session);
}

extern "C" __declspec(dllexport) void dvl_test_set_log_sink(dvl::LogSink sink, void* context)
{
    dvl::set_log_sink_for_tests(sink, context);
}

extern "C" __declspec(dllexport) int dvl_test_add_pending_line(const char* line)
{
    dvl::Presenter* const p = dvl::presenter_if_any();
    if (p == nullptr || line == nullptr) {
        return 0;
    }
    p->pending.add(dvl::Level::Warn, line);
    return 1;
}

extern "C" __declspec(dllexport) int dvl_test_console_event(unsigned long event)
{
    return dvl::handle_console_event(event) ? 1 : 0;
}

extern "C" __declspec(dllexport) int dvl_test_fill_trace()
{
    dvl::Presenter* const p = dvl::presenter_if_any();
    if (p == nullptr || !p->trace) {
        return 0;
    }
    while (p->trace->dropped() == 0) {
        p->note(nvdm::TraceKind::Counter);
    }
    return 1;
}

namespace {

template <class Trial>
int on_device(VkDevice device, const char* name, Trial&& trial)
{
    int answer = -1;
    dvl::guarded_void(name, [&] {
        if (dvl::DeviceData* data = dvl::find_device_data(device)) {
            answer = trial(*data) ? 1 : 0;
        }
    });
    return answer;
}

}

extern "C" __declspec(dllexport) int dvl_test_trial_import_at_swapchain(VkDevice device)
{
    return on_device(device, "dvl_test_trial_import_at_swapchain",
                     [](dvl::DeviceData& data) { return dvl::trial_import_at_swapchain(data); });
}

extern "C" __declspec(dllexport) int dvl_test_trial_import(VkDevice device)
{
    return on_device(device, "dvl_test_trial_import", [](dvl::DeviceData& data) { return dvl::trial_import(data); });
}
