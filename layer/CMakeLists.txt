# Copyright 2026, Finn Zeruhn
# SPDX-License-Identifier: BSL-1.0

cmake_minimum_required(VERSION 3.21)
project(mirador_direct_mode VERSION 0.1.0 LANGUAGES CXX)

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_CXX_EXTENSIONS OFF)

if(NOT WIN32)
    message(FATAL_ERROR "mirador-direct-mode is a Windows-only project")
endif()

if(MSVC)
    add_compile_options(/W4 /permissive- /utf-8)
endif()

set(CMAKE_MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")
add_compile_definitions(WIN32_LEAN_AND_MEAN NOMINMAX)

find_package(VulkanHeaders CONFIG REQUIRED)
find_package(Catch2 3 CONFIG REQUIRED)

foreach(dir core layer check)
    add_subdirectory(${dir})
endforeach()

enable_testing()
add_subdirectory(tests)
