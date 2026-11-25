// Copyright 2026, Finn Zeruhn
// SPDX-License-Identifier: BSL-1.0

#include <catch2/catch_test_macros.hpp>

#include <algorithm>
#include <cstdint>
#include <functional>
#include <map>
#include <memory>
#include <optional>
#include <set>
#include <string>
#include <vector>

#include "core/fence_pool.hpp"
#include "core/frame_model.hpp"

namespace {

using nvdm::Acquired;
using nvdm::BufferState;
using nvdm::Candidate;
using nvdm::FenceSlot;
using nvdm::FenceState;
using nvdm::FencePool;
using nvdm::FrameEnd;
using nvdm::FrameModel;
using nvdm::FrameModelConfig;
using nvdm::FrameSeq;
using nvdm::FrameState;
using nvdm::kNoFence;
using nvdm::SwapchainGen;

constexpr int64_t kUs = 1'000;
constexpr int64_t kMs = 1'000'000;
constexpr int64_t kRefresh = 11'111'111;
constexpr int64_t kStep = 100 * kUs;

struct Device {
    std::map<FenceSlot, int64_t> ready_at;
    const int64_t* now;
    FencePool pool;
    explicit Device(const int64_t* clock)
        : now(clock), pool(16, [this](FenceSlot slot) { return ready(slot); })
    {
    }
    bool ready(FenceSlot slot) const
    {
        const auto it = ready_at.find(slot);
        return it != ready_at.end() && it->second <= *now;
    }
};

struct MonadoFrame {
    FrameSeq seq;
    uint64_t id;
    SwapchainGen gen;
};

struct Sim {
    int64_t t = 0;
    std::unique_ptr<Device> device;
    FrameModel m;
    std::vector<std::string> log;

    bool alive = true;
    uint32_t hw_counter = 40000;
    int64_t hw_qpc = 0;
    int64_t hw_next = kRefresh;

    bool thread_on = true;
    bool have_read = false;
    uint32_t last_read = 0;
    struct Call {
        FrameSeq seq;
        int64_t returns;
        int code;
        bool left_behind;
    };
    std::optional<Call> call;
    std::optional<FenceSlot> waiting_on;
    uint64_t presents_made = 0;
    std::function<int64_t(uint64_t)> present_ns = [](uint64_t) { return 50 * kUs; };
    std::function<int(uint64_t)> present_code = [](uint64_t) { return 0; };
    std::vector<int64_t> present_times;
    bool ever_stopped = false;

    enum class Phase { Off, Waiting, Acquiring, Rendering, Idling, PresentWaiting };
    Phase phase = Phase::Off;
    SwapchainGen gen = 0;
    bool present_wait = true;
    int64_t period = kRefresh;
    int64_t think_ns = 1 * kMs;
    int64_t cpu_ns = 1 * kMs;
    std::function<int64_t(uint64_t)> gpu_ns = [](uint64_t) { return 3 * kMs; };
    int64_t next_start = 0;
    int64_t frame_start = 0;
    int64_t queue_at = 0;
    int64_t idle_at = 0;
    int64_t wait_until = 0;
    FrameSeq seq = 0;
    uint64_t id = 0;
    uint64_t next_id = 1;
    uint64_t frames = 0;
    uint64_t stop_after = 0;
    std::vector<MonadoFrame> history;
    std::set<FrameSeq> shown_before_destroy;
    uint64_t acquire_misses = 0;
    int64_t longest_acquire_wait = 0;
    uint64_t via[4] = {};

    explicit Sim(FrameModelConfig config = {})
        : device(std::make_unique<Device>(&t)), m(config, &device->pool)
    {
        m.set_log([this](const std::string& line) { log.push_back(line); });
        check();
        thread_start();
    }

    void check() const { m.check_invariants(); }

    void thread_start()
    {
        m.thread_started(t);
        check();
        thread_on = true;
        have_read = false;
        waiting_on.reset();
    }

