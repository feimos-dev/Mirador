# Copyright 2026, Finn Zeruhn
# SPDX-License-Identifier: BSL-1.0
#
# The Khronos OpenXR loader as a DLL (openxr_loader.dll in the build folder), for the desktop program
# (Mirador.Xr.exe --desktop), whose bindings load it by name. vcpkg builds it with this folder's triplet: a DLL with the
# static CRT, so nothing needs the Visual C++ Redistributable. Built with tools\build-cmake.ps1.

cmake_minimum_required(VERSION 3.21)
project(mirador_openxr_loader VERSION 0.1.0 LANGUAGES CXX)

# vcpkg's package has only a release build (the triplet's VCPKG_BUILD_TYPE).
set(CMAKE_MAP_IMPORTED_CONFIG_RELWITHDEBINFO Release)
find_package(OpenXR CONFIG REQUIRED)

add_custom_target(openxr_loader_dll ALL
    COMMAND ${CMAKE_COMMAND} -E copy_if_different $<TARGET_FILE:OpenXR::openxr_loader> ${CMAKE_BINARY_DIR}/openxr_loader.dll)
