cmake_minimum_required(VERSION 3.20)
project(zf_brain C CXX)

set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_EXPORT_COMPILE_COMMANDS ON)

# Warningi-jako-błędy tylko dla NASZYCH targetów (llama.cpp i sqlite by ich nie przeszły).
if(MSVC)
  set(ZF_WARNINGS /W4 /WX)
else()
  set(ZF_WARNINGS -Wall -Wextra -Wpedantic -Werror)
endif()

# Systemowy SQLite3 jeśli jest; inaczej amalgamacja z third_party/sqlite3
# (MinGW z reguły nie ma nagłówków deweloperskich SQLite).
find_package(SQLite3 QUIET)
if(NOT SQLite3_FOUND)
  add_library(sqlite3_vendored STATIC third_party/sqlite3/sqlite3.c)
  target_include_directories(sqlite3_vendored PUBLIC
    ${CMAKE_CURRENT_SOURCE_DIR}/third_party/sqlite3
  )
  # Kod third-party: nie egzekwujemy naszych warningów-jako-błędów.
  if(MSVC)
    target_compile_options(sqlite3_vendored PRIVATE /W0 /WX-)
  else()
    target_compile_options(sqlite3_vendored PRIVATE -w)
  endif()
  add_library(SQLite::SQLite3 ALIAS sqlite3_vendored)
  message(STATUS "SQLite3: uzywam amalgamacji z third_party/sqlite3")
endif()

# db/schema.sql jest jedynym źródłem prawdy dla schematu; wbijamy go do binarki,
# żeby brain nie zależał od względnej ścieżki do repo w runtime.
file(READ ${CMAKE_CURRENT_SOURCE_DIR}/../db/schema.sql SCHEMA_SQL_CONTENT)
configure_file(
  ${CMAKE_CURRENT_SOURCE_DIR}/src/schema_embed.hpp.in
  ${CMAKE_CURRENT_BINARY_DIR}/generated/schema_embed.hpp
  @ONLY
)

add_library(zf_core STATIC
  src/config.cpp
  src/db.cpp
  src/bridge.cpp
  src/engine.cpp
  src/fallback.cpp
  src/llm.cpp
)

target_include_directories(zf_core PUBLIC
  ${CMAKE_CURRENT_SOURCE_DIR}/third_party
  ${CMAKE_CURRENT_BINARY_DIR}/generated
  ${CMAKE_CURRENT_SOURCE_DIR}/src
)

target_compile_options(zf_core PRIVATE ${ZF_WARNINGS})
target_link_libraries(zf_core PUBLIC SQLite::SQLite3)

# Etap 4: llama.cpp jako submoduł. Bez niego (albo z -DZF_WITH_LLM=OFF) brain
# buduje się dalej, a radio zostaje na szablonach fallback.
option(ZF_WITH_LLM "Buduj z llama.cpp (głos LLM)" ON)
if(ZF_WITH_LLM AND EXISTS ${CMAKE_CURRENT_SOURCE_DIR}/third_party/llama.cpp/CMakeLists.txt)
  set(LLAMA_BUILD_COMMON OFF CACHE BOOL "" FORCE)
  set(LLAMA_BUILD_TESTS OFF CACHE BOOL "" FORCE)
  set(LLAMA_BUILD_EXAMPLES OFF CACHE BOOL "" FORCE)
  set(LLAMA_BUILD_TOOLS OFF CACHE BOOL "" FORCE)
  set(LLAMA_CURL OFF CACHE BOOL "" FORCE)
  # MinGW + statyczne libgcc: OpenMP nie dolinkowuje GOMP do naszego exe — ggml
  # i tak ma własny thread pool, więc OpenMP wyłączamy.
  set(GGML_OPENMP OFF CACHE BOOL "" FORCE)
  add_subdirectory(third_party/llama.cpp EXCLUDE_FROM_ALL)
  target_link_libraries(zf_core PUBLIC llama)
  target_compile_definitions(zf_core PUBLIC ZF_WITH_LLM)
  message(STATUS "LLM: llama.cpp włączony")
else()
  message(STATUS "LLM: brak third_party/llama.cpp/CMakeLists.txt — buduję bez LLM (szablony fallback)")
endif()

add_executable(zf_brain src/main.cpp)
target_compile_options(zf_brain PRIVATE ${ZF_WARNINGS})
target_link_libraries(zf_brain PRIVATE zf_core)

# MinGW: runtime statycznie, inaczej exe uruchomiony poza CLionem pada bez
# komunikatu (STATUS_DLL_NOT_FOUND — libstdc++-6.dll itd. nie są w PATH).
# Pełny -static nie linkuje się z MinGW bundlowanym w CLion (braki w libpthread.a).
if(MINGW)
  target_link_options(zf_brain PRIVATE -static-libstdc++ -static-libgcc)
  # winpthread nie da się zlinkować statycznie w tym bundlu — kopiujemy DLL obok exe.
  get_filename_component(ZF_MINGW_BIN ${CMAKE_CXX_COMPILER} DIRECTORY)
  if(EXISTS "${ZF_MINGW_BIN}/libwinpthread-1.dll")
    add_custom_command(TARGET zf_brain POST_BUILD
      COMMAND ${CMAKE_COMMAND} -E copy_if_different
        "${ZF_MINGW_BIN}/libwinpthread-1.dll" "$<TARGET_FILE_DIR:zf_brain>"
    )
  endif()
endif()

enable_testing()
add_subdirectory(tests)