    void monado_start(SwapchainGen g, uint64_t count = 0)
    {
        gen = g;
        stop_after = count == 0 ? 0 : frames + count;
        phase = Phase::Waiting;
        next_start = t + think_ns;
    }

    void step()
    {
        t += kStep;
        display();
        display_thread();
        monado();
    }

    void run_until(int64_t end, const std::function<void()>& each = {})
    {
        while (t < end) {
            step();
            if (each) {
                each();
            }
        }
    }

    void step_until(const std::function<bool()>& done)
    {
        const int64_t limit = t + 60'000 * kMs;
        while (!done()) {
            REQUIRE(t < limit);
            step();
        }
    }

    void run_frames(int64_t tail = 3 * kRefresh, const std::function<void()>& each = {})
    {
        const int64_t limit = t + 120'000 * kMs;
        while (phase != Phase::Off) {
            REQUIRE(t < limit);
            step();
            if (each) {
                each();
            }
        }
        run_until(t + tail, each);
    }

    void display()
    {
        while (t >= hw_next) {
            if (alive) {
                ++hw_counter;
                hw_qpc = hw_next;
            }
            hw_next += kRefresh;
        }
    }

    void display_thread()
    {
        if (call) {
            if (t < call->returns) {
                return;
            }
            if (!call->left_behind) {
                m.present_returned(call->seq, call->code, call->returns);
                check();
            }
            call.reset();
        }
        if (!thread_on) {
            return;
        }
        if (!have_read || hw_counter != last_read) {
            m.on_counter(hw_counter, hw_qpc);
            check();
            have_read = true;
            last_read = hw_counter;
        }
        m.tick(t);
        check();
        ever_stopped = ever_stopped || m.flips_stopped();
        const std::optional<Candidate> c = m.candidate(t);
        check();
        if (waiting_on && (!c || c->fence != *waiting_on)) {
            m.fence_waiting(*waiting_on, false);
            check();
            waiting_on.reset();
        }
        if (!c) {
            return;
        }
        if (c->fence != kNoFence && !device->ready(c->fence)) {
            if (!waiting_on) {
                m.fence_waiting(c->fence, true);
                check();
                waiting_on = c->fence;
            }
            return;
        }
        if (c->fence != kNoFence) {
            if (!waiting_on) {
                m.fence_waiting(c->fence, true);
                check();
            }
            m.fence_seen(c->fence);
            m.fence_waiting(c->fence, false);
            check();
        }
        waiting_on.reset();
        const bool started = m.present_started(c->seq, t);
        check();
        REQUIRE(started);
        present_times.push_back(t);
        const uint64_t n = presents_made++;
        const int64_t duration = present_ns(n);
        call = Call{c->seq, t + duration, present_code(n), false};
        if (duration < kStep) {
            m.present_returned(call->seq, call->code, call->returns);
            check();
            call.reset();
        }
    }

    void finish_frame()
    {
        next_start = std::max(frame_start + period, t + think_ns);
        phase = stop_after != 0 && frames >= stop_after ? Phase::Off : Phase::Waiting;
    }

    void monado()
    {
        switch (phase) {
        case Phase::Off:
            return;
        case Phase::Waiting:
            if (t < next_start) {
                return;
            }
            phase = Phase::Acquiring;
            frame_start = next_start;
            [[fallthrough]];
        case Phase::Acquiring: {
            const int64_t bound = m.acquire_bound_ns();
            check();
            const std::optional<Acquired> got = m.acquire(gen, t);
            check();
            if (!got) {
                ++acquire_misses;
                REQUIRE(t < bound);
                return;
            }
            longest_acquire_wait = std::max(longest_acquire_wait, t - frame_start);
            ++via[static_cast<int>(got->via)];
            seq = got->seq;
            queue_at = t + cpu_ns;
            phase = Phase::Rendering;
            return;
        }
        case Phase::Rendering: {
            if (t < queue_at) {
                return;
            }
            const std::optional<FenceSlot> slot = device->pool.take();
            REQUIRE(slot.has_value());
            idle_at = t + gpu_ns(frames);
            device->ready_at[*slot] = idle_at;
            id = next_id++;
            m.queue(seq, id, *slot, t);
            check();
            history.push_back({seq, id, gen});
            ++frames;
            phase = Phase::Idling;
            return;
        }
        case Phase::Idling:
            if (t < idle_at) {
                return;
            }
            if (present_wait) {
                wait_until = t + period * 5 / 2;
                phase = Phase::PresentWaiting;
            } else {
                finish_frame();
            }
            return;
        case Phase::PresentWaiting: {
            const uint64_t done = m.completed_present_id(gen);
            check();
            if (done >= id || t >= wait_until) {
                finish_frame();
            }
            return;
        }
        }
    }

