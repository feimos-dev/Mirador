# Copyright 2026, Finn Zeruhn
# SPDX-License-Identifier: BSL-1.0

add_library(nvdm STATIC
    board.cpp
    clock.cpp
    display_loop.cpp
    display_session.cpp
    display_thread.cpp
    fence_pool.cpp
    frame_model.cpp
    holders.cpp
    nvapi_api.cpp
    product_table.cpp
    edid.cpp
    stats.cpp
    trace.cpp
)

target_include_directories(nvdm PUBLIC "${PROJECT_SOURCE_DIR}")
target_compile_features(nvdm PUBLIC cxx_std_17)

target_link_libraries(nvdm PUBLIC advapi32 avrt d3d11 dxgi setupapi)
