add_executable(zf_bridge_test bridge_test.cpp)
target_compile_options(zf_bridge_test PRIVATE ${ZF_WARNINGS})
target_link_libraries(zf_bridge_test PRIVATE zf_core)

add_test(NAME bridge_test COMMAND zf_bridge_test)

add_executable(zf_engine_test engine_test.cpp)
target_compile_options(zf_engine_test PRIVATE ${ZF_WARNINGS})
target_link_libraries(zf_engine_test PRIVATE zf_core)

add_test(NAME engine_test COMMAND zf_engine_test)
