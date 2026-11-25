// Copyright 2026, Finn Zeruhn
// SPDX-License-Identifier: BSL-1.0

#include <catch2/catch_test_macros.hpp>

#include <cstdint>
#include <functional>
#include <optional>
#include <set>
#include <stdexcept>

#include "core/fence_pool.hpp"

namespace {

using nvdm::FencePool;
using nvdm::FenceSlot;
using nvdm::FenceState;

struct Signals {
    std::set<FenceSlot> on;
    std::function<bool(FenceSlot)> fn()
    {
        return [this](FenceSlot slot) { return on.count(slot) > 0; };
    }
};

FenceSlot must_take(FencePool& pool)
{
    std::optional<FenceSlot> slot = pool.take();
    REQUIRE(slot.has_value());
    return *slot;
}

}

TEST_CASE("take_prefers_pool_then_signalled_left_over_then_new")
{
    Signals sig;
    FencePool pool(16, sig.fn());

    const FenceSlot a = must_take(pool);
    const FenceSlot b = must_take(pool);
    REQUIRE(a != b);
    REQUIRE(pool.created() == 2);
    pool.attach(a);
    pool.attach(b);
    REQUIRE(pool.state(a) == FenceState::Attached);

    pool.waiting(a, true);
    pool.seen_signalled(a);
    pool.waiting(a, false);
    pool.frame_ended(a);
    pool.frame_ended(b);
    REQUIRE(pool.state(a) == FenceState::InPool);
    REQUIRE(pool.state(b) == FenceState::LeftOver);
    sig.on.insert(b);

    CHECK(must_take(pool) == a);
    CHECK(must_take(pool) == b);
    const FenceSlot c = must_take(pool);
    CHECK(c != a);
    CHECK(c != b);
    CHECK(pool.created() == 3);
    CHECK(pool.state(c) == FenceState::Taken);
}

TEST_CASE("left_over_never_reused_unsignalled")
{
    Signals sig;
    FencePool pool(16, sig.fn());
    const FenceSlot a = must_take(pool);
    pool.attach(a);
    pool.frame_ended(a);
    REQUIRE(pool.state(a) == FenceState::LeftOver);
    REQUIRE(pool.oldest_left_over() == a);

    const FenceSlot b = must_take(pool);
    CHECK(b != a);
    CHECK(pool.state(a) == FenceState::LeftOver);

    SECTION("reclaimed once its status says signalled")
    {
        sig.on.insert(a);
        CHECK(must_take(pool) == a);
    }
    SECTION("or back in the pool once seen signalled (the wait on the oldest left-over one)")
    {
        pool.waiting(a, true);
        pool.seen_signalled(a);
        pool.waiting(a, false);
        CHECK(pool.state(a) == FenceState::InPool);
        CHECK_FALSE(pool.oldest_left_over().has_value());
        CHECK(must_take(pool) == a);
    }
    SECTION("the oldest left-over is the one that became left over first")
    {
        pool.attach(b);
        pool.frame_ended(b);
        CHECK(pool.oldest_left_over() == a);
    }
}

TEST_CASE("waited_on_fence_not_reclaimed")
{
    Signals sig;
    FencePool pool(16, sig.fn());
    const FenceSlot a = must_take(pool);
    pool.attach(a);
    pool.waiting(a, true);
    pool.frame_ended(a);
    sig.on.insert(a);

    CHECK(must_take(pool) != a);
    CHECK_FALSE(pool.oldest_left_over().has_value());

    SECTION("seen signalled while waited on: in the pool, still not handed out")
    {
        pool.seen_signalled(a);
        CHECK(pool.state(a) == FenceState::InPool);
        CHECK(must_take(pool) != a);
        pool.waiting(a, false);
        CHECK(must_take(pool) == a);
    }
    SECTION("free to reclaim once the wait is over")
    {
        pool.waiting(a, false);
        CHECK(must_take(pool) == a);
    }
}

TEST_CASE("seen_signalled_counts_only_while_waited")
{
    Signals sig;
    FencePool pool(16, sig.fn());
    const FenceSlot a = must_take(pool);
    pool.attach(a);

    pool.seen_signalled(a);
    pool.frame_ended(a);
    CHECK(pool.state(a) == FenceState::LeftOver);
    pool.seen_signalled(a);
    CHECK(pool.state(a) == FenceState::LeftOver);
    CHECK(pool.take() != a);

    pool.waiting(a, true);
    pool.seen_signalled(a);
    pool.waiting(a, false);
    CHECK(pool.state(a) == FenceState::InPool);
    CHECK(pool.take() == a);
}

TEST_CASE("cap_is_16")
{
    FencePool pool;
    std::set<FenceSlot> seen;
    for (int i = 0; i < 16; ++i) {
        const FenceSlot slot = must_take(pool);
        CHECK(seen.insert(slot).second);
        pool.attach(slot);
        pool.frame_ended(slot);
    }
    CHECK(pool.created() == 16);
    CHECK_FALSE(pool.take().has_value());
    CHECK(pool.created() == 16);
    CHECK(pool.oldest_left_over() == FenceSlot{0});
}

TEST_CASE("fence pool: attach needs a slot from take(), and kNoFence is ignored")
{
    FencePool pool;
    const FenceSlot a = must_take(pool);
    pool.attach(a);
    CHECK_THROWS_AS(pool.attach(a), std::logic_error);
    pool.frame_ended(a);
    CHECK_THROWS_AS(pool.attach(a), std::logic_error);
    CHECK_THROWS_AS(pool.attach(FenceSlot{7}), std::logic_error);

    pool.attach(nvdm::kNoFence);
    pool.frame_ended(nvdm::kNoFence);
    pool.seen_signalled(nvdm::kNoFence);
    pool.waiting(nvdm::kNoFence, true);
    CHECK(pool.created() == 1);
}

TEST_CASE("give_back_returns_a_fence_whose_submit_failed")
{
    Signals sig;
    FencePool pool(2, sig.fn());
    const FenceSlot a = must_take(pool);
    pool.give_back(a);
    CHECK(pool.state(a) == FenceState::InPool);
    CHECK(must_take(pool) == a);
    const FenceSlot b = must_take(pool);
    pool.give_back(b);
    CHECK(must_take(pool) == b);
    CHECK(pool.created() == 2);
    CHECK_FALSE(pool.take().has_value());

    pool.attach(a);
    pool.give_back(a);
    CHECK(pool.state(a) == FenceState::Attached);
    pool.give_back(nvdm::kNoFence);
    pool.give_back(7);
}
