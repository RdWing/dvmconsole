cmake_minimum_required(VERSION 3.20)

project(dvmaudio LANGUAGES C)

set(CMAKE_C_STANDARD 11)
set(CMAKE_C_STANDARD_REQUIRED ON)

add_library(dvmaudio SHARED dvmaudio.c)
target_compile_options(dvmaudio PRIVATE -Wall -Wextra -Wpedantic)
target_link_libraries(dvmaudio PRIVATE
    "-framework AudioToolbox"
    "-framework AudioUnit"
    "-framework CoreAudio"
    "-framework CoreFoundation")
set_target_properties(dvmaudio PROPERTIES OUTPUT_NAME "dvmaudio")
