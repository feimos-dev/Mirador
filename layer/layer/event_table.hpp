// Copyright 2026, Finn Zeruhn
// SPDX-License-Identifier: BSL-1.0

#pragma once

#include <vulkan/vulkan.h>

#include <cstdint>
#include <map>
#include <mutex>
#include <optional>
#include <utility>
#include <vector>

namespace dvl {

class EventTable {
public:
    void add(VkDevice device, VkFence fence, int64_t registered_ns);
    bool remove(VkDevice device, VkFence fence);
    size_t remove_device(VkDevice device);
    std::vector<VkFence> fences_of(VkDevice device) const;

    bool is_event(VkDevice device, VkFence fence) const;
    bool is_set(VkDevice device, VkFence fence) const;
    std::optional<int64_t> set_at(VkDevice device, VkFence fence) const;
    void reset(VkDevice device, VkFence fence);

    size_t on_refresh(int64_t qpc_ns);
    bool pending() const;
    size_t size() const;

    struct Split {
        std::vector<VkFence> events;
        std::vector<VkFence> ordinary;
    };
    Split split(VkDevice device, const VkFence* fences, uint32_t count) const;

private:
    struct Event {
        int64_t registered_ns = 0;
        bool fired = false;
        bool set = false;
        int64_t set_ns = 0;
    };
    using Key = std::pair<VkDevice, VkFence>;
    std::map<Key, Event> events_;
};

class FenceWaitHost {
public:
    virtual ~FenceWaitHost() = default;
    virtual int64_t now_ns() = 0;
    virtual VkResult wait_down(const std::vector<VkFence>& fences, bool wait_all, uint64_t timeout_ns) = 0;
    virtual void wait_change(std::unique_lock<std::mutex>& lock, int64_t until_ns) = 0;
};

struct EventWait {
    VkResult result = VK_TIMEOUT;
    std::optional<int64_t> set_ns;
};

inline constexpr int64_t kMixedPollNs = 1'000'000;

EventWait wait_for_fences(EventTable& table, std::unique_lock<std::mutex>& lock, VkDevice device,
                          const VkFence* fences, uint32_t count, bool wait_all, uint64_t timeout_ns,
                          FenceWaitHost& host);

}
