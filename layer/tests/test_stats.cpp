// Copyright 2026, Finn Zeruhn
// SPDX-License-Identifier: BSL-1.0

#include <catch2/catch_test_macros.hpp>

#include <cstdint>
#include <optional>
#include <string>

#include "core/fence_pool.hpp"
#include "core/frame_model.hpp"
#include "core/stats.hpp"

namespace {

using nvdm::Acquired;
using nvdm::FencePool;
using nvdm::FenceSlot;
using nvdm::FrameModel;
using nvdm::FrameModelConfig;
using nvdm::StatsWindow;
using nvdm::SwapchainGen;

constexpr int64_t kUs = 1'000;
constexpr int64_t kMs = 1'000'000;
constexpr int64_t kRefresh = 11'111'111;

struct Feeder {
    StatsWindow stats;
    FrameModel::Counters counters;
    uint32_t counter = 5000;
    int64_t t = 0;

    Feeder() { stats.on_refresh(counter, t); }

    void flip()
    {
        t += kRefresh;
        stats.on_refresh(++counter, t);
    }
    std::optional<std::string> frame()
    {
        flip();
        stats.on_counters(counters);
        stats.on_present(t + 2 * kMs);
        return stats.take_line();
    }
    int frames_to_line(int limit)
    {
        for (int i = 1; i <= limit; ++i) {
            line = frame();
            if (line) {
                return i;
            }
        }
        return -1;
    }
    std::optional<std::string> line;
};

bool starts_with(const std::string& text, const std::string& head)
{
    return text.compare(0, head.size(), head) == 0;
}

struct Drive {
    FencePool pool{16, [](FenceSlot) { return true; }};
    FrameModel model{FrameModelConfig{}, &pool};
    StatsWindow stats;
    SwapchainGen gen = 0;
    uint32_t counter = 100;
    int64_t t = 0;
    uint64_t id = 0;

    Drive()
    {
        model.thread_started(t);
        model.on_counter(counter, t);
        stats.on_refresh(counter, t);
        gen = model.add_swapchain();
    }
    void flip()
    {
        t += kRefresh;
        model.on_counter(++counter, t);
        stats.on_refresh(counter, t);
        stats.on_counters(model.counters());
    }
    void flips(int n)
    {
        for (int i = 0; i < n; ++i) {
            flip();
        }
    }
    std::optional<std::string> present()
    {
        const std::optional<Acquired> a = model.acquire(gen, t + 1 * kMs);
        REQUIRE(a.has_value());
        const std::optional<FenceSlot> fence = pool.take();
        REQUIRE(fence.has_value());
        model.queue(a->seq, ++id, *fence, t + 1 * kMs);
        model.fence_waiting(*fence, true);
        model.fence_seen(*fence);
        model.fence_waiting(*fence, false);
        REQUIRE(model.present_started(a->seq, t + 2 * kMs));
        stats.on_counters(model.counters());
        stats.on_present(t + 2 * kMs);
        model.present_returned(a->seq, 0, t + 2 * kMs + 50 * kUs);
        return stats.take_line();
    }
};

}

TEST_CASE("first_line_after_90_then_every_2700")
{
    Feeder f;
    REQUIRE_FALSE(f.frame().has_value());

    CHECK(f.frames_to_line(100) == 90);
    CHECK(f.frames_to_line(3000) == 2700);
    CHECK(f.frames_to_line(3000) == 2700);
    CHECK_FALSE(f.stats.take_line().has_value());
}

TEST_CASE("new_session_after_1s_gap")
{
    Feeder f;
    REQUIRE_FALSE(f.frame().has_value());
    REQUIRE(f.frames_to_line(100) == 90);
    for (int i = 0; i < 50; ++i) {
        REQUIRE_FALSE(f.frame().has_value());
    }

    SECTION("over 1 s without a present: a new app session, whose first 90 presents get a line")
    {
        for (int i = 0; i < 100; ++i) {
            f.flip();
        }
        REQUIRE_FALSE(f.frame().has_value());
        CHECK(f.frames_to_line(3000) == 90);
        REQUIRE(f.line.has_value());
        CHECK(starts_with(*f.line, "90 presents in 1.0 s (90.0 Hz), 90 flips (90.0 Hz),"));
    }
    SECTION("exactly 1 s without a present: the same session")
    {
        f.flip();
        f.stats.on_present(f.t + 2 * kMs);
        f.t += 1'000'000'000;
        f.stats.on_present(f.t + 2 * kMs);
        CHECK_FALSE(f.stats.take_line().has_value());
        CHECK(f.frames_to_line(3000) == 2700 - 52);
    }
}

