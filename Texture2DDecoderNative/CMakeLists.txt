cmake_minimum_required(VERSION 3.22.1)
project(Texture2DDecoderNative LANGUAGES CXX)

add_library(Texture2DDecoderNative SHARED
    astc.cpp
    atc.cpp
    bcn.cpp
    crunch.cpp
    dllmain.cpp
    etc.cpp
    pvrtc.cpp
    unitycrunch.cpp
)

target_compile_features(Texture2DDecoderNative PRIVATE cxx_std_17)
target_compile_definitions(Texture2DDecoderNative PRIVATE _T2D_DLL)
target_include_directories(Texture2DDecoderNative PRIVATE ${CMAKE_CURRENT_SOURCE_DIR})
set_target_properties(Texture2DDecoderNative PROPERTIES
    CXX_VISIBILITY_PRESET hidden
    VISIBILITY_INLINES_HIDDEN YES
    OUTPUT_NAME Texture2DDecoderNative
)
