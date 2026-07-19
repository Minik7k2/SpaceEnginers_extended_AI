cmake_minimum_required(VERSION 3.20)
project(zf_brain C CXX)

set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_EXPORT_COMPILE_COMMANDS ON)

if(MSVC)
  add_compile_options(/W4 /WX)
else()
  add_compile_options(-Wall -Wextra -Wpedantic -Werror)
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
)

target_include_directories(zf_core PUBLIC
  ${CMAKE_CURRENT_SOURCE_DIR}/third_party
  ${CMAKE_CURRENT_BINARY_DIR}/generated
  ${CMAKE_CURRENT_SOURCE_DIR}/src
)

target_link_libraries(zf_core PUBLIC SQLite::SQLite3)

add_executable(zf_brain src/main.cpp)
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
