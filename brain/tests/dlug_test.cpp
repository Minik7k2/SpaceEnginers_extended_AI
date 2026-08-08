// Strażnicy DŁUGU TECHNICZNEGO — testy, które pilnują rzeczy ŚWIADOMIE niedokończonych.
//
// PO CO TO JEST. Zwykły test broni działającej mechaniki. Ten plik broni czegoś innego:
// decyzji „tego jeszcze NIE WŁĄCZAMY, bo brakuje drugiej połowy". Taka decyzja żyje dziś
// wyłącznie w komentarzu w rules.toml i w CLAUDE.md, a jedyne, co dzieli ją od cofnięcia,
// to jedna cyfra w configu. Nikt tego nie zauważy — zlecenie po prostu wyjdzie, gra je
// przyjmie, a gracz dostanie zadanie bez warunku wykonania i straci kaucję.
//
// I dokładnie tak się stało: `[kontrakty.typy] wlasne = 0` z długim komentarzem
// „WYŁĄCZONE, bo nie ma warunku wykonania" było prawdą tylko dla wartości domyślnej —
// `[kontrakty.typy.KRW]` niżej w TYM SAMYM PLIKU miało `wlasne = 2`, a nadpisanie frakcji
// wygrywa z domyślną. Piraci przez tydzień losowali typ opisany w repo jako wyłączony.
// Żaden istniejący test tego nie łapał: scenariusze sprawdzają typ WYMUSZONY (`/zf kontrakt
// KRW wlasne`), czyli ścieżkę, która celowo omija wagi.
//
// ZASADA: dopóki dług istnieje, waga ma być zerowa WSZĘDZIE. Gdy ktoś dopisze brakującą
// połowę, skreśla wpis z kBlokady — i to skreślenie jest miejscem, w którym trzeba spojrzeć
// na listę warunków. Test nie broni cyfry, broni tego spojrzenia.
//
// Czytamy PRAWDZIWY brain/configs/rules.toml (ścieżka z CMake), a nie config syntetyczny.
// Test o wartościach wpisanych w samym teście nie powiedziałby nic o tym, co dostaje gracz.
#include <algorithm>
#include <cassert>
#include <filesystem>
#include <fstream>
#include <iostream>
#include <map>
#include <set>
#include <sstream>
#include <string>
#include <vector>

#include "config.hpp"
#include "db.hpp"
#include "engine.hpp"
#include "fallback.hpp"

namespace fs = std::filesystem;

namespace {

// Typy zleceń zablokowane wagą 0 — z powodem i z warunkiem odblokowania. Powód idzie
// wprost do komunikatu FAIL, żeby ten, kto zmienił wagę, przeczytał go bez wchodzenia
// w CLAUDE.md.
struct Blokada {
    const char* typ;
    const char* powod;
    const char* warunek_odblokowania;
};

const std::vector<Blokada> kBlokady = {
    {"wlasne",
     "IMyContractCustom NIE MA warunku wykonania — MyContractConditionCustom kończy "
     "wyłącznie mod przez IMyContractSystem.TryFinishCustomContract(id), czego nie robimy. "
     "Gracz dostaje zlecenie bez zadania, które wygasa na karę relacji i przepadek kaucji",
     "zaimplementuj warunek po stronie moda (TryFinishCustomContract w Contracts.cs) "
     "i dopiero wtedy podnieś wagę"},
    {"eskorta",
     "typ USUNIĘTY Z GRY (potwierdzone 2026-08-05): Content/Data wozi osiem typów, "
     "ContractTypeEscort wśród nich nie ma. CreateCustomEscortContract wychodzi na "
     "pierwszym warunku i zwraca Error BEZ WPISU DO LOGU",
     "typ może wrócić tylko z aktualizacją gry — najpierw sprawdź, czy definicja "
     "ContractTypeEscort znowu istnieje"},
    {"nagroda",
     "vanillowa nagroda za głowę liczy zabicia GRACZY, nie NPC — zlecenie na tożsamość "
     "bota nie ma jak się zaliczyć",
     "potwierdź W GRZE (I14 w docs/testy-reczne.md), że zabicie NPC zamyka kontrakt"},
};

// Stany maszyny frakcji — waga może zależeć od stanu (nagroda × mnoznik_nagrody_w_napieciu).
const std::vector<std::string> kStany = {"spokoj", "napiecie", "wojna"};

bool zablokowany(const std::string& typ) {
    return std::any_of(kBlokady.begin(), kBlokady.end(),
                       [&](const Blokada& b) { return typ == b.typ; });
}

int bledy = 0;

void zglos(const std::string& co) {
    std::cerr << "  FAIL " << co << "\n";
    ++bledy;
}

void ok(const std::string& co) { std::cout << "  ok   " << co << "\n"; }

void sprawdz(bool warunek, const std::string& co) {
    if (warunek) {
        ok(co);
    } else {
        zglos(co);
    }
}

} // namespace

