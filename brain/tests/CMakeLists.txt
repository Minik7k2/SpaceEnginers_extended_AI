# Testy opierają się na assert(), a Release definiuje NDEBUG — bez -UNDEBUG binarka
# testowa w Release przechodziłaby "zielono", nie sprawdzając NICZEGO. Asserty
# włączamy więc jawnie w każdej konfiguracji.
function(zf_add_test name source)
  add_executable(${name} ${source})
  target_compile_options(${name} PRIVATE ${ZF_WARNINGS})
  if(MSVC)
    target_compile_options(${name} PRIVATE /UNDEBUG)
  else()
    target_compile_options(${name} PRIVATE -UNDEBUG)
  endif()
  target_link_libraries(${name} PRIVATE zf_core)
  add_test(NAME ${name} COMMAND ${name})
endfunction()

zf_add_test(bridge_test bridge_test.cpp)
zf_add_test(engine_test engine_test.cpp)
# Sanityzacja wyjścia modelu — czysta logika tekstowa, działa też w buildzie bez llama.cpp.
zf_add_test(llm_test llm_test.cpp)
zf_add_test(config_test config_test.cpp)
