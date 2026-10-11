#pragma once

#include <array>
#include <optional>
#include <string_view>

namespace zf {

// Wspólne słowniki braina. Wewnątrz silnika i configu żyją jako enumy — literówka typu
// "napięcie" (z ogonkiem) albo "nagorda" kompilowała się bez słowa i zachowywała jak
// spokój / jak brak typu. Stringi zostają WYŁĄCZNIE na granicach: SQLite, JSON do moda,
// klucze w rules.toml i logi — tamte formaty się nie zmieniły.

// Stan frakcji wobec gracza (maszyna stanów z histerezą, patrz Engine::update_state).
enum class FactionState { Spokoj, Napiecie, Wojna };

inline constexpr std::array<FactionState, 3> kFactionStates = {
    FactionState::Spokoj, FactionState::Napiecie, FactionState::Wojna};

// Klucz w SQLite i w logach, bez ogonków: "spokoj" / "napiecie" / "wojna".
const char* to_key(FactionState state);
// Nieznany klucz -> Spokoj, czyli to samo co DEFAULT kolumny w schema.sql.
FactionState parse_faction_state(std::string_view key);
// Do raportu /zf rel i promptu: "napięcie" z ogonkiem, reszta jak klucz.
const char* display(FactionState state);

// Rodzaj zlecenia — każdy ma swoją klasę w Sandbox.ModAPI.Contracts (patrz Contracts.cs).
enum class ContractKind { Dostawa, Nagroda, Transport, Naprawa, Poszukiwania, Wlasne };

// Kolejność = kolejność w dokumentacji i w /zf kontrakt.
inline constexpr std::array<ContractKind, 6> kContractKinds = {
    ContractKind::Dostawa,      // MyContractAcquisition
    ContractKind::Nagroda,      // MyContractBounty
    ContractKind::Transport,    // MyContractHauling
    ContractKind::Naprawa,      // MyContractRepair
    ContractKind::Poszukiwania, // MyContractSearch
    ContractKind::Wlasne,       // MyContractCustom
    // "eskorta" USUNIĘTA 2026-08-09. Gra nie ma już definicji ContractTypeEscort
    // (Content/Data wozi osiem typów, Escort wśród nich nie ma), więc AddContract
    // zawsze zwracało Error — typ nie mógł powstać ANI RAZU. Trzymaliśmy go „na wypadek
    // przywrócenia przez Keena", ale kosztowało to gałąź w silniku, dwa case'y w modzie
    // i trzy warstwy testów pilnujących, żeby martwy kod pozostał martwy. Implementacja
    // i ustalenie z dekompilacji siedzą w historii gita — to jest archiwum, nie kod.
};

// Klucz w JSON-ie mostka, w SQLite i w rules.toml: "dostawa", "nagroda"...
const char* to_key(ContractKind kind);
// nullopt = nieznany (albo pusty) klucz; decyzję o fallbacku podejmuje wołający.
std::optional<ContractKind> parse_contract_kind(std::string_view key);

} // namespace zf
