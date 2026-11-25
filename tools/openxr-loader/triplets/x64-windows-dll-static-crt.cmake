# The OpenXR loader as a DLL with the static CRT, release only, for the desktop program. Its own dependencies (jsoncpp)
# are linked into it, so the one DLL is all that ships.
set(VCPKG_TARGET_ARCHITECTURE x64)
set(VCPKG_CRT_LINKAGE static)
set(VCPKG_LIBRARY_LINKAGE static)
if(PORT STREQUAL "openxr-loader")
    set(VCPKG_LIBRARY_LINKAGE dynamic)
endif()
set(VCPKG_BUILD_TYPE release)
