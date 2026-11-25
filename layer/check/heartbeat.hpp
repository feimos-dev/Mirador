// Copyright 2026, Finn Zeruhn
// SPDX-License-Identifier: BSL-1.0

#pragma once

#include <array>
#include <chrono>
#include <cstdint>
#include <memory>
#include <string>
#include <string_view>

#include "core/board.hpp"

namespace dvl {

inline constexpr uint16_t kBoardVendorId = nvdm::kBoardVendorId;
inline constexpr uint16_t kBoardProductId = nvdm::kBoardProductId;

inline constexpr size_t kHeartbeatSize = 65;
inline constexpr std::chrono::milliseconds kHeartbeatPeriod{200};

std::array<uint8_t, kHeartbeatSize> heartbeat_report();

bool is_board_interface_path(std::string_view path);

class HeartbeatPort {
public:
    virtual ~HeartbeatPort() = default;
    virtual bool open(std::string& detail) = 0;
    virtual bool write(const std::array<uint8_t, kHeartbeatSize>& report) = 0;
    virtual void drain() = 0;
    virtual void close() = 0;
};

std::unique_ptr<HeartbeatPort> make_hid_port();

class Heartbeat {
public:
    Heartbeat();
    Heartbeat(std::unique_ptr<HeartbeatPort> port, std::chrono::milliseconds period);
    ~Heartbeat();
    Heartbeat(const Heartbeat&) = delete;
    Heartbeat& operator=(const Heartbeat&) = delete;

    bool start();
    void stop();

    bool running() const;
    uint64_t sent() const;
    uint64_t failed() const;
    std::string detail() const;

private:
    struct Impl;
    std::unique_ptr<Impl> impl_;
};

}
