if (NOT HAVE_SPLASH)
  message(FATAL_ERROR "XPDF_BUILD_WINUI requires FreeType and the Xpdf Splash renderer. Set FREETYPE_DIR or the FREETYPE_INCLUDE_DIR_* and FREETYPE_LIBRARY variables.")
endif ()
if (NOT USE_EXCEPTIONS)
  message(FATAL_ERROR "XPDF_BUILD_WINUI requires USE_EXCEPTIONS=ON so native errors cannot terminate the managed application.")
endif ()

add_library(xpdf_winui_native SHARED
  $<TARGET_OBJECTS:goo_objs>
  $<TARGET_OBJECTS:fofi_objs>
  $<TARGET_OBJECTS:splash_objs>
  xpdf_winui_native.cc
  $<TARGET_OBJECTS:xpdf_objs>
  "${PROJECT_SOURCE_DIR}/xpdf/ShadingImage.cc"
  "${PROJECT_SOURCE_DIR}/xpdf/SplashOutputDev.cc"
  "${PROJECT_SOURCE_DIR}/xpdf/TextOutputDev.cc"
)
target_compile_features(xpdf_winui_native PRIVATE cxx_std_11)
target_compile_definitions(xpdf_winui_native PRIVATE XPDF_WINUI_NATIVE_EXPORTS)
target_include_directories(xpdf_winui_native PRIVATE
  "${PROJECT_SOURCE_DIR}"
  "${PROJECT_BINARY_DIR}"
  "${PROJECT_SOURCE_DIR}/goo"
  "${PROJECT_SOURCE_DIR}/fofi"
  "${PROJECT_SOURCE_DIR}/splash"
  "${PROJECT_SOURCE_DIR}/xpdf"
  ${FREETYPE_INCLUDE_DIRS}
)
if (HAVE_LCMS)
  target_include_directories(xpdf_winui_native PRIVATE "${LCMS_INCLUDE_DIR}")
endif ()
target_link_libraries(xpdf_winui_native PRIVATE
  ${PAPER_LIBRARY}
  ${FREETYPE_LIBRARY} ${FREETYPE_OTHER_LIBS}
  ${DTYPE_LIBRARY}
  ${LCMS_LIBRARY}
  ${FONTCONFIG_LIBRARY}
  ${CMAKE_THREAD_LIBS_INIT}
)
if (WIN32)
  target_link_libraries(xpdf_winui_native PRIVATE shell32 ole32 uuid advapi32 gdi32 user32)
  target_compile_definitions(xpdf_winui_native PRIVATE NOMINMAX WIN32_LEAN_AND_MEAN)
endif ()
set_target_properties(xpdf_winui_native PROPERTIES
  PREFIX ""
  OUTPUT_NAME "xpdf_winui_native"
  CXX_VISIBILITY_PRESET hidden
  VISIBILITY_INLINES_HIDDEN YES
)

include(CTest)
if (BUILD_TESTING)
  add_executable(xpdf_winui_native_tests tests/native_tests.cc)
  target_compile_features(xpdf_winui_native_tests PRIVATE cxx_std_11)
  target_include_directories(xpdf_winui_native_tests PRIVATE "${CMAKE_CURRENT_SOURCE_DIR}")
  target_link_libraries(xpdf_winui_native_tests PRIVATE xpdf_winui_native ${CMAKE_THREAD_LIBS_INIT})
  if (WIN32)
    target_link_libraries(xpdf_winui_native_tests PRIVATE bcrypt)
  endif ()
  add_test(NAME xpdf_winui_native_smoke COMMAND xpdf_winui_native_tests)
endif ()
