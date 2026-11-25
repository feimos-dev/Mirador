// Copyright 2026, Finn Zeruhn
// SPDX-License-Identifier: BSL-1.0

#pragma once

#include <cstdint>

namespace nvdm {

class Clock {
public:
    virtual ~Clock() = default;
    virtual int64_t now_ns() = 0;
};

int64_t qpc_ticks_to_ns(int64_t ticks, int64_t frequency);

class QpcClock : public Clock {
public:
    QpcClock();
    int64_t now_ns() override;

private:
    int64_t frequency_;
};

}
