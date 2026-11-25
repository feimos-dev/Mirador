// Copyright 2026, Finn Zeruhn
// SPDX-License-Identifier: BSL-1.0

#include <catch2/catch_test_macros.hpp>

#include <vulkan/vulkan.h>

#include <array>
#include <cstdint>
#include <optional>
#include <vector>

#include "core/frame_model.hpp"
#include "fakes/layer_dll.hpp"
#include "layer/present_timing.hpp"

namespace {

using dvl::PresentRequest;
using dvl::PresentTimings;
using nvdm::FrameReport;
using nvdm::FrameTimes;

constexpr VkPresentStageFlagsEXT kQueueEnd = VK_PRESENT_STAGE_QUEUE_OPERATIONS_END_BIT_EXT;
constexpr VkPresentStageFlagsEXT kDequeued = VK_PRESENT_STAGE_REQUEST_DEQUEUED_BIT_EXT;
constexpr VkPresentStageFlagsEXT kPixelOut = VK_PRESENT_STAGE_IMAGE_FIRST_PIXEL_OUT_BIT_EXT;
constexpr VkPresentStageFlagsEXT kVisible = VK_PRESENT_STAGE_IMAGE_FIRST_PIXEL_VISIBLE_BIT_EXT;
constexpr VkPresentStageFlagsEXT kAll = kQueueEnd | kDequeued | kPixelOut;

constexpr nvdm::SwapchainGen kGen = 1;
const FrameTimes kShown{100, 200, 300};

PresentRequest asking(uint64_t id, VkPresentStageFlagsEXT stages = kAll, uint64_t domain = dvl::kQpcTimeDomainId,
                      uint64_t target = 0)
{
    PresentRequest r;
    r.present_id = id;
    r.timing = true;
    r.stages = stages;
    r.time_domain_id = domain;
    r.target_time = target;
    return r;
}

class Results {
public:
    explicit Results(size_t n)
        : timings_(n, VkPastPresentationTimingEXT{VK_STRUCTURE_TYPE_PAST_PRESENTATION_TIMING_EXT}), stages_(n)
    {
    }
    Results(const Results&) = delete;
    Results& operator=(const Results&) = delete;

    VkResult read(PresentTimings& t, VkPastPresentationTimingFlagsEXT flags = 0,
                  const PresentTimings::LiveTimes& live = {}, uint32_t room = 4)
    {
        for (size_t i = 0; i < timings_.size(); ++i) {
            timings_[i].presentStageCount = room;
            timings_[i].pPresentStages = stages_[i].data();
        }
        props.presentationTimingCount = static_cast<uint32_t>(timings_.size());
        props.pPresentationTimings = timings_.data();
        return t.past(kGen, flags, props, live);
    }
    uint32_t count(PresentTimings& t, VkPastPresentationTimingFlagsEXT flags = 0,
                   const PresentTimings::LiveTimes& live = {})
    {
        props.presentationTimingCount = 99;
        props.pPresentationTimings = nullptr;
        REQUIRE(t.past(kGen, flags, props, live) == VK_SUCCESS);
        return props.presentationTimingCount;
    }

    const VkPastPresentationTimingEXT& operator[](size_t i) const { return timings_[i]; }
    uint32_t written() const { return props.presentationTimingCount; }
    std::vector<std::pair<VkPresentStageFlagsEXT, uint64_t>> stages(size_t i) const
    {
        std::vector<std::pair<VkPresentStageFlagsEXT, uint64_t>> out;
        for (uint32_t k = 0; k < timings_[i].presentStageCount; ++k) {
            out.emplace_back(timings_[i].pPresentStages[k].stage, timings_[i].pPresentStages[k].time);
        }
        return out;
    }

    VkPastPresentationTimingPropertiesEXT props{VK_STRUCTURE_TYPE_PAST_PRESENTATION_TIMING_PROPERTIES_EXT};

private:
    std::vector<VkPastPresentationTimingEXT> timings_;
    std::vector<std::array<VkPresentStageTimeEXT, 4>> stages_;
};

using Stages = std::vector<std::pair<VkPresentStageFlagsEXT, uint64_t>>;

}