    void teardown()
    {
        phase = Phase::Off;
        m.remove_swapchain(gen);
        check();
        thread_on = false;
        if (waiting_on) {
            m.fence_waiting(*waiting_on, false);
            check();
            waiting_on.reset();
        }
        destroy_device();
    }

    void destroy_device()
    {
        for (const MonadoFrame& f : history) {
            if (m.frame_state(f.seq) == FrameState::OnScreen) {
                shown_before_destroy.insert(f.seq);
            }
        }
        m.device_destroyed();
        check();
    }

    SwapchainGen retry()
    {
        device = std::make_unique<Device>(&t);
        m.set_pool(&device->pool);
        check();
        const SwapchainGen g = m.add_swapchain();
        check();
        thread_start();
        return g;
    }

    void check_every_id_completed_once(bool allow_in_flight = false)
    {
        uint64_t expected = 0;
        std::map<SwapchainGen, uint64_t> newest;
        for (const MonadoFrame& f : history) {
            const std::optional<FrameState> state = m.frame_state(f.seq);
            bool completed = false;
            if (state) {
                if (*state == FrameState::OnScreen) {
                    completed = true;
                } else {
                    REQUIRE(allow_in_flight);
                }
            } else {
                const std::optional<FrameEnd> end = m.frame_end(f.seq);
                REQUIRE(end.has_value());
                completed = *end != FrameEnd::Destroyed || shown_before_destroy.count(f.seq) > 0;
            }
            if (completed) {
                ++expected;
                newest[f.gen] = std::max(newest[f.gen], f.id);
            }
        }
        CHECK(m.ids_completed() == expected);
        for (const auto& [g, id_done] : newest) {
            const uint64_t record = m.completed_present_id(g);
            CHECK((record == id_done || record == 0));
        }
        check();
    }

    bool logged(const std::string& part) const
    {
        return std::any_of(log.begin(), log.end(),
                           [&](const std::string& line) { return line.find(part) != std::string::npos; });
    }
};

}

TEST_CASE("scenario: normal 90 Hz, 2700 frames")
{
    Sim s;
    const SwapchainGen g = s.m.add_swapchain();
    s.monado_start(g, 2700);
    s.run_frames();

    const FrameModel::Counters c = s.m.counters();
    CHECK(s.frames == 2700);
    CHECK(c.presents == 2700);
    CHECK(c.missed == 0);
    CHECK(c.dropped == 0);
    CHECK(c.failed == 0);
    CHECK(c.unconfirmed == 0);
    CHECK(c.reserved == 0);
    CHECK(c.link_drops == 0);
    CHECK(s.acquire_misses == 0);
    CHECK(s.m.completed_present_id(g) == 2700);
    s.check_every_id_completed_once();
    for (size_t i = 0; i + 1 < s.history.size(); ++i) {
        REQUIRE(s.m.frame_end(s.history[i].seq) == FrameEnd::Replaced);
    }
    CHECK(s.m.frame_state(s.history.back().seq) == FrameState::OnScreen);
    CHECK(s.device->pool.created() <= 4);
}

