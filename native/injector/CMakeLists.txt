add_executable(AshenVoiceInjector
    main.cpp
)

target_compile_definitions(AshenVoiceInjector PRIVATE
    UNICODE
    _UNICODE
    WIN32_LEAN_AND_MEAN
    NOMINMAX
)

target_compile_options(AshenVoiceInjector PRIVATE
    /W4
    /permissive-
    /EHsc
)

set_target_properties(AshenVoiceInjector PROPERTIES
    OUTPUT_NAME "AshenVoiceInjector"
)