int main() {
    const fs::path rules = ZF_RULES_TOML;
    if (!fs::exists(rules)) {
        std::cerr << "Nie ma " << rules << " — testy długu nie mają czego pilnować.\n";
        return 1;
    }
    // wymagaj_storage=false: CI nie ma zapisów SE, a most plikowy jest tu nieużywany.
    const zf::Config cfg = zf::load_config(rules.string(), /*wymagaj_storage=*/false);

    // Frakcje, dla których pytamy o wagę: nasze trzy PLUS każda, która ma własną sekcję
    // [kontrakty.typy.X] w pliku. Sama lista NASZE_TAGI nie wystarczy — dopisanie sekcji
    // dla nowej frakcji (Kult z Etapu 7) nie może wymknąć się spod tej bramki.
    std::set<std::string> frakcje = {"HEL", "KRW", "WGR"};
    for (const auto& [tag, _] : cfg.kontrakty_wagi) {
        if (!tag.empty()) {
            frakcje.insert(tag);
        }
    }
    // Pusty tag = wartości domyślne dla wszystkich frakcji; sprawdzamy je tak samo.
    frakcje.insert("");

    std::cout << "== D1. Typy zablokowane mają wagę 0 dla KAŻDEJ frakcji i w KAŻDYM stanie\n";
    for (const Blokada& b : kBlokady) {
        // Melduj po jednym FAIL na FRAKCJĘ, nie na każdy stan — nadpisanie wagi jest jedno,
        // a trzy identyczne akapity z powodem blokady tylko zaśmiecają raport.
        std::set<std::string> zle;
        for (const std::string& frakcja : frakcje) {
            for (const std::string& stan : kStany) {
                if (zf::contract_kind_weight(cfg, frakcja, b.typ, stan) > 0) {
                    zle.insert(frakcja);
                }
            }
        }
        if (zle.empty()) {
            ok(std::string(b.typ) + " zablokowane wszędzie (domyślnie i per frakcja)");
            continue;
        }
        std::string gdzie;
        for (const std::string& frakcja : zle) {
            gdzie += (gdzie.empty() ? "" : ", ") + std::string("[kontrakty.typy") +
                     (frakcja.empty() ? "" : "." + frakcja) + "]";
        }
        zglos(std::string(b.typ) + " ma wagę > 0 w: " + gdzie + " — a ma być 0 WSZĘDZIE.\n" +
              "       POWÓD BLOKADY: " + b.powod + ".\n" +
              "       ŻEBY ODBLOKOWAĆ: " + b.warunek_odblokowania + ".\n" +
              "       UWAGA: nadpisanie [kontrakty.typy.<frakcja>] WYGRYWA z wartością "
              "domyślną, więc `" + b.typ + " = 0` u góry pliku samo nie wystarcza.\n" +
              "       Jeśli dług NAPRAWDĘ został spłacony — usuń wpis \"" + b.typ +
              "\" z kBlokady w tym pliku. To skreślenie jest jedynym miejscem, w którym "
              "ktoś musi świadomie potwierdzić, że warunek wyżej jest spełniony.");
    }

    std::cout << "== D2. Mnożnik nagrody w napięciu NIE wskrzesza typu wyłączonego\n";
    {
        // 0 × N = 0 — komentarz w config.cpp to obiecuje, więc niech to będzie sprawdzone.
        // Gdyby ktoś zmienił mnożnik na dodawanie, `nagroda` wróciłaby do puli w każdej
        // frakcji w napięciu i nikt by tego nie zauważył poza grą.
        const double spokoj = zf::contract_kind_weight(cfg, "KRW", "nagroda", "spokoj");
        const double wojna = zf::contract_kind_weight(cfg, "KRW", "nagroda", "wojna");
        sprawdz(spokoj == 0 && wojna == 0,
                "nagroda: 0 w spokoju => 0 w wojnie (mnoznik_nagrody_w_napieciu nie włącza typu)");
    }

    std::cout << "== D3. Typy NIEzablokowane naprawdę wchodzą do losowania\n";
    for (const std::string& typ : zf::Config::contract_kinds()) {
        if (zablokowany(typ)) {
            continue;
        }
        bool gdziekolwiek = false;
        for (const std::string& frakcja : frakcje) {
            gdziekolwiek = gdziekolwiek || zf::contract_kind_weight(cfg, frakcja, typ, "spokoj") > 0;
        }
        // Typ z wagą 0 wszędzie, ale BEZ wpisu w kBlokady, to dług nieudokumentowany:
        // albo ktoś wyłączył go po cichu, albo zapomniał opisać dlaczego.
        sprawdz(gdziekolwiek, "typ " + typ + " ma wagę > 0 u co najmniej jednej frakcji "
                              "(inaczej: albo dopisz go do kBlokady z powodem, albo przywróć wagę)");
    }

    std::cout << "== D4. Każdy typ ma JAWNY mnożnik trudności w [kontrakty.mnoznik]\n";
    for (const std::string& typ : zf::Config::contract_kinds()) {
        // Brak wpisu daje po cichu 1.0 — zlecenie jest wtedy wycenione jak dostawa, choć
        // może być dwa razy trudniejsze. Pytamy o KLUCZ, nie o wartość: „1.0, bo tak
        // zdecydowaliśmy" i „1.0, bo zapomnieliśmy wpisać" są nie do odróżnienia po samej
        // liczbie, a to druga wersja jest błędem.
        const bool jawny = cfg.kontrakty_mnoznik.count(typ) > 0;
        sprawdz(jawny, "typ " + typ + " ma wpis w [kontrakty.mnoznik]" +
                           (jawny ? " (" + std::to_string(zf::contract_kind_multiplier(cfg, typ)) +
                                        ")"
                                  : " — brak wpisu, wycena po cichu spada do 1.0"));
    }

    std::cout << "== D5. Losowanie NIGDY nie zwraca typu zablokowanego (test zachowania)\n";
    {
        // D1 pyta o wagę, D5 pyta o WYNIK — bo między jednym a drugim stoi
        // pick_contract_kind z własnymi bramkami i pulą. Gdyby kiedyś ktoś dołożył tam
        // ścieżkę „gdy pula pusta, weź cokolwiek", D1 dalej byłoby zielone.
        const fs::path tmp = fs::temp_directory_path() / "zf_dlug_test";
        std::error_code ec;
        fs::remove_all(tmp, ec);
        fs::create_directories(tmp);
        const fs::path fallback_path = tmp / "fallback.toml";
        {
            std::ofstream out(fallback_path, std::ios::binary);
            out << "[HEL]\nneutral = \"x\"\n[KRW]\nneutral = \"x\"\n[WGR]\nneutral = \"x\"\n";
        }

        zf::Db db(":memory:");
        zf::Fallback fallback(fallback_path.string());
        zf::Engine engine(db, fallback, /*rng_seed=*/2026);

        zf::Event start;
        start.type = "session_start";
        start.data = {{"world", "DLUG"}, {"player_id", 1}, {"player_name", "Test"},
                      {"mod_version", "test"}};
        std::int64_t ts = 1000;
        engine.on_event(start, cfg, ts);
        engine.take_contracts();

        std::map<std::string, int> rozklad;
        std::map<std::string, int> wpadki; // "<frakcja> <typ>" -> ile razy wyszedł mimo blokady
        const int kLosowan = 400; // dość, by typ o wadze 1 przy sumie ~9 wyszedł wielokrotnie
        // Silnik melduje każde zlecenie na stdout — przy 1200 losowaniach to 1200 linii,
        // w których ginie jedna linia FAIL. Na czas pętli przekierowujemy cout do kosza;
        // cerr (czyli zglos) zostaje, więc błędy dalej widać.
        std::ostringstream kosz;
        std::streambuf* poprzedni = std::cout.rdbuf(kosz.rdbuf());
        for (int i = 0; i < kLosowan; ++i) {
            for (const std::string frakcja : {"HEL", "KRW", "WGR"}) {
                ts += 1000;
                zf::Event ev;
                ev.type = "debug_command";
                // Bez pola "kind" — czyli normalne losowanie wagami, ta sama droga co tick.
                ev.data = {{"cmd", "kontrakt"}, {"faction", frakcja}};
                engine.on_event(ev, cfg, ts);
                for (const zf::ContractOut& c : engine.take_contracts()) {
                    ++rozklad[c.kind];
                    if (zablokowany(c.kind)) {
                        // Zliczamy zamiast meldować od razu: przy wadze 2 na 8 to byłoby
                        // 300 identycznych linii, w których ginie reszta raportu.
                        ++wpadki[frakcja + " " + c.kind];
                    }
                }
            }
        }
        std::cout.rdbuf(poprzedni);

        std::string opis;
        for (const auto& [typ, ile] : rozklad) {
            opis += (opis.empty() ? "" : ", ") + typ + "=" + std::to_string(ile);
        }
        std::cout << "     rozkład z " << kLosowan * 3 << " losowań: " << opis << "\n";
        for (const auto& [kto, ile] : wpadki) {
            zglos("losowanie zwróciło ZABLOKOWANY typ: " + kto + " — " + std::to_string(ile) +
                  " raz(y) na " + std::to_string(kLosowan) + " losowań tej frakcji. "
                  "Sprawdź nadpisanie [kontrakty.typy.<frakcja>]: wygrywa ono z wartością "
                  "domyślną, więc `wlasne = 0` u góry pliku niczego nie gwarantuje");
        }
        sprawdz(wpadki.empty(), "żadne z " + std::to_string(kLosowan * 3) +
                                    " losowań nie zwróciło typu zablokowanego");
        // Kontrola pustego przebiegu: gdyby losowanie nic nie zwracało, pętla wyżej
        // przeszłaby „na zielono", nie sprawdziwszy niczego (ta sama pułapka co pusty glob
        // scenariuszy — zero przypadków przechodzi w 100%).
        sprawdz(!rozklad.empty(), "losowanie w ogóle coś zwróciło (inaczej D5 nic nie sprawdza)");
        fs::remove_all(tmp, ec);
    }

    std::cout << "\n[dlug_test] " << (bledy == 0 ? "OK" : "BŁĘDY: " + std::to_string(bledy))
              << "\n";
    // assert zamiast samego return: w Debug daje natychmiastowy stack trace, a w Release
    // (z -UNDEBUG, patrz CMakeLists) działa tak samo.
    assert(bledy == 0 && "strażnicy długu technicznego — patrz komunikaty FAIL wyżej");
    return bledy == 0 ? 0 : 1;
}