TEST_CASE("present timing: the surface offers three stages and present at an absolute time, in two QPC domains")
{
    VkPresentTimingSurfaceCapabilitiesEXT caps{VK_STRUCTURE_TYPE_PRESENT_TIMING_SURFACE_CAPABILITIES_EXT};
    caps.presentAtRelativeTimeSupported = VK_TRUE;
    dvl::fill_present_timing_caps(caps, true);
    CHECK(caps.presentTimingSupported == VK_TRUE);
    CHECK(caps.presentAtAbsoluteTimeSupported == VK_TRUE);
    CHECK(caps.presentAtRelativeTimeSupported == VK_FALSE);
    CHECK(caps.presentStageQueries == kAll);

    VkPresentTimingSurfaceCapabilitiesEXT off{VK_STRUCTURE_TYPE_PRESENT_TIMING_SURFACE_CAPABILITIES_EXT};
    off.presentAtAbsoluteTimeSupported = VK_TRUE;
    dvl::fill_present_timing_caps(off, false);
    CHECK(off.presentTimingSupported == VK_TRUE);
    CHECK(off.presentAtAbsoluteTimeSupported == VK_FALSE);
    CHECK(off.presentAtRelativeTimeSupported == VK_FALSE);
    CHECK(off.presentStageQueries == kAll);

    VkSwapchainTimeDomainPropertiesEXT domains{VK_STRUCTURE_TYPE_SWAPCHAIN_TIME_DOMAIN_PROPERTIES_EXT};
    REQUIRE(dvl::fill_time_domains(domains) == VK_SUCCESS);
    CHECK(domains.timeDomainCount == 2);
    VkTimeDomainKHR kinds[2] = {};
    uint64_t ids[2] = {};
    domains.pTimeDomains = kinds;
    domains.pTimeDomainIds = ids;
    REQUIRE(dvl::fill_time_domains(domains) == VK_SUCCESS);
    CHECK(domains.timeDomainCount == 2);
    CHECK(kinds[0] == VK_TIME_DOMAIN_QUERY_PERFORMANCE_COUNTER_KHR);
    CHECK(ids[0] == dvl::kQpcTimeDomainId);
    CHECK(kinds[1] == VK_TIME_DOMAIN_PRESENT_STAGE_LOCAL_EXT);
    CHECK(ids[1] == dvl::kStageLocalTimeDomainId);
    domains.timeDomainCount = 1;
    CHECK(dvl::fill_time_domains(domains) == VK_INCOMPLETE);
    CHECK(domains.timeDomainCount == 1);
}

