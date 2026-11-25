// Copyright 2026, Finn Zeruhn
// SPDX-License-Identifier: BSL-1.0

#pragma once

#include <cstddef>
#include <cstdint>
#include <functional>
#include <optional>
#include <vector>

namespace nvdm {

using FenceSlot = uint32_t;
constexpr FenceSlot kNoFence = UINT32_MAX;

inline constexpr uint32_t kFencePoolCap = 16;

enum class FenceState { InPool, Taken, Attached, LeftOver };

class FencePool {
public:
    explicit FencePool(uint32_t cap = kFencePoolCap,
                       std::function<bool(FenceSlot)> is_signalled = [](FenceSlot) { return false; });

    std::optional<FenceSlot> take();
    std::optional<FenceSlot> oldest_left_over() const;
    void give_back(FenceSlot slot);

    void attach(FenceSlot slot);
    void waiting(FenceSlot slot, bool on);
    void seen_signalled(FenceSlot slot);
    void frame_ended(FenceSlot slot);

    size_t created() const;
    FenceState state(FenceSlot slot) const;

private:
    struct Slot {
        FenceState state = FenceState::Taken;
        bool seen = false;
        bool waited_on = false;
        uint64_t left_over_order = 0;
    };
    bool valid(FenceSlot slot) const;
    FenceSlot hand_out(FenceSlot slot);

    uint32_t cap_;
    std::function<bool(FenceSlot)> is_signalled_;
    std::vector<Slot> slots_;
    uint64_t left_over_count_ = 0;
};

}