TEST_CASE("scenario: a GPU frame late past the refresh")
{
    Sim s;
    s.gpu_ns = [](uint64_t n) { return n == 100 ? 14 * kMs : 3 * kMs; };
    const SwapchainGen g = s.m.add_swapchain();
    s.monado_start(g, 300);
    s.run_frames();

    const FrameModel::Counters c = s.m.counters();
    CHECK(c.presents == 300);
    CHECK(c.missed == 1);
    CHECK(c.dropped == 0);
    CHECK(c.failed == 0);
    CHECK(s.m.completed_present_id(g) == 300);
    s.check_every_id_completed_once();
}

TEST_CASE("scenario: Monado faster than refreshes")
{
    Sim s;
    s.present_wait = false;
    s.period = 5 * kMs;
    s.think_ns = 500 * kUs;
    s.cpu_ns = 500 * kUs;
    s.gpu_ns = [](uint64_t) { return 2 * kMs; };
    const SwapchainGen g = s.m.add_swapchain();
    s.monado_start(g);
    s.run_until(2000 * kMs);
    const FrameModel::Counters running = s.m.counters();
    CHECK(running.presents + 1 >= running.refreshes);
    s.phase = Sim::Phase::Off;
    s.run_until(s.t + 4 * kRefresh);

    const FrameModel::Counters c = s.m.counters();
    CHECK(c.missed == 0);
    CHECK(c.dropped == 0);
    CHECK(c.failed == 0);
    CHECK(c.unconfirmed == 0);
    CHECK(s.acquire_misses > 0);
    CHECK(s.longest_acquire_wait <= kRefresh + 1 * kMs);
    s.check_every_id_completed_once();
}

TEST_CASE("scenario: link drop, one 400 ms Present, then the counter reset")
{
    Sim s;
    s.present_ns = [](uint64_t n) { return n == 100 ? 400 * kMs : 50 * kUs; };
    const SwapchainGen g = s.m.add_swapchain();
    s.monado_start(g, 300);
    bool down = false;
    bool back = false;
    int64_t back_at = 0;
    s.run_frames(3 * kRefresh, [&] {
        if (!down && s.presents_made == 101) {
            REQUIRE(s.call.has_value());
            s.alive = false;
            back_at = s.call->returns;
            down = true;
        }
        if (down && !back && s.t >= back_at) {
            s.hw_counter = 0;
            s.hw_qpc = s.t;
            s.alive = true;
            s.hw_next = s.t + kRefresh;
            back = true;
        }
        REQUIRE_FALSE(s.m.flips_stopped());
    });

    const FrameModel::Counters c = s.m.counters();
    CHECK(back);
    CHECK(c.link_drops == 1);
    CHECK(s.logged("link drop"));
    CHECK(c.dropped == 0);
    CHECK(c.failed == 0);
    CHECK(c.unconfirmed == 0);
    CHECK(c.reserved == 0);
    CHECK(c.presents == 300);
    CHECK(s.m.completed_present_id(g) == 300);
    s.check_every_id_completed_once();
}

TEST_CASE("scenario: dead display with an outstanding Present")
{
    Sim s;
    const SwapchainGen g = s.m.add_swapchain();
    s.monado_start(g);
    s.step_until([&] { return s.presents_made == 101; });
    REQUIRE_FALSE(s.call.has_value());
    s.alive = false;
    const uint64_t presents_at_death = s.presents_made;
    const uint64_t dropped_before = s.m.counters().dropped;
    s.run_until(s.t + 2000 * kMs, [&] {
        REQUIRE(s.presents_made == presents_at_death);
    });
    CHECK(s.ever_stopped);
    CHECK(s.m.flips_stopped());
    CHECK(s.logged("flips stopped"));
    CHECK(s.m.counters().dropped > dropped_before + 50);
    CHECK(s.longest_acquire_wait <= 610 * kMs);
    CHECK(s.m.counters().unconfirmed == 0);

    s.alive = true;
    s.run_until(s.t + 500 * kMs);
    CHECK_FALSE(s.m.flips_stopped());
    CHECK(s.logged("flips resumed"));
    CHECK(s.presents_made > presents_at_death + 30);
    s.phase = Sim::Phase::Off;
    s.run_until(s.t + 100 * kMs);
    CHECK(s.m.counters().reserved == 0);
    s.check_every_id_completed_once();
}

