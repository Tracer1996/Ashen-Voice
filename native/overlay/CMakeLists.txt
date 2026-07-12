add_library(AshenVoiceOverlay SHARED
    dllmain.cpp
)

target_compile_definitions(AshenVoiceOverlay PRIVATE
    UNICODE
    _UNICODE
    WIN32_LEAN_AND_MEAN
    NOMINMAX
)

target_compile_options(AshenVoiceOverlay PRIVATE
    /W4
    /permissive-
    /EHsc
)

target_link_libraries(AshenVoiceOverlay PRIVATE
    d3d9
    windowscodecs
    ole32
    gdi32
    user32
    minhook
)

set_target_properties(AshenVoiceOverlay PROPERTIES
    OUTPUT_NAME "AshenVoiceOverlay"
)
