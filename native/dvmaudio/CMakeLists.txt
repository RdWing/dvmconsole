cmake_minimum_required(VERSION 3.20)

project(dvmaudio LANGUAGES C OBJC)

set(CMAKE_C_STANDARD 11)
set(CMAKE_C_STANDARD_REQUIRED ON)

add_library(dvmaudio SHARED
    dvmaudio.c
    high_quality_bluetooth.m)
set_source_files_properties(high_quality_bluetooth.m PROPERTIES
    COMPILE_OPTIONS "-fobjc-arc;-fblocks")
target_compile_options(dvmaudio PRIVATE -Wall -Wextra -Wpedantic)
target_link_libraries(dvmaudio PRIVATE
    "-framework AudioToolbox"
    "-framework AudioUnit"
    "-framework AVFAudio"
    "-framework CoreAudio"
    "-framework CoreFoundation"
    "-framework Foundation")
set_target_properties(dvmaudio PROPERTIES OUTPUT_NAME "dvmaudio")
