cmake_minimum_required(VERSION 3.20)
project(hook_native LANGUAGES C CXX)

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_C_STANDARD 11)

# Force x64
if(NOT CMAKE_SIZEOF_VOID_P EQUAL 8)
    message(FATAL_ERROR "This project requires x64 compilation")
endif()

# MinHook source files
set(MINHOOK_SOURCES
    minhook/src/buffer.c
    minhook/src/hook.c
    minhook/src/trampoline.c
    minhook/src/hde/hde64.c
)

# Our hook DLL
add_library(hook_native SHARED
    hook_native.cpp
    ${MINHOOK_SOURCES}
)

target_include_directories(hook_native PRIVATE
    minhook/include
    minhook/src
    minhook/src/hde
)

# Static CRT (/MT) so no runtime dependencies
set_property(TARGET hook_native PROPERTY
    MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")

# x64 only, optimize for size
target_compile_options(hook_native PRIVATE
    $<$<CXX_COMPILER_ID:MSVC>:/O2 /GS- /W3>
    $<$<C_COMPILER_ID:MSVC>:/O2 /GS- /W3>
)

# No default libraries bloat
target_link_options(hook_native PRIVATE
    /NODEFAULTLIB:msvcrt.lib
)

# Output name without lib prefix
set_target_properties(hook_native PROPERTIES
    PREFIX ""
    OUTPUT_NAME "hook_native"
)
