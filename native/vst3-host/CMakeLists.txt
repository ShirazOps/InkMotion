cmake_minimum_required(VERSION 3.25)
project(InkMotionVst3Host LANGUAGES CXX)

if(NOT DEFINED VST3_SDK_ROOT)
    set(VST3_SDK_ROOT "${CMAKE_CURRENT_SOURCE_DIR}/../vst3sdk")
endif()

set(SMTG_ENABLE_VST3_PLUGIN_EXAMPLES OFF CACHE BOOL "" FORCE)
set(SMTG_ENABLE_VST3_HOSTING_EXAMPLES OFF CACHE BOOL "" FORCE)
set(SMTG_ENABLE_VSTGUI_SUPPORT OFF CACHE BOOL "" FORCE)

add_subdirectory("${VST3_SDK_ROOT}" "${CMAKE_CURRENT_BINARY_DIR}/vst3sdk")

add_executable(InkMotionVst3Host main.cpp)
if(WIN32)
    target_sources(InkMotionVst3Host PRIVATE
        "${VST3_SDK_ROOT}/public.sdk/source/vst/hosting/plugprovider.cpp"
        "${VST3_SDK_ROOT}/public.sdk/source/vst/hosting/module_win32.cpp"
    )
endif()
target_compile_features(InkMotionVst3Host PRIVATE cxx_std_17)
target_include_directories(InkMotionVst3Host PRIVATE
    "${VST3_SDK_ROOT}"
    "${VST3_SDK_ROOT}/public.sdk"
)
target_link_libraries(InkMotionVst3Host PRIVATE sdk_hosting)
target_compile_definitions(InkMotionVst3Host PRIVATE NOMINMAX WIN32_LEAN_AND_MEAN)
