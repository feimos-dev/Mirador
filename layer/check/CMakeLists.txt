# Copyright 2026, Finn Zeruhn
# SPDX-License-Identifier: BSL-1.0

add_library(dvl_check_lib STATIC
    heartbeat.cpp
    interrupt.cpp
    nvapi_mode.cpp
    options.cpp
    report.cpp
)
target_include_directories(dvl_check_lib PUBLIC "${PROJECT_SOURCE_DIR}")
target_compile_features(dvl_check_lib PUBLIC cxx_std_17)
target_compile_definitions(dvl_check_lib PUBLIC VK_NO_PROTOTYPES)
target_link_libraries(dvl_check_lib PUBLIC nvdm Vulkan::Headers setupapi hid)

add_executable(mirador-display-check main.cpp)
target_link_libraries(mirador-display-check PRIVATE dvl_check_lib)
target_compile_definitions(mirador-display-check PRIVATE "DVL_CHECK_VERSION=\"${PROJECT_VERSION}\"")
set_target_properties(mirador-display-check PROPERTIES
    RUNTIME_OUTPUT_DIRECTORY "${CMAKE_CURRENT_BINARY_DIR}")
