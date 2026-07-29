// Testy configu: nakładka rules.local.toml, sekcja [kontrakty] i — przede wszystkim —
// automatyczne wykrywanie katalogu storage moda (ścieżka zmienia się przy każdym nowym
// świecie, więc ręczne wpisywanie jej było główną barierą wejścia).
#include <cassert>
#include <chrono>
#include <cstdlib>
#include <filesystem>
#include <fstream>
#include <iostream>

#include "config.hpp"

namespace fs = std::filesystem;

namespace {

void write_file(const fs::path& path, const std::string& content) {
    fs::create_directories(path.parent_path());
    std::ofstream out(path, std::ios::binary);
    out << content;
}

} // namespace

int main() {
    const fs::path tmp = fs::temp_directory_path() / "zf_config_test";
    std::error_code ec;
    fs::remove_all(tmp, ec);
    fs::create_directories(tmp);

    // Drzewo zapisów SE: Saves/<steamid>/<świat>/Storage/<mod>/events.jsonl
    const fs::path saves = tmp / "Saves";
    const fs::path stary = saves / "76561198000000000" / "Stary Swiat" / "Storage" / "ZF" / "events.jsonl";
    const fs::path nowy = saves / "76561198000000000" / "Nowy Swiat" / "Storage" / "ZF" / "events.jsonl";
    write_file(stary, "{}\n");
    write_file(nowy, "{}\n");
    // „Nowy Swiat" ma być świeższy — to w nim gracz właśnie siedzi.
    fs::last_write_time(stary, fs::file_time_type::clock::now() - std::chrono::hours(5), ec);

#ifdef _WIN32
    _putenv_s("ZF_SAVES_DIR", saves.string().c_str());
#else
    setenv("ZF_SAVES_DIR", saves.string().c_str(), 1);
#endif

    // 1. Pusty storage_dir => wykrycie automatyczne, wybór najświeższego świata.
    const fs::path cfg_path = tmp / "rules.toml";
    write_file(cfg_path,
               "[bridge]\nstorage_dir = \"\"\npoll_ms = 250\n"
               "[kontrakty]\nwlaczone = false\nnagroda_min = 1234\n"
               "[reputacja]\nsync = false\nzakres = 900\npolityka = false\n");
    {
        const zf::Config cfg = zf::load_config(cfg_path.string());
        assert(cfg.storage_dir == nowy.parent_path().generic_string() &&
               "ma wykryć storage najświeższego świata");
        assert(cfg.poll_ms == 250);
        assert(!cfg.kontrakty_wlaczone && cfg.kontrakty_nagroda_min == 1234 &&
               "sekcja [kontrakty] ma być wczytywana");
        assert(!cfg.reputacja_sync && cfg.reputacja_zakres == 900 && !cfg.reputacja_polityka &&
               "sekcja [reputacja] ma być wczytywana");
        assert(cfg.reputacja_prog == 500 && "brak klucza => domyślny próg gry");
    }

    // 2. Ścieżka z configu, która NIE istnieje (typowo: świat skasowany albo inna maszyna)
    //    — też schodzimy na wykrywanie zamiast wywalać się błędem.
    write_file(cfg_path, "[bridge]\nstorage_dir = \"/nie/ma/takiej/sciezki\"\n");
    {
        const zf::Config cfg = zf::load_config(cfg_path.string());
        assert(cfg.storage_dir == nowy.parent_path().generic_string() &&
               "nieistniejąca ścieżka z configu ma ustąpić wykrytej");
    }

    // 3. Istniejąca ścieżka z configu wygrywa z wykrywaniem (świadomy wybór użytkownika).
    const fs::path reczny = tmp / "reczny_storage";
    fs::create_directories(reczny);
    write_file(cfg_path, "[bridge]\nstorage_dir = \"" + reczny.generic_string() + "\"\n");
    {
        const zf::Config cfg = zf::load_config(cfg_path.string());
        assert(cfg.storage_dir == reczny.generic_string() && "config ma pierwszeństwo, gdy katalog istnieje");
    }

    // 4. Nakładka rules.local.toml nadpisuje wartości z pliku głównego.
    write_file(tmp / "rules.local.toml", "[bridge]\npoll_ms = 999\n");
    {
        const zf::Config cfg = zf::load_config(cfg_path.string());
        assert(cfg.poll_ms == 999 && "rules.local.toml ma nadpisywać rules.toml");
        assert(cfg.storage_dir == reczny.generic_string());
    }

    // 5. Brak jakiegokolwiek storage (pusty korzeń poszukiwań) => czytelny błąd, nie cisza.
#ifdef _WIN32
    _putenv_s("ZF_SAVES_DIR", (tmp / "pusto").string().c_str());
#else
    setenv("ZF_SAVES_DIR", (tmp / "pusto").string().c_str(), 1);
#endif
    write_file(cfg_path, "[bridge]\nstorage_dir = \"\"\n");
    fs::remove(tmp / "rules.local.toml", ec);
    {
        bool threw = false;
        try {
            zf::load_config(cfg_path.string());
        } catch (const std::exception&) {
            threw = true;
        }
        assert(threw && "bez storage config ma rzucić wyjaśniający błąd");
    }

    fs::remove_all(tmp, ec);
    std::cout << "zf_config_test: OK\n";
    return 0;
}
