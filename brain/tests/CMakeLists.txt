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

# Strażnicy długu technicznego: pilnują, żeby rzeczy ŚWIADOMIE wyłączone (typy zleceń bez
# drugiej połowy implementacji) nie wróciły do gry przez jedną cyfrę w configu. Test czyta
# PRAWDZIWY rules.toml — o wartościach wpisanych w samym teście nie dowiedzielibyśmy się
# niczego o tym, co dostaje gracz. Ścieżka idzie przez definicję, a nie WORKING_DIRECTORY,
# bo ma być odporna na to, skąd ctest zostanie uruchomiony.
zf_add_test(dlug_test dlug_test.cpp)
target_compile_definitions(dlug_test PRIVATE
  ZF_RULES_TOML="${CMAKE_CURRENT_SOURCE_DIR}/../configs/rules.toml")

# Scenariusze: zdarzenia z gry + OCZEKIWANIA w jednym pliku JSONL (patrz scenariusz.hpp).
# Pokrywają brainową połowę mechanik, których do tej pory pilnowały wyłącznie ręczne
# testy w grze — ceny, okup i kontrakty. zf_brain wraca kodem 1, gdy choć jedno
# oczekiwanie nie wyjdzie, więc ctest wystarczy uruchomienie binarki.
# WORKING_DIRECTORY = brain/, bo config i persony mają ścieżki względne do niego.
# CONFIGURE_DEPENDS: bez tego glob liczy się TYLKO przy konfiguracji, więc świeżo dopisany
# scenariusz nie pojawiał się w ctest aż do ręcznego `cmake -S brain -B build`. Łatwo tego
# nie zauważyć i uznać, że nowy test przechodzi — a on się w ogóle nie uruchamiał.
file(GLOB ZF_SCENARIUSZE CONFIGURE_DEPENDS ${CMAKE_CURRENT_SOURCE_DIR}/scenariusze/*.jsonl)
# Pusty glob to CISZA, nie błąd — i właśnie dlatego ta warstwa raz już zniknęła
# niezauważona (globalne `*.jsonl` w .gitignore zjadło scenariusze, CI dalej było zielone,
# bo zero testów przechodzi w 100%). Brak plików musi wywalić KONFIGURACJĘ, nie zostawić
# zielonego przebiegu bez pokrycia.
if(NOT ZF_SCENARIUSZE)
  message(FATAL_ERROR
    "Brak scenariuszy w ${CMAKE_CURRENT_SOURCE_DIR}/scenariusze/*.jsonl. "
    "Jeśli to nowy klon: sprawdź, czy .gitignore ma wyjątek "
    "`!brain/tests/scenariusze/*.jsonl` — bez niego pliki nie trafiają do repo.")
endif()
foreach(plik ${ZF_SCENARIUSZE})
  get_filename_component(nazwa ${plik} NAME_WE)
  add_test(NAME scenariusz_${nazwa}
           COMMAND $<TARGET_FILE:zf_brain> --replay ${plik}
           WORKING_DIRECTORY ${CMAKE_CURRENT_SOURCE_DIR}/..)
endforeach()
