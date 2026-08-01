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

# Scenariusze: zdarzenia z gry + OCZEKIWANIA w jednym pliku JSONL (patrz scenariusz.hpp).
# Pokrywają brainową połowę mechanik, których do tej pory pilnowały wyłącznie ręczne
# testy w grze — ceny, okup i kontrakty. zf_brain wraca kodem 1, gdy choć jedno
# oczekiwanie nie wyjdzie, więc ctest wystarczy uruchomienie binarki.
# WORKING_DIRECTORY = brain/, bo config i persony mają ścieżki względne do niego.
file(GLOB ZF_SCENARIUSZE ${CMAKE_CURRENT_SOURCE_DIR}/scenariusze/*.jsonl)
foreach(plik ${ZF_SCENARIUSZE})
  get_filename_component(nazwa ${plik} NAME_WE)
  add_test(NAME scenariusz_${nazwa}
           COMMAND $<TARGET_FILE:zf_brain> --replay ${plik}
           WORKING_DIRECTORY ${CMAKE_CURRENT_SOURCE_DIR}/..)
endforeach()
