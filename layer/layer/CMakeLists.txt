# Copyright 2026, Finn Zeruhn
# SPDX-License-Identifier: BSL-1.0

find_package(Python3 REQUIRED COMPONENTS Interpreter)
find_file(DVL_VK_XML vk.xml
    HINTS "${VulkanHeaders_DIR}/../../.."
    PATH_SUFFIXES share/vulkan/registry
    DOC "Vulkan registry (vk.xml) that tools/gen_handle_commands.py reads"
    REQUIRED)
set(DVL_HANDLE_COMMANDS_INC "${PROJECT_BINARY_DIR}/generated/handle_commands.inc")
add_custom_command(
    OUTPUT "${DVL_HANDLE_COMMANDS_INC}"
    COMMAND "${CMAKE_COMMAND}" -E make_directory "${PROJECT_BINARY_DIR}/generated"
    COMMAND "${Python3_EXECUTABLE}" -I "${PROJECT_SOURCE_DIR}/tools/gen_handle_commands.py"
            "${DVL_VK_XML}" "${DVL_HANDLE_COMMANDS_INC}"
    DEPENDS "${PROJECT_SOURCE_DIR}/tools/gen_handle_commands.py" "${DVL_VK_XML}"
    COMMENT "Listing the vk.xml commands that take display, mode, surface or swapchain handles"
    VERBATIM)

add_library(dvl_pure OBJECT
    device_setup.cpp
    dispatch.cpp
    display.cpp
    env.cpp
    event_table.cpp
    exit.cpp
    guard.cpp
    handled_commands.cpp
    import_rules.cpp
    log.cpp
    present_timing.cpp
    surface.cpp
    warnings.cpp
    "${DVL_HANDLE_COMMANDS_INC}"
)
target_include_directories(dvl_pure PUBLIC "${PROJECT_SOURCE_DIR}")
target_include_directories(dvl_pure PRIVATE "${PROJECT_BINARY_DIR}/generated")
target_compile_features(dvl_pure PUBLIC cxx_std_17)
target_compile_definitions(dvl_pure PUBLIC VK_NO_PROTOTYPES VK_USE_PLATFORM_WIN32_KHR)
target_link_libraries(dvl_pure PUBLIC Vulkan::Headers nvdm)

add_library(dvl_layer OBJECT
    device.cpp
    display_intercepts.cpp
    events.cpp
    import.cpp
    layer_main.cpp
    misc_intercepts.cpp
    present.cpp
    present_timing_intercepts.cpp
    surface_intercepts.cpp
    swapchain.cpp
)
target_link_libraries(dvl_layer PRIVATE dvl_pure nvdm)
target_compile_definitions(dvl_layer PRIVATE "DVL_LAYER_VERSION=\"${PROJECT_VERSION}\"")

add_library(mirador_direct_mode SHARED $<TARGET_OBJECTS:dvl_layer>)
target_link_libraries(mirador_direct_mode PRIVATE dvl_pure nvdm)
set_target_properties(mirador_direct_mode PROPERTIES
    RUNTIME_OUTPUT_DIRECTORY "${CMAKE_CURRENT_BINARY_DIR}"
    LIBRARY_OUTPUT_DIRECTORY "${CMAKE_CURRENT_BINARY_DIR}"
    ARCHIVE_OUTPUT_DIRECTORY "${CMAKE_CURRENT_BINARY_DIR}"
)

add_library(dvl_layer_for_tests SHARED $<TARGET_OBJECTS:dvl_layer> test_hooks.cpp)
target_link_libraries(dvl_layer_for_tests PRIVATE dvl_pure nvdm)
set_target_properties(dvl_layer_for_tests PROPERTIES
    RUNTIME_OUTPUT_DIRECTORY "${CMAKE_CURRENT_BINARY_DIR}"
    LIBRARY_OUTPUT_DIRECTORY "${CMAKE_CURRENT_BINARY_DIR}"
    ARCHIVE_OUTPUT_DIRECTORY "${CMAKE_CURRENT_BINARY_DIR}"
)

foreach(target mirador_direct_mode dvl_layer_for_tests)
    target_link_libraries(${target} PRIVATE delayimp)
    target_link_options(${target} PRIVATE
        /DELAYLOAD:d3d11.dll /DELAYLOAD:dxgi.dll /DELAYLOAD:avrt.dll /DELAYLOAD:setupapi.dll)
endforeach()

configure_file(mirador_direct_mode.json.in "${CMAKE_CURRENT_BINARY_DIR}/mirador_direct_mode.json" @ONLY)
