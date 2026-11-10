// Słowniki braina (types.hpp): enumy w środku, stringi na granicach. Ten test pilnuje
// granic — klucze idą do SQLite, do JSON-a moda i do rules.toml, więc zmiana choćby
// jednej litery w to_key() po cichu rozjechałaby brain z istniejącymi bazami i z modem.
#include <cassert>
#include <iostream>
#include <set>
#include <string>

#include "db.hpp"
#include "types.hpp"

int main() {
    // Klucze stanów: dokładnie te, które od Etapu 3 leżą w kolumnie factions.state.
    assert(std::string(zf::to_key(zf::FactionState::Spokoj)) == "spokoj");
    assert(std::string(zf::to_key(zf::FactionState::Napiecie)) == "napiecie");
    assert(std::string(zf::to_key(zf::FactionState::Wojna)) == "wojna");
    for (const zf::FactionState s : zf::kFactionStates) {
        assert(zf::parse_faction_state(zf::to_key(s)) == s && "stan: klucz tam i z powrotem");
    }
    // Nieznany klucz = stan startowy (DEFAULT 'spokoj' w schema.sql), nie wyjątek.
    assert(zf::parse_faction_state("") == zf::FactionState::Spokoj);
    assert(zf::parse_faction_state("napięcie") == zf::FactionState::Spokoj &&
           "klucz jest BEZ ogonków — wersja z ogonkiem to literówka, nie stan");
    assert(std::string(zf::display(zf::FactionState::Napiecie)) == "napięcie");
    assert(std::string(zf::display(zf::FactionState::Spokoj)) == "spokoj" &&
           "raport /zf rel od zawsze pokazuje „spokoj” — nie zmieniamy tego po cichu");

    // Klucze typów zleceń: to samo, co mod czyta w contract_create (Contracts.cs) i co
    // stoi w [kontrakty.typy] w rules.toml.
    const std::set<std::string> oczekiwane = {"dostawa", "nagroda", "transport",
                                              "naprawa", "poszukiwania", "wlasne"};
    std::set<std::string> klucze;
    for (const zf::ContractKind k : zf::kContractKinds) {
        klucze.insert(zf::to_key(k));
        assert(zf::parse_contract_kind(zf::to_key(k)) == k && "typ: klucz tam i z powrotem");
    }
    assert(klucze == oczekiwane && "lista typów zgodna z protokołem i z modem");
    assert(!zf::parse_contract_kind("") && "puste pole = brak typu (fallback robi wołający)");
    assert(!zf::parse_contract_kind("eskorta") && "eskorta usunięta 2026-08-09 — ma nie wrócić");

    // Granica SQLite: stan zapisany enumem wraca tym samym enumem, a w kolumnie leży klucz.
    {
        zf::Db db(":memory:");
        db.ensure_faction("KRW", "Krwawa Ręka");
        assert(db.list_factions().at(0).state == zf::FactionState::Spokoj && "stan startowy");
        for (const zf::FactionState s : zf::kFactionStates) {
            db.set_faction_state("KRW", s);
            assert(db.list_factions().at(0).state == s && "stan przeżywa zapis do SQLite");
        }
    }

    std::cout << "types_test OK\n";
    return 0;
}
