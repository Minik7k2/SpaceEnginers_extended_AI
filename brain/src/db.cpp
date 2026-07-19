#include "db.hpp"

#include <filesystem>
#include <sqlite3.h>
#include <stdexcept>

#include "schema_embed.hpp"

namespace zf {

namespace {

constexpr const char* kCommandsSeqKey = "__commands_seq__";

void exec_or_throw(sqlite3* handle, const std::string& sql) {
    char* err_msg = nullptr;
    const int rc = sqlite3_exec(handle, sql.c_str(), nullptr, nullptr, &err_msg);
    if (rc != SQLITE_OK) {
        std::string msg = err_msg != nullptr ? err_msg : "nieznany błąd sqlite";
        sqlite3_free(err_msg);
        throw std::runtime_error("sqlite: " + msg);
    }
}

} // namespace

Db::Db(const std::string& path) {
    const std::filesystem::path fs_path(path);
    if (fs_path.has_parent_path()) {
        std::filesystem::create_directories(fs_path.parent_path());
    }

    if (sqlite3_open(path.c_str(), &handle_) != SQLITE_OK) {
        const std::string err = handle_ != nullptr ? sqlite3_errmsg(handle_) : "brak handle";
        throw std::runtime_error("nie można otworzyć bazy " + path + ": " + err);
    }

    exec_or_throw(handle_, "PRAGMA journal_mode=WAL;");
    exec_or_throw(handle_, kSchemaSql);
}

Db::~Db() {
    if (handle_ != nullptr) {
        sqlite3_close(handle_);
    }
}

std::int64_t Db::get_line_offset(const std::string& file) const {
    sqlite3_stmt* stmt = nullptr;
    const char* sql = "SELECT line_offset FROM bridge_state WHERE file = ?;";
    if (sqlite3_prepare_v2(handle_, sql, -1, &stmt, nullptr) != SQLITE_OK) {
        throw std::runtime_error("sqlite prepare (get_line_offset) nieudane");
    }
    sqlite3_bind_text(stmt, 1, file.c_str(), -1, SQLITE_TRANSIENT);

    std::int64_t result = 0;
    if (sqlite3_step(stmt) == SQLITE_ROW) {
        result = sqlite3_column_int64(stmt, 0);
    }
    sqlite3_finalize(stmt);
    return result;
}

void Db::set_line_offset(const std::string& file, std::int64_t offset) {
    const char* sql =
        "INSERT INTO bridge_state (file, line_offset) VALUES (?, ?) "
        "ON CONFLICT(file) DO UPDATE SET line_offset = excluded.line_offset;";
    sqlite3_stmt* stmt = nullptr;
    if (sqlite3_prepare_v2(handle_, sql, -1, &stmt, nullptr) != SQLITE_OK) {
        throw std::runtime_error("sqlite prepare (set_line_offset) nieudane");
    }
    sqlite3_bind_text(stmt, 1, file.c_str(), -1, SQLITE_TRANSIENT);
    sqlite3_bind_int64(stmt, 2, offset);
    if (sqlite3_step(stmt) != SQLITE_DONE) {
        sqlite3_finalize(stmt);
        throw std::runtime_error("sqlite step (set_line_offset) nieudane");
    }
    sqlite3_finalize(stmt);
}

std::int64_t Db::next_commands_seq() {
    const std::int64_t current = get_line_offset(kCommandsSeqKey);
    const std::int64_t next = current + 1;
    set_line_offset(kCommandsSeqKey, next);
    return next;
}

} // namespace zf
