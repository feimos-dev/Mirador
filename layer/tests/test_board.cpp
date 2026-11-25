// Copyright 2026, Finn Zeruhn
// SPDX-License-Identifier: BSL-1.0

#include <catch2/catch_test_macros.hpp>

#include "core/board.hpp"

TEST_CASE("the board's USB and HID instance ids are recognised, in either case")
{
    CHECK(nvdm::is_board_instance_id(L"USB\\VID_34A4&PID_0044\\5&2B1E2F7&0&3"));
    CHECK(nvdm::is_board_instance_id(L"USB\\VID_34A4&PID_0044&MI_00\\6&1A2B3C4D&0&0000"));
    CHECK(nvdm::is_board_instance_id(L"HID\\VID_34A4&PID_0044&MI_00\\7&367B06A1&0&0000"));
    CHECK(nvdm::is_board_instance_id(L"usb\\vid_34a4&pid_0044\\5&2b1e2f7&0&3"));
}

TEST_CASE("other devices, other enumerators and look-alike ids are not the board")
{
    CHECK_FALSE(nvdm::is_board_instance_id(L"USB\\VID_34A4&PID_0045\\5&2B1E2F7&0&3"));
    CHECK_FALSE(nvdm::is_board_instance_id(L"USB\\VID_045E&PID_0044\\1"));
    CHECK_FALSE(nvdm::is_board_instance_id(L"USB\\VID_34A4&PID_00441\\1"));
    CHECK_FALSE(nvdm::is_board_instance_id(L"PCI\\VEN_10DE&DEV_2C05&SUBSYS_143C196E\\4"));
    CHECK_FALSE(nvdm::is_board_instance_id(L"SWD\\MMDEVAPI\\VID_34A4&PID_0044"));
    CHECK_FALSE(nvdm::is_board_instance_id(L"DISPLAY\\PVR1019\\5&991C2CF&0&UID4357"));
    CHECK_FALSE(nvdm::is_board_instance_id(L""));
    CHECK_FALSE(nvdm::is_board_instance_id(L"USB\\"));
}
