# Copyright 2026, Finn Zeruhn
# SPDX-License-Identifier: BSL-1.0

cmake_minimum_required(VERSION 3.21)
project(mirador_openxr_loader VERSION 0.1.0 LANGUAGES CXX)

set(CMAKE_MAP_IMPORTED_CONFIG_RELWITHDEBINFO Release)
find_package(OpenXR CONFIG REQUIRED)

add_custom_target(openxr_loader_dll ALL
    COMMAND ${CMAKE_COMMAND} -E copy_if_different $<TARGET_FILE:OpenXR::openxr_loader> ${CMAKE_BINARY_DIR}/openxr_loader.dll)
