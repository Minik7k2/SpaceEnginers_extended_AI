// Test integracyjny mostka bez uruchamiania Space Engineers: symuluje moda dopisując
// linie do events.jsonl i sprawdza, że brain (EventReader/CommandWriter/Db) poprawnie
// czyta zdarzenia, trzyma offsety w SQLite i odpisuje testowym radio_message.
#include <cassert>
#include <filesystem>
#include <fstream>
#include <iostream>
#include <sstream>

#include <nlohmann/json.hpp>

#include "bridge.hpp"
#include "db.hpp"

namespace fs = std::filesystem;

namespace {

void append_line(const fs::path& file, const nlohmann::json& line) {
    std::ofstream out(file, std::ios::app | std::ios::binary);
    out << line.dump() << "\n";
}

std::string read_file(const fs::path& file) {
    std::ifstream in(file, std::ios::binary);
    std::ostringstream buf;
    buf << in.rdbuf();
    return buf.str();
}

} // namespace

int main() {
    const fs::path tmp = fs::temp_directory_path() / "zf_bridge_test";
    std::error_code ec;
    fs::remove_all(tmp, ec);
    fs::create_directories(tmp);

    const fs::path storage_dir = tmp / "storage";
    fs::create_directories(storage_dir);
    const fs::path db_path = tmp / "state.sqlite3";

    // 1) mod pisze session_start + chat_message do events.jsonl
    append_line(storage_dir / "events.jsonl", {
        {"v", 1}, {"seq", 1}, {"ts", 1000},
        {"type", "session_start"},
        {"data", {{"world", "TestWorld"}, {"player_id", 1}, {"player_name", "Minik"}, {"mod_version", "0.1"}}},
    });
    append_line(storage_dir / "events.jsonl", {
        {"v", 1}, {"seq", 2}, {"ts", 2000},
        {"type", "chat_message"},
        {"data", {{"text", "halo brain"}, {"target", nullptr}, {"in_range", nlohmann::json::array()}}},
    });
    // niekompletna linia (poprawny JSON, ale bez końcowego \n — jakby zapis dopiero trwał)
    // — musi zostać zignorowana w tym pollu.
    {
        std::ofstream out(storage_dir / "events.jsonl", std::ios::app | std::ios::binary);
        out << R"({"v":1,"seq":3,"ts":3000,"type":"heartbeat","data":{"pos":[1,2,3],"speed":10.5}})";
    }

    {
        zf::Db db(db_path.string());
        zf::EventReader reader(storage_dir.string(), db);
        zf::CommandWriter writer(storage_dir.string(), db, 5 * 1024 * 1024);

        const std::vector<zf::Event> events = reader.poll();
        assert(events.size() == 2 && "niekompletna linia nie powinna być przetworzona");
        assert(events[0].type == "session_start");
        assert(events[1].type == "chat_message");
        assert(events[1].data.at("text") == "halo brain");

        // brain odpisuje echem — symulacja tego co robi main.cpp dla chat_message
        writer.write_radio_message("TEST", "Echo: " + events[1].data.at("text").get<std::string>(), "white", 0);

        // drugi poll bez nowych danych — offset z SQLite musi zapobiec ponownemu przetworzeniu
        assert(reader.poll().empty());
    }

    const std::string commands = read_file(storage_dir / "commands.jsonl");
    const nlohmann::json cmd_line = nlohmann::json::parse(
        commands.substr(0, commands.find('\n')));
    assert(cmd_line.at("type") == "radio_message");
    assert(cmd_line.at("data").at("faction") == "TEST");
    assert(cmd_line.at("data").at("text") == "Echo: halo brain");

    // dopisanie niedokończonej linii i restart procesu (nowy Db/reader) — offset musi przetrwać
    {
        std::ofstream out(storage_dir / "events.jsonl", std::ios::app | std::ios::binary);
        out << "\n"; // dokańcza wcześniej niekompletną linię heartbeat
    }
    {
        zf::Db db(db_path.string());
        zf::EventReader reader(storage_dir.string(), db);
        const std::vector<zf::Event> events = reader.poll();
        assert(events.size() == 1);
        assert(events[0].type == "heartbeat");
    }

    // Regresja: nowy świat = nowy storage_dir, ale ta sama baza brain. Offsety są
    // kluczowane pełną ścieżką, więc krótszy events.jsonl innego świata musi być
    // przeczytany od zera (bug z 2026-07-19: klucz samą nazwą pliku → "brak odczytów").
    const fs::path storage_dir2 = tmp / "storage2";
    fs::create_directories(storage_dir2);
    append_line(storage_dir2 / "events.jsonl", {
        {"v", 1}, {"seq", 1}, {"ts", 9000},
        {"type", "session_start"},
        {"data", {{"world", "NowySwiat"}, {"player_id", 1}, {"player_name", "Minik"}, {"mod_version", "0.1"}}},
    });
    {
        zf::Db db(db_path.string());
        zf::EventReader reader(storage_dir2.string(), db);
        const std::vector<zf::Event> events = reader.poll();
        assert(events.size() == 1 && "zdarzenia nowego świata muszą być czytane mimo offsetów starego");
        assert(events[0].data.at("world") == "NowySwiat");
    }

    // Regresja: świat odtworzony pod TĄ SAMĄ ścieżką (plik krótszy niż offset)
    // — reader ma zresetować offset i przeczytać od początku, nie czekać w nieskończoność.
    fs::remove(storage_dir / "events.jsonl");
    append_line(storage_dir / "events.jsonl", {
        {"v", 1}, {"seq", 1}, {"ts", 9500},
        {"type", "session_start"},
        {"data", {{"world", "OdtworzonySwiat"}, {"player_id", 1}, {"player_name", "Minik"}, {"mod_version", "0.1"}}},
    });
    {
        zf::Db db(db_path.string());
        zf::EventReader reader(storage_dir.string(), db);
        const std::vector<zf::Event> events = reader.poll();
        assert(events.size() == 1 && "po skróceniu pliku offset musi się zresetować");
        assert(events[0].data.at("world") == "OdtworzonySwiat");
        assert(reader.poll().empty() && "po resecie offset musi być znów utrwalony");
    }

    std::cout << "zf_bridge_test: OK\n";
    return 0;
}
