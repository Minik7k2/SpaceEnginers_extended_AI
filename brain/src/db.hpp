#pragma once

#include <cstdint>
#include <string>
#include <utility>
#include <vector>

struct sqlite3;

namespace zf {

struct FactionRow {
    std::string tag;
    std::string name;
    std::string state;        // spokoj / napiecie / wojna
    int action_budget = 0;
};

struct RelationRow {
    double value = 0;
    double cap = 100;
};

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

    // --- Silnik relacji (Etap 3) ---

    // Rejestruje frakcję jeśli nieznana (stan startowy: spokoj). Nie nadpisuje istniejącej.
    void ensure_faction(const std::string& tag, const std::string& name);
    std::vector<FactionRow> list_factions() const;
    void set_faction_state(const std::string& tag, const std::string& state);
    void set_faction_budget(const std::string& tag, int budget);

    // Relacja a→b (b to 'PLAYER' albo tag frakcji). Brak wiersza = 0 / cap 100.
    RelationRow get_relation(const std::string& a, const std::string& b) const;
    // Dodaje deltę i przycina do [-100, cap]. Zwraca nową wartość.
    double adjust_relation(const std::string& a, const std::string& b, double delta);
    // Trwały modyfikator świata mściwego: cap tylko maleje (min z dotychczasowym).
    void lower_relation_cap(const std::string& a, const std::string& b, double cap);
    // Pary relacji, w których uczestniczy `a` (do dryfu i raportu /zf rel).
    std::vector<std::pair<std::string, std::string>> list_relation_pairs() const;

    // Pamięć długoterminowa (kontekst LLM w Etapie 4). weight: 0 zwykłe, 2 ciężkie.
    void add_memory(std::int64_t ts, const std::string& faction, const std::string& type,
                    int weight, const std::string& summary);
    // Ostatnie n wpisów pamięci frakcji, od najstarszego do najnowszego.
    std::vector<std::string> recent_memories(const std::string& faction, int n) const;

    // Kontrakty (Etap 6): utrwalone — ID muszą przeżyć restart świata (odtworzenie).
    void upsert_contract(const std::string& id, const std::string& faction, const std::string& kind,
                         const std::string& status, const std::string& payload);
    void set_contract_status(const std::string& id, const std::string& status);
    // Frakcja kontraktu ("" = nieznany id).
    std::string get_contract_faction(const std::string& id) const;

    // Małe wartości int64 (np. znacznik ostatniego ticku) — współdzieli tabelę bridge_state,
    // klucze zaczynają się od "__" żeby nie kolidowały ze ścieżkami plików.
    std::int64_t get_kv(const std::string& key) const { return get_line_offset(key); }
    void set_kv(const std::string& key, std::int64_t value) { set_line_offset(key, value); }

private:
    sqlite3* handle_ = nullptr;
};

} // namespace zf