TEST_CASE("present timing: a present's request comes from its pNext chain")
{
    uint64_t id1 = 11;
    uint64_t id2 = 22;
    VkPresentIdKHR v1{VK_STRUCTURE_TYPE_PRESENT_ID_KHR, nullptr, 1, &id1};
    VkPresentId2KHR v2{VK_STRUCTURE_TYPE_PRESENT_ID_2_KHR, &v1, 1, &id2};
    VkPresentTimingInfoEXT info{VK_STRUCTURE_TYPE_PRESENT_TIMING_INFO_EXT};
    info.flags = VK_PRESENT_TIMING_INFO_PRESENT_AT_NEAREST_REFRESH_CYCLE_BIT_EXT;
    info.targetTime = 5'000;
    info.timeDomainId = dvl::kStageLocalTimeDomainId;
    info.presentStageQueries = kPixelOut;
    VkPresentTimingsInfoEXT timings{VK_STRUCTURE_TYPE_PRESENT_TIMINGS_INFO_EXT, &v2, 1, &info};
    VkPresentInfoKHR present{VK_STRUCTURE_TYPE_PRESENT_INFO_KHR, &timings};

    PresentRequest r = dvl::read_present_request(present);
    CHECK(r.present_id == 22);
    CHECK(r.timing);
    CHECK(r.timing_flags == VK_PRESENT_TIMING_INFO_PRESENT_AT_NEAREST_REFRESH_CYCLE_BIT_EXT);
    CHECK(r.target_time == 5'000);
    CHECK(r.time_domain_id == dvl::kStageLocalTimeDomainId);
    CHECK(r.stages == kPixelOut);

    v2.pNext = nullptr;
    v1.pNext = &timings;
    timings.pNext = &v2;
    present.pNext = &v1;
    CHECK(dvl::read_present_request(present).present_id == 22);
    v1.pNext = nullptr;
    CHECK(dvl::read_present_request(present).present_id == 11);
    CHECK_FALSE(dvl::read_present_request(present).timing);

    timings.pTimingInfos = nullptr;
    timings.pNext = nullptr;
    present.pNext = &timings;
    CHECK_FALSE(dvl::read_present_request(present).timing);
    present.pNext = nullptr;
    r = dvl::read_present_request(present);
    CHECK(r.present_id == 0);
    CHECK_FALSE(r.timing);
}

TEST_CASE("present timing: a timed present holds a slot of the results queue until its complete results are read")
{
    PresentTimings t;
    t.add_swapchain(kGen);
    CHECK_FALSE(t.slot_free(kGen, asking(1)));
    CHECK(t.slot_free(kGen, asking(1, 0)));
    CHECK(t.slot_free(kGen, PresentRequest{}));

    REQUIRE(t.set_queue_size(kGen, 2) == VK_SUCCESS);
    CHECK(t.slot_free(kGen, asking(1)));
    t.queued(kGen, 10, asking(1));
    t.queued(kGen, 11, asking(2));
    t.queued(kGen, 12, asking(3, 0));
    CHECK(t.slots_taken(kGen) == 2);
    CHECK_FALSE(t.slot_free(kGen, asking(4)));
    CHECK(t.set_queue_size(kGen, 1) == VK_NOT_READY);
    CHECK_FALSE(t.slot_free(kGen, asking(4)));

    Results r(4);
    CHECK(r.count(t) == 0);
    t.on_report(FrameReport{kGen, 10, kShown});
    t.on_report(FrameReport{kGen, 12, kShown});
    CHECK(r.count(t) == 1);
    REQUIRE(r.read(t) == VK_SUCCESS);
    REQUIRE(r.written() == 1);
    CHECK(r.props.timingPropertiesCounter == dvl::kTimingPropertiesCounter);
    CHECK(r.props.timeDomainsCounter == dvl::kTimeDomainsCounter);
    CHECK(r[0].presentId == 1);
    CHECK(r[0].reportComplete == VK_TRUE);
    CHECK(r[0].timeDomain == VK_TIME_DOMAIN_QUERY_PERFORMANCE_COUNTER_KHR);
    CHECK(r[0].timeDomainId == dvl::kQpcTimeDomainId);
    CHECK(r.stages(0) == Stages{{kQueueEnd, 100}, {kDequeued, 200}, {kPixelOut, 300}});

    CHECK(t.slots_taken(kGen) == 1);
    CHECK(t.slot_free(kGen, asking(4)));
    CHECK(r.count(t) == 0);
    REQUIRE(t.set_queue_size(kGen, 1) == VK_SUCCESS);
}

TEST_CASE("present timing: results come in present order, partial and out of order only when allowed")
{
    PresentTimings t;
    t.add_swapchain(kGen);
    REQUIRE(t.set_queue_size(kGen, 8) == VK_SUCCESS);
    t.queued(kGen, 10, asking(1));
    t.queued(kGen, 11, asking(2));
    t.queued(kGen, 12, asking(3));
    t.on_report(FrameReport{kGen, 11, FrameTimes{150, 0, 0}});

    Results r(4);
    CHECK(r.count(t) == 0);
    constexpr VkPastPresentationTimingFlagsEXT kAnyOrder = VK_PAST_PRESENTATION_TIMING_ALLOW_OUT_OF_ORDER_RESULTS_BIT_EXT;
    CHECK(r.count(t, kAnyOrder) == 1);

    const PresentTimings::LiveTimes live = [](nvdm::FrameSeq seq) -> std::optional<FrameTimes> {
        if (seq == 10) {
            return FrameTimes{100, 200, 0};
        }
        if (seq == 12) {
            return FrameTimes{};
        }
        return std::nullopt;
    };
    constexpr VkPastPresentationTimingFlagsEXT kPartial = VK_PAST_PRESENTATION_TIMING_ALLOW_PARTIAL_RESULTS_BIT_EXT;
    REQUIRE(r.read(t, kPartial, live) == VK_SUCCESS);
    REQUIRE(r.written() == 2);
    CHECK(r[0].presentId == 1);
    CHECK(r[0].reportComplete == VK_FALSE);
    CHECK(r.stages(0) == Stages{{kQueueEnd, 100}, {kDequeued, 200}});
    CHECK(r[1].presentId == 2);
    CHECK(r[1].reportComplete == VK_TRUE);
    CHECK(r.stages(1) == Stages{{kQueueEnd, 150}, {kDequeued, 0}, {kPixelOut, 0}});

    CHECK(t.slots_taken(kGen) == 2);
    t.on_report(FrameReport{kGen, 10, kShown});
    REQUIRE(r.read(t) == VK_SUCCESS);
    REQUIRE(r.written() == 1);
    CHECK(r[0].presentId == 1);
    CHECK(r[0].reportComplete == VK_TRUE);
    CHECK(r.stages(0) == Stages{{kQueueEnd, 100}, {kDequeued, 200}, {kPixelOut, 300}});
    CHECK(t.slots_taken(kGen) == 1);
}

TEST_CASE("present timing: a complete result lists every stage asked for, 0 where the frame never got there")
{
    PresentTimings t;
    t.add_swapchain(kGen);
    REQUIRE(t.set_queue_size(kGen, 4) == VK_SUCCESS);
    t.queued(kGen, 10, asking(1, kAll | kVisible));
    t.queued(kGen, 11, asking(2, kPixelOut | kQueueEnd));
    t.on_report(FrameReport{kGen, 10, FrameTimes{100, 200, 0}});
    t.on_report(FrameReport{kGen, 11, kShown});

    Results r(4);
    REQUIRE(r.read(t) == VK_SUCCESS);
    REQUIRE(r.written() == 2);
    CHECK(r.stages(0) == Stages{{kQueueEnd, 100}, {kDequeued, 200}, {kPixelOut, 0}, {kVisible, 0}});
    CHECK(r.stages(1) == Stages{{kQueueEnd, 100}, {kPixelOut, 300}});

    t.queued(kGen, 12, asking(3));
    t.on_report(FrameReport{kGen, 12, kShown});
    REQUIRE(r.read(t, 0, {}, 2) == VK_SUCCESS);
    CHECK(r.stages(0) == Stages{{kQueueEnd, 100}, {kDequeued, 200}});
}

TEST_CASE("present timing: a short array gets VK_INCOMPLETE and the rest stays for the next call")
{
    PresentTimings t;
    t.add_swapchain(kGen);
    REQUIRE(t.set_queue_size(kGen, 4) == VK_SUCCESS);
    for (uint64_t i = 0; i < 3; ++i) {
        t.queued(kGen, 10 + i, asking(1 + i));
        t.on_report(FrameReport{kGen, 10 + i, kShown});
    }
    Results two(2);
    CHECK(two.read(t) == VK_INCOMPLETE);
    REQUIRE(two.written() == 2);
    CHECK(two[0].presentId == 1);
    CHECK(two[1].presentId == 2);
    CHECK(t.slots_taken(kGen) == 1);
    CHECK(two.read(t) == VK_SUCCESS);
    REQUIRE(two.written() == 1);
    CHECK(two[0].presentId == 3);
    CHECK(t.slots_taken(kGen) == 0);
}

TEST_CASE("present timing: a result names its request's time domain and target time, else falls back to QPC")
{
    PresentTimings t;
    t.add_swapchain(kGen);
    REQUIRE(t.set_queue_size(kGen, 4) == VK_SUCCESS);
    t.queued(kGen, 10, asking(1, kAll, dvl::kStageLocalTimeDomainId, 123'456));
    t.queued(kGen, 11, asking(2, kAll, 99));
    t.on_report(FrameReport{kGen, 10, kShown});
    t.on_report(FrameReport{kGen, 11, kShown});

    Results r(2);
    REQUIRE(r.read(t) == VK_SUCCESS);
    REQUIRE(r.written() == 2);
    CHECK(r[0].timeDomain == VK_TIME_DOMAIN_PRESENT_STAGE_LOCAL_EXT);
    CHECK(r[0].timeDomainId == dvl::kStageLocalTimeDomainId);
    CHECK(r[0].targetTime == 123'456);
    CHECK(r.stages(0) == Stages{{kQueueEnd, 100}, {kDequeued, 200}, {kPixelOut, 300}});
    CHECK(r[1].timeDomain == VK_TIME_DOMAIN_QUERY_PERFORMANCE_COUNTER_KHR);
    CHECK(r[1].timeDomainId == dvl::kQpcTimeDomainId);
    CHECK(r[1].targetTime == 0);
}

TEST_CASE("present timing: a frame the model never took, and a removed swapchain")
{
    PresentTimings t;
    t.add_swapchain(kGen);
    REQUIRE(t.set_queue_size(kGen, 4) == VK_SUCCESS);
    t.queued(kGen, 10, asking(1));
    t.not_queued(kGen, 10);
    Results r(2);
    REQUIRE(r.read(t) == VK_SUCCESS);
    REQUIRE(r.written() == 1);
    CHECK(r[0].reportComplete == VK_TRUE);
    CHECK(r.stages(0) == Stages{{kQueueEnd, 0}, {kDequeued, 0}, {kPixelOut, 0}});

    t.queued(kGen, 11, asking(2));
    t.remove_swapchain(kGen);
    t.on_report(FrameReport{kGen, 11, kShown});
    CHECK(t.slots_taken(kGen) == 0);
    CHECK(t.slot_free(kGen, asking(3)));
    CHECK(r.count(t) == 0);
}

TEST_CASE("present timing: a target time holds its frame for the flip at or after it, NEAREST for the one nearest it")
{
    using Use = dvl::Target::Use;
    constexpr int64_t kPeriod = 11'111'111;
    constexpr int64_t kNow = 1'000'000'000'000;
    const auto targeted = [](uint64_t target, VkPresentTimingInfoFlagsEXT flags = 0) {
        PresentRequest r = asking(1, 0, dvl::kQpcTimeDomainId, target);
        r.timing_flags = flags;
        return r;
    };
    constexpr VkPresentTimingInfoFlagsEXT kNearest = VK_PRESENT_TIMING_INFO_PRESENT_AT_NEAREST_REFRESH_CYCLE_BIT_EXT;
    const uint64_t target = kNow + 3 * kPeriod;

    CHECK(dvl::read_target(PresentRequest{}, kPeriod, kNow, true).use == Use::None);
    CHECK(dvl::read_target(targeted(0, kNearest), kPeriod, kNow, true).use == Use::None);

    dvl::Target t = dvl::read_target(targeted(target), kPeriod, kNow, true);
    CHECK(t.use == Use::Hold);
    CHECK(t.not_before_ns == static_cast<int64_t>(target) - dvl::kTargetToleranceNs);
    t = dvl::read_target(targeted(target, kNearest), kPeriod, kNow, true);
    CHECK(t.use == Use::Hold);
    CHECK(t.not_before_ns == static_cast<int64_t>(target) - kPeriod / 2);
    PresentRequest local = targeted(target, kNearest);
    local.time_domain_id = dvl::kStageLocalTimeDomainId;
    CHECK(dvl::read_target(local, kPeriod, kNow, true).not_before_ns == t.not_before_ns);

    CHECK(dvl::read_target(targeted(kNow - kPeriod), kPeriod, kNow, true).use == Use::Hold);
    CHECK(dvl::read_target(targeted(kNow + dvl::kFarTargetNs), kPeriod, kNow, true).use == Use::Hold);

    CHECK(dvl::read_target(targeted(target, kNearest), kPeriod, kNow, false).use == Use::Off);
    CHECK(dvl::read_target(targeted(kPeriod, VK_PRESENT_TIMING_INFO_PRESENT_AT_RELATIVE_TIME_BIT_EXT), kPeriod, kNow,
                           true)
              .use == Use::Relative);
    CHECK(dvl::read_target(targeted(kNow + dvl::kFarTargetNs + 1), kPeriod, kNow, true).use == Use::TooFar);
    CHECK(dvl::read_target(targeted(UINT64_MAX, kNearest), kPeriod, kNow, true).use == Use::TooFar);
    for (const Use use : {Use::Off, Use::Relative, Use::TooFar}) {
        CHECK(dvl::Target{use}.not_before_ns == 0);
    }
}

TEST_CASE("present timing: MIRADOR_DIRECT_MODE_NO_PRESENT_AT switches present at a time off")
{
    {
        dvl_test::ScopedEnv unset(L"MIRADOR_DIRECT_MODE_NO_PRESENT_AT", nullptr);
        CHECK(dvl::present_at_enabled());
    }
    {
        dvl_test::ScopedEnv zero(L"MIRADOR_DIRECT_MODE_NO_PRESENT_AT", L"0");
        CHECK(dvl::present_at_enabled());
    }
    {
        dvl_test::ScopedEnv one(L"MIRADOR_DIRECT_MODE_NO_PRESENT_AT", L"1");
        CHECK_FALSE(dvl::present_at_enabled());
    }
    {
        dvl_test::ScopedEnv yes(L"MIRADOR_DIRECT_MODE_NO_PRESENT_AT", L"yes");
        CHECK_FALSE(dvl::present_at_enabled());
    }
}
