// Copyright 2026, Finn Zeruhn
// SPDX-License-Identifier: BSL-1.0

#pragma once

#include <atomic>
#include <cstdint>
#include <functional>
#include <memory>
#include <mutex>
#include <string>

#include "display_loop.hpp"

namespace nvdm {

inline constexpr int64_t kBatonWaitNs = 1'000'000'000;
inline constexpr int64_t kJoinTimeoutNs = 1'000'000'000;

class DisplayThread {
public:
    explicit DisplayThread(std::function<void(const std::string&)> log = {}, int64_t baton_wait_ns = kBatonWaitNs);
    ~DisplayThread();
    DisplayThread(const DisplayThread&) = delete;
    DisplayThread& operator=(const DisplayThread&) = delete;

    void start(std::function<std::unique_ptr<DisplayLoop>()> make_loop);

    bool stop_and_join(int64_t timeout_ns);

    uint64_t epoch() const;

    struct Epoch;
    struct Baton;

private:
    bool stop_locked(int64_t timeout_ns);
    void say(const std::string& line) const;

    std::function<void(const std::string&)> log_;
    int64_t baton_wait_ns_;
    std::shared_ptr<Baton> baton_;
    std::mutex mutex_;
    std::shared_ptr<Epoch> current_;
    std::atomic<uint64_t> epoch_{0};
};

}
