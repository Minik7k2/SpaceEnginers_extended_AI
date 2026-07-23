#include "db.hpp"

#include <algorithm>
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

namespace {

// Wspólny szkielet: przygotuj, zbinduj tekst/liczby, wykonaj do SQLITE_DONE.
class Stmt {
public:
    Stmt(sqlite3* handle, const char* sql) {
        if (sqlite3_prepare_v2(handle, sql, -1, &stmt_, nullptr) != SQLITE_OK) {
            throw std::runtime_error(std::string("sqlite prepare nieudane: ") + sql);
        }
    }
    ~Stmt() { sqlite3_finalize(stmt_); }
    Stmt(const Stmt&) = delete;
    Stmt& operator=(const Stmt&) = delete;

    Stmt& text(int idx, const std::string& v) {
        sqlite3_bind_text(stmt_, idx, v.c_str(), -1, SQLITE_TRANSIENT);
        return *this;
    }
    Stmt& num(int idx, double v) {
        sqlite3_bind_double(stmt_, idx, v);
        return *this;
    }
    Stmt& i64(int idx, std::int64_t v) {
        sqlite3_bind_int64(stmt_, idx, v);
        return *this;
    }
    bool row() { return sqlite3_step(stmt_) == SQLITE_ROW; }
    void done() {
        if (sqlite3_step(stmt_) != SQLITE_DONE) {
            throw std::runtime_error("sqlite step nieudane");
        }
    }
    std::string col_text(int idx) const {
        const unsigned char* v = sqlite3_column_text(stmt_, idx);
        return v != nullptr ? reinterpret_cast<const char*>(v) : "";
    }
    double col_num(int idx) const { return sqlite3_column_double(stmt_, idx); }
    int col_int(int idx) const { return sqlite3_column_int(stmt_, idx); }

private:
    sqlite3_stmt* stmt_ = nullptr;
};

} // namespace

void Db::ensure_faction(const std::string& tag, const std::string& name) {
    Stmt s(handle_, "INSERT INTO factions (tag, name) VALUES (?, ?) ON CONFLICT(tag) DO NOTHING;");
    s.text(1, tag).text(2, name).done();
}

std::vector<FactionRow> Db::list_factions() const {
    Stmt s(handle_, "SELECT tag, name, state, action_budget FROM factions ORDER BY tag;");
    std::vector<FactionRow> rows;
    while (s.row()) {
        rows.push_back({s.col_text(0), s.col_text(1), s.col_text(2), s.col_int(3)});
    }
    return rows;
}

void Db::set_faction_state(const std::string& tag, const std::string& state) {
    Stmt s(handle_, "UPDATE factions SET state = ? WHERE tag = ?;");
    s.text(1, state).text(2, tag).done();
}

void Db::set_faction_budget(const std::string& tag, int budget) {
    Stmt s(handle_, "UPDATE factions SET action_budget = ? WHERE tag = ?;");
    s.i64(1, budget).text(2, tag).done();
}

RelationRow Db::get_relation(const std::string& a, const std::string& b) const {
    Stmt s(handle_, "SELECT value, cap FROM relations WHERE a = ? AND b = ?;");
    s.text(1, a).text(2, b);
    RelationRow row;
    if (s.row()) {
        row.value = s.col_num(0);
        row.cap = s.col_num(1);
    }
    return row;
}

double Db::adjust_relation(const std::string& a, const std::string& b, double delta) {
    const RelationRow current = get_relation(a, b);
    double next = current.value + delta;
    next = std::min(next, current.cap);
    next = std::max(next, -100.0);

    Stmt s(handle_,
           "INSERT INTO relations (a, b, value, cap) VALUES (?, ?, ?, ?) "
           "ON CONFLICT(a, b) DO UPDATE SET value = excluded.value;");
    s.text(1, a).text(2, b).num(3, next).num(4, current.cap).done();
    return next;
}

void Db::lower_relation_cap(const std::string& a, const std::string& b, double cap) {
    const RelationRow current = get_relation(a, b);
    const double next_cap = std::min(current.cap, cap);
    const double next_value = std::min(current.value, next_cap);
    Stmt s(handle_,
           "INSERT INTO relations (a, b, value, cap) VALUES (?, ?, ?, ?) "
           "ON CONFLICT(a, b) DO UPDATE SET value = excluded.value, cap = excluded.cap;");
    s.text(1, a).text(2, b).num(3, next_value).num(4, next_cap).done();
}

std::vector<std::pair<std::string, std::string>> Db::list_relation_pairs() const {
    Stmt s(handle_, "SELECT a, b FROM relations ORDER BY a, b;");
    std::vector<std::pair<std::string, std::string>> pairs;
    while (s.row()) {
        pairs.emplace_back(s.col_text(0), s.col_text(1));
    }
    return pairs;
}

void Db::add_memory(std::int64_t ts, const std::string& faction, const std::string& type,
                    int weight, const std::string& summary) {
    Stmt s(handle_,
           "INSERT INTO events (ts, faction, type, weight, summary) VALUES (?, ?, ?, ?, ?);");
    s.i64(1, ts).text(2, faction).text(3, type).i64(4, weight).text(5, summary).done();
}

std::vector<std::string> Db::recent_memories(const std::string& faction, int n) const {
    Stmt s(handle_,
           "SELECT summary FROM events WHERE faction = ? ORDER BY id DESC LIMIT ?;");
    s.text(1, faction).i64(2, n);
    std::vector<std::string> rows;
    while (s.row()) {
        rows.push_back(s.col_text(0));
    }
    std::reverse(rows.begin(), rows.end());
    return rows;
}

void Db::upsert_contract(const std::string& id, const std::string& faction, const std::string& kind,
                         const std::string& status, const std::string& payload) {
    Stmt s(handle_,
           "INSERT INTO contracts (contract_id, faction, kind, status, payload) "
           "VALUES (?, ?, ?, ?, ?) "
           "ON CONFLICT(contract_id) DO UPDATE SET faction=excluded.faction, kind=excluded.kind, "
           "status=excluded.status, payload=excluded.payload;");
    s.text(1, id).text(2, faction).text(3, kind).text(4, status).text(5, payload).done();
}

void Db::set_contract_status(const std::string& id, const std::string& status) {
    Stmt s(handle_, "UPDATE contracts SET status = ? WHERE contract_id = ?;");
    s.text(1, status).text(2, id).done();
}

std::string Db::get_contract_faction(const std::string& id) const {
    Stmt s(handle_, "SELECT faction FROM contracts WHERE contract_id = ?;");
    s.text(1, id);
    return s.row() ? s.col_text(0) : std::string{};
}

} // namespace zf
