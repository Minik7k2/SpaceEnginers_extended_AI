cmake_minimum_required(VERSION 3.20)
project(zf_brain C CXX)

set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_EXPORT_COMPILE_COMMANDS ON)

# Warningi-jako-błędy tylko dla NASZYCH targetów (llama.cpp i sqlite by ich nie przeszły).
# ZF_WERROR domyślnie ON (konwencja projektu); wyłączamy tylko przy rozruchu nowego
# toolchainu (np. pierwszy build MSVC/CUDA), gdzie MSVC ostrzega o innych rzeczach niż GCC.
option(ZF_WERROR "Traktuj ostrzeżenia jako błędy" ON)
if(MSVC)
  set(ZF_WARNINGS /W4)
  if(ZF_WERROR)
    list(APPEND ZF_WARNINGS /WX)
  endif()
else()
  set(ZF_WARNINGS -Wall -Wextra -Wpedantic)
  if(ZF_WERROR)
    list(APPEND ZF_WARNINGS -Werror)
  endif()
endif()

# llama.cpp deklaruje cxx_std_17 ("don't bump"), ale nasz CMAKE_CXX_STANDARD=20
# przecieka do jego targetów. W C++20 literały u8"" (makro LU8 w llama-chat.cpp)
# mają typ char8_t[], którego MSVC nie konwertuje na char* (GCC/MinGW to przełyka).
# /Zc:char8_t- przywraca zachowanie z C++17 (u8"" => char[]). Tylko dla CXX —
# nvcc nie rozumie tej flagi, więc plików CUDA nie dotykamy.
if(MSVC)
  add_compile_options($<$<COMPILE_LANGUAGE:CXX>:/Zc:char8_t->)
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

# Build GPU (MSVC/CUDA): llama.cpp jest budowany jako DLL-e, a CUDA linkuje
# cublas/cudart dynamicznie. Kopiujemy zależne DLL-e obok zf_brain.exe, żeby
# brain ruszał bez ustawiania PATH (z CLion, konsoli, gdziekolwiek). Uwaga:
# cublasLt64_*.dll potrafi ważyć setki MB (CUDA 13).
if(GGML_CUDA)
  # ggml*.dll, llama.dll i pozostałe zależności runtime targetu
  add_custom_command(TARGET zf_brain POST_BUILD
    COMMAND ${CMAKE_COMMAND} -E copy_if_different
      $<TARGET_RUNTIME_DLLS:zf_brain> $<TARGET_FILE_DIR:zf_brain>
    COMMAND_EXPAND_LISTS
  )
  # runtime CUDA (cudart/cublas/cublasLt) — importowane targety CUDA::* są w
  # zasięgu podkatalogu llama.cpp i niewidoczne tutaj, a TARGET_RUNTIME_DLLS ich
  # nie łapie. Namierzamy katalog toolkitu z nvcc (CMAKE_CUDA_COMPILER) i kopiujemy
  # DLL-e globem — obsługuje bin/ i bin/x64 (CUDA 13) oraz dowolny sufiks wersji.
  get_filename_component(_zf_cuda_bin "${CMAKE_CUDA_COMPILER}" DIRECTORY)
  set(_zf_cuda_dlls "")
  foreach(_dir "${_zf_cuda_bin}" "${_zf_cuda_bin}/x64")
    foreach(_lib cudart64 cublas64 cublasLt64)
      file(GLOB _zf_found "${_dir}/${_lib}_*.dll")
      list(APPEND _zf_cuda_dlls ${_zf_found})
    endforeach()
  endforeach()
  if(_zf_cuda_dlls)
    add_custom_command(TARGET zf_brain POST_BUILD
      COMMAND ${CMAKE_COMMAND} -E copy_if_different
        ${_zf_cuda_dlls} $<TARGET_FILE_DIR:zf_brain>
      COMMAND_EXPAND_LISTS
    )
    message(STATUS "ZF: runtime CUDA obok exe: ${_zf_cuda_dlls}")
  else()
    message(WARNING "ZF: nie znaleziono runtime CUDA (cudart/cublas) w ${_zf_cuda_bin} — brain nie ruszy bez CUDA w PATH")
  endif()
endif()

enable_testing()
add_subdirectory(tests)