TEST_CASE("scenario: dead display without an outstanding Present")
{
    Sim s;
    const SwapchainGen g = s.m.add_swapchain();
    s.monado_start(g, 100);
    s.run_frames(500 * kMs);
    s.alive = false;
    s.run_until(s.t + 1000 * kMs);
    CHECK(s.m.flips_stopped());
    const uint64_t presents_at_stop = s.presents_made;

    s.monado_start(g);
    s.run_until(s.t + 1000 * kMs);
    CHECK(s.presents_made == presents_at_stop + 1);
    CHECK(s.m.counters().dropped > 50);

    s.alive = true;
    s.run_until(s.t + 500 * kMs);
    CHECK_FALSE(s.m.flips_stopped());
    CHECK(s.presents_made > presents_at_stop + 30);
    s.phase = Sim::Phase::Off;
    s.run_until(s.t + 100 * kMs);
    CHECK(s.m.counters().failed == 0);
    s.check_every_id_completed_once();
}

TEST_CASE("scenario: a non-zero Present")
{
    Sim s;
    s.present_code = [](uint64_t n) { return n == 100 ? 5 : 0; };
    const SwapchainGen g = s.m.add_swapchain();
    s.monado_start(g, 300);
    s.run_frames();

    const FrameModel::Counters c = s.m.counters();
    CHECK(c.failed == 1);
    CHECK(c.reserved == 1);
    CHECK(c.missed <= 1);
    CHECK(c.dropped == 0);
    CHECK(c.presents == 300);
    REQUIRE(s.present_times.size() > 101);
    const int64_t failed_at = s.present_times[100];
    const int64_t next_refresh = (failed_at / kRefresh + 1) * kRefresh;
    CHECK(s.present_times[101] >= next_refresh);
    for (uint32_t b = 0; b < 3; ++b) {
        CHECK(s.m.buffer_state(b) != BufferState::Reserved);
    }
    CHECK(s.m.completed_present_id(g) == 300);
    s.check_every_id_completed_once();
}

TEST_CASE("scenario: a left-behind thread, its return time never written")
{
    Sim s;
    s.present_ns = [](uint64_t n) { return n == 100 ? 3000 * kMs : 50 * kUs; };
    const SwapchainGen g1 = s.m.add_swapchain();
    s.monado_start(g1);
    s.step_until([&] { return s.presents_made == 101; });
    s.run_until(s.t + 5 * kMs);
    REQUIRE(s.call.has_value());
    const FrameSeq stuck = s.call->seq;
    const int64_t old_exit = s.call->returns;

    s.phase = Sim::Phase::Off;
    s.m.remove_swapchain(g1);
    s.check();
    s.call->left_behind = true;
    s.thread_on = false;
    if (s.waiting_on) {
        s.m.fence_waiting(*s.waiting_on, false);
        s.waiting_on.reset();
    }
    s.run_until(s.t + 1000 * kMs);
    s.destroy_device();
    CHECK(s.m.frame_end(stuck) == FrameEnd::Destroyed);

    const SwapchainGen g2 = s.retry();
    REQUIRE(old_exit > s.t);
    s.monado_start(g2, 200);
    s.run_frames();

    const FrameModel::Counters c = s.m.counters();
    CHECK(c.reserved == 1);
    CHECK(c.unconfirmed == 0);
    CHECK(s.logged("not credited"));
    CHECK(s.m.completed_present_id(g1) == 0);
    CHECK(s.m.completed_present_id(g2) == s.history.back().id);
    s.check_every_id_completed_once();
}

