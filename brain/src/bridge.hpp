#pragma once

#include <cstdint>
#include <fstream>
#include <string>
#include <vector>

#include <nlohmann/json.hpp>

#include "db.hpp"

namespace zf {

struct Event {
    std::string type;
    nlohmann::json data;
};

// Czyta events.jsonl (+ rotowane events-NNNN.jsonl) pisane przez mod. Offsety (liczba
// przetworzonych linii na plik) trzymane w SQLite, patrz docs/protocol.md.
class EventReader {
public:
    EventReader(std::string storage_dir, Db& db);

    // Zwraca nowe, kompletne (zakończone \n) zdarzenia od ostatniego pollu. Linie niekompletne
    // lub z niepoprawnym JSON-em / nieznanym "type" są pomijane (spec: "pomiń i czekaj").
    std::vector<Event> poll();

private:
    std::string storage_dir_;
    Db& db_;
};

// Pisze commands.jsonl (+ rotacja po rotate_bytes) czytane przez mod co ~60 tików.
// Plik otwierany tylko na czas dopisania linii: trzymanie uchwytu do zapisu blokuje
// ReadFileInWorldStorage po stronie moda (sharing violation → crash gry).
class CommandWriter {
public:
    CommandWriter(std::string storage_dir, Db& db, std::uint64_t rotate_bytes);

    void write_radio_message(const std::string& faction, const std::string& text,
                              const std::string& color, int priority);

    // spawn_request (Etap 5): mod woła spawner statku frakcji. kind = patrol/raid/convoy;
    // context to opis dla logu/atrybucji. Patrz docs/protocol.md.
    void write_spawn_request(const std::string& faction, const std::string& kind,
                             bool near_player, const std::string& context);

private:
    void write_line(const nlohmann::json& line);
    void rotate_if_needed();
    std::string active_path() const;

    std::string storage_dir_;
    Db& db_;
    std::uint64_t rotate_bytes_;
    int current_index_ = 1;
    std::uint64_t current_bytes_ = 0;
};

} // namespace zf
