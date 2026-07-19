#pragma once

#include <cstdint>
#include <map>
#include <string>

namespace zf {

// Szablony radiowe bez LLM (brain/personas/fallback.toml): [TAG] rodzaj = "tekst {zmienna}".
// Hot-reload przez mtime jak configi. Brak pliku/szablonu nie jest błędem krytycznym —
// silnik po prostu nie nada wiadomości (i zaloguje to raz na stderr).
class Fallback {
public:
    explicit Fallback(std::string path);

    // Przeładowuje plik gdy zmienił się mtime. Zwraca true przy przeładowaniu.
    bool poll();

    bool has(const std::string& faction, const std::string& kind) const;
    // Podstawia {klucz} z vars. Nieznane {klucze} zostają w tekście (widać co brakuje).
    std::string render(const std::string& faction, const std::string& kind,
                       const std::map<std::string, std::string>& vars = {}) const;

private:
    std::string path_;
    std::int64_t mtime_ = -1;
    std::map<std::string, std::map<std::string, std::string>> templates_;
    void load();
};

} // namespace zf
