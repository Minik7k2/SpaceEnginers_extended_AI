#pragma once

#include <cstdint>
#include <string>

struct sqlite3;

namespace zf {

// Cienki wrapper na SQLite C API. Otwiera/tworzy bazę i wykonuje db/schema.sql (wbudowany, patrz
// schema_embed.hpp) przy każdym starcie — CREATE TABLE IF NOT EXISTS jest idempotentne.
class Db {
public:
    explicit Db(const std::string& path);
    ~Db();

    Db(const Db&) = delete;
    Db& operator=(const Db&) = delete;

    // Liczba linii pliku `file` już przetworzonych przez brain (patrz docs/protocol.md: offsety w SQLite).
    std::int64_t get_line_offset(const std::string& file) const;
    void set_line_offset(const std::string& file, std::int64_t offset);

    // Rosnący licznik `seq` nadawcy dla commands.jsonl, przetrwały restart brain.
    std::int64_t next_commands_seq();

private:
    sqlite3* handle_ = nullptr;
};

} // namespace zf