TEST_CASE("missed_counts_gap_minus_one_ignoring_over_1s")
{
    Drive d;
    std::optional<std::string> line;
    int presents = 0;
    auto frame = [&] {
        line = d.present();
        ++presents;
        d.flip();
    };
    frame();
    frame();
    d.flips(2);
    frame();
    d.flips(44);
    frame();
    while (!line) {
        frame();
    }
    CHECK(presents == 91);
    CHECK(starts_with(*line, "90 presents in 1.5 s (59.6 Hz), 136 flips (90.0 Hz), 46 missed (33.82 %),"));

    d.flips(134);
    line.reset();
    presents = 0;
    while (!line) {
        frame();
    }
    CHECK(presents == 91);
    CHECK(starts_with(*line, "90 presents in 1.0 s (90.0 Hz), 90 flips (90.0 Hz), 0 missed (0.00 %),"));
    CHECK(d.model.counters().missed == 46);
}

TEST_CASE("line_format_matches")
{
    Feeder f;
    REQUIRE_FALSE(f.frame().has_value());
    REQUIRE(f.frames_to_line(100) == 90);

    std::optional<std::string> line;
    for (int i = 0; i < 2700 && !line; ++i) {
        f.stats.on_wake(i == 1000 ? 250 * kUs : 100 * kUs);
        line = f.frame();
    }
    REQUIRE(line.has_value());
    CHECK(*line == "2700 presents in 30.0 s (90.0 Hz), 2700 flips (90.0 Hz), 0 missed (0.00 %), 0 link drops; "
                   "dropped 0, failed 0, unconfirmed 0, reserved 0, held 0, target skips 0; event wake median 0.10 ms, max 0.25 ms");
}

TEST_CASE("stats: frame ends and link drops are the window's own")
{
    Feeder f;
    f.counters.dropped = 7;
    f.counters.link_drops = 1;
    REQUIRE_FALSE(f.frame().has_value());
    REQUIRE(f.frames_to_line(100) == 90);

    std::optional<std::string> line;
    for (int i = 0; i < 2700 && !line; ++i) {
        if (i == 10) {
            f.counters.dropped += 2;
            f.counters.failed += 1;
            f.counters.unconfirmed += 3;
            f.counters.reserved += 4;
            f.counters.link_drops += 1;
            f.counters.missed += 27;
        }
        line = f.frame();
    }
    REQUIRE(line.has_value());
    CHECK(*line == "2700 presents in 30.0 s (90.0 Hz), 2700 flips (90.0 Hz), 27 missed (1.00 %), 1 link drops; "
                   "dropped 2, failed 1, unconfirmed 3, reserved 4, held 0, target skips 0; event wake median 0.00 ms, max 0.00 ms");
}

TEST_CASE("stats: flips count the counter's increments, a link drop counting on from the new value")
{
    Feeder f;
    REQUIRE_FALSE(f.frame().has_value());
    for (int i = 0; i < 40; ++i) {
        REQUIRE_FALSE(f.frame().has_value());
    }
    f.counter = 3;
    f.stats.on_refresh(f.counter, f.t);
    f.stats.on_refresh(f.counter, f.t);
    std::optional<std::string> line;
    for (int i = 0; i < 100 && !line; ++i) {
        line = f.frame();
    }
    REQUIRE(line.has_value());
    CHECK(starts_with(*line, "90 presents in 1.0 s (90.0 Hz), 90 flips (90.0 Hz),"));
}

TEST_CASE("stats: event wake median of an even count is the mean of the middle two")
{
    Feeder f;
    REQUIRE_FALSE(f.frame().has_value());
    f.stats.on_wake(100 * kUs);
    f.stats.on_wake(400 * kUs);
    f.stats.on_wake(200 * kUs);
    f.stats.on_wake(300 * kUs);
    std::optional<std::string> line;
    for (int i = 0; i < 100 && !line; ++i) {
        line = f.frame();
    }
    REQUIRE(line.has_value());
    const std::string tail = "event wake median 0.25 ms, max 0.40 ms";
    REQUIRE(line->size() > tail.size());
    CHECK(line->substr(line->size() - tail.size()) == tail);
}
