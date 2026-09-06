cmake_minimum_required(VERSION 3.20)
project(dvmaudio_pipewire LANGUAGES C)

find_package(PkgConfig REQUIRED)
find_package(Threads REQUIRED)
pkg_check_modules(PIPEWIRE REQUIRED IMPORTED_TARGET libpipewire-0.3)

add_library(dvmaudio_pipewire SHARED
    dvmaudio_pipewire.c
    ../dvmaudio/dvm_capture_signal.c
    ../dvmaudio/dvm_pcm_ring.c)
target_include_directories(dvmaudio_pipewire PRIVATE ../dvmaudio)
target_link_libraries(dvmaudio_pipewire PRIVATE PkgConfig::PIPEWIRE Threads::Threads)
target_compile_features(dvmaudio_pipewire PRIVATE c_std_11)
set_target_properties(dvmaudio_pipewire PROPERTIES
    OUTPUT_NAME "dvmaudio-pipewire"
    C_VISIBILITY_PRESET hidden
    VISIBILITY_INLINES_HIDDEN YES)

include(CTest)
if(BUILD_TESTING)
    add_executable(dvm_capture_signal_tests
        ../dvmaudio/dvm_capture_signal.c
        ../dvmaudio/dvm_capture_signal_tests.c)
    target_include_directories(dvm_capture_signal_tests PRIVATE ../dvmaudio)
    target_compile_features(dvm_capture_signal_tests PRIVATE c_std_11)
    target_link_libraries(dvm_capture_signal_tests PRIVATE Threads::Threads)
    add_test(NAME dvm_capture_signal_tests COMMAND dvm_capture_signal_tests)

    add_executable(dvm_pcm_ring_tests
        ../dvmaudio/dvm_pcm_ring_tests.c
        ../dvmaudio/dvm_pcm_ring.c)
    target_include_directories(dvm_pcm_ring_tests PRIVATE ../dvmaudio)
    target_compile_features(dvm_pcm_ring_tests PRIVATE c_std_11)
    add_test(NAME dvm_pcm_ring_tests COMMAND dvm_pcm_ring_tests)
endif()
