add_executable(zf_bridge_test bridge_test.cpp)
target_link_libraries(zf_bridge_test PRIVATE zf_core)

add_test(NAME bridge_test COMMAND zf_bridge_test)
