cmake_minimum_required(VERSION 3.16)
project(l2d_render CXX)

# The Cubism SDK for Native is not part of this plugin: each user downloads it from Live2D, agreeing to its
# licence, into sdk/. setup.ps1 points CUBISM_SDK at it.
set(CUBISM_SDK "" CACHE PATH "Unpacked Cubism SDK for Native")
if(NOT EXISTS "${CUBISM_SDK}/Core/include/Live2DCubismCore.h")
  message(FATAL_ERROR "CUBISM_SDK must point at an unpacked Cubism SDK for Native")
endif()

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_MSVC_RUNTIME_LIBRARY "MultiThreaded")
add_compile_options(/utf-8)

# The newest Visual Studio toolset the SDK has a Core library for (later compilers link it as well).
foreach(toolset 143 142 141)
  if(EXISTS "${CUBISM_SDK}/Core/lib/windows/x86_64/${toolset}/Live2DCubismCore_MT.lib")
    set(CORE_LIBRARY "${CUBISM_SDK}/Core/lib/windows/x86_64/${toolset}/Live2DCubismCore_MT.lib")
    break()
  endif()
endforeach()
if(NOT CORE_LIBRARY)
  message(FATAL_ERROR "No Live2DCubismCore_MT.lib for x86_64 in ${CUBISM_SDK}/Core/lib/windows")
endif()

add_library(Live2DCubismCore STATIC IMPORTED)
set_target_properties(Live2DCubismCore PROPERTIES
  IMPORTED_LOCATION ${CORE_LIBRARY}
  INTERFACE_INCLUDE_DIRECTORIES ${CUBISM_SDK}/Core/include)

set(FRAMEWORK_SOURCE D3D11)
add_subdirectory(${CUBISM_SDK}/Framework ${CMAKE_CURRENT_BINARY_DIR}/Framework)
target_link_libraries(Framework Live2DCubismCore d3d11 d3dcompiler)
# The framework compiles its shaders on every start; main.cpp keeps the results between starts.
target_compile_definitions(Framework PRIVATE D3DCompile=CachedD3DCompile)

add_executable(l2d-render main.cpp)
target_compile_options(l2d-render PRIVATE /MP)
target_link_libraries(l2d-render Framework dxgi windowscodecs ole32 winmm psapi shlwapi)

add_custom_command(TARGET l2d-render POST_BUILD
  COMMAND ${CMAKE_COMMAND} -E copy_directory
    ${CUBISM_SDK}/Framework/src/Rendering/D3D11/Shaders
    $<TARGET_FILE_DIR:l2d-render>/FrameworkShaders)