TEST_CASE("scenario: recreate with oldSwapchain")
{
    Sim s;
    const SwapchainGen g1 = s.m.add_swapchain();
    s.monado_start(g1);
    SwapchainGen g2 = 0;
    FrameSeq last_old = 0;
    s.run_until(150 * kRefresh, [&] {
        if (g2 == 0 && s.frames == 100 && s.phase == Sim::Phase::Idling) {
            last_old = s.seq;
            g2 = s.m.add_swapchain();
            s.check();
            s.m.remove_swapchain(g1);
            s.check();
            s.gen = g2;
            s.phase = Sim::Phase::Waiting;
            s.next_start = s.t + s.think_ns;
        }
    });
    REQUIRE(g2 != 0);
    s.phase = Sim::Phase::Off;
    s.run_until(s.t + 3 * kRefresh);

    CHECK(s.m.frame_end(last_old).has_value());
    CHECK(s.m.completed_present_id(g1) == 0);
    CHECK(s.m.completed_present_id(g2) == s.history.back().id);
    CHECK(s.frames > 140);
    CHECK(s.m.counters().failed == 0);
    s.check_every_id_completed_once();
}

TEST_CASE("scenario: device destroyed during a Present")
{
    Sim s;
    s.present_ns = [](uint64_t n) { return n == 100 ? 5 * kMs : 50 * kUs; };
    const SwapchainGen g1 = s.m.add_swapchain();
    s.monado_start(g1);
    FrameSeq during = 0;
    s.run_until(150 * kRefresh, [&] {
        if (during == 0 && s.presents_made == 101 && s.call) {
            during = s.call->seq;
            s.teardown();
        }
    });
    REQUIRE(during != 0);
    CHECK(s.m.frame_end(during) == FrameEnd::Destroyed);
    uint32_t on_screen = 3;
    for (uint32_t b = 0; b < 3; ++b) {
        if (s.m.buffer_state(b) == BufferState::OnScreen) {
            on_screen = b;
        }
    }
    REQUIRE(on_screen < 3);
    CHECK(s.m.completed_present_id(g1) == 0);

    const SwapchainGen g2 = s.retry();
    s.monado_start(g2, 100);
    s.run_frames();
    CHECK(s.m.completed_present_id(g2) == s.history.back().id);
    CHECK(s.m.counters().reserved == 0);
    s.check_every_id_completed_once();
}

TEST_CASE("scenario: a retry with a new pool")
{
    Sim s;
    const SwapchainGen g1 = s.m.add_swapchain();
    s.monado_start(g1, 50);
    s.run_frames();
    s.teardown();
    const Device* old = s.device.get();
    std::vector<FenceState> old_states;
    for (FenceSlot slot = 0; slot < old->pool.created(); ++slot) {
        old_states.push_back(old->pool.state(slot));
    }
    std::unique_ptr<Device> keep = std::move(s.device);

    const SwapchainGen g2 = s.retry();
    s.monado_start(g2, 50);
    s.run_frames();

    CHECK(s.device->pool.created() > 0);
    REQUIRE(keep->pool.created() == old_states.size());
    for (FenceSlot slot = 0; slot < keep->pool.created(); ++slot) {
        CHECK(keep->pool.state(slot) == old_states[slot]);
    }
    CHECK(s.m.completed_present_id(g2) == s.history.back().id);
    CHECK(s.m.counters().dropped == 0);
    s.check_every_id_completed_once();
}

TEST_CASE("scenario: a 5 s gap with no app session, then acquire at session start does not drop")
{
    Sim s;
    const SwapchainGen g = s.m.add_swapchain();
    s.monado_start(g, 100);
    s.run_frames(5000 * kMs);
    REQUIRE(s.t - s.m.last_credited_ns() > 4900 * kMs);
    REQUIRE(s.t - s.m.last_refresh_ns() < kRefresh);

    s.present_wait = false;
    s.period = 2 * kMs;
    s.think_ns = 200 * kUs;
    s.cpu_ns = 200 * kUs;
    s.gpu_ns = [](uint64_t) { return 1 * kMs; };
    const uint64_t misses = s.acquire_misses;
    s.monado_start(g, 20);
    s.run_frames();

    CHECK(s.acquire_misses > misses);
    CHECK(s.m.counters().dropped == 0);
    CHECK(s.longest_acquire_wait <= kRefresh + 1 * kMs);
    s.check_every_id_completed_once();
}
