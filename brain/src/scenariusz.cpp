#include "scenariusz.hpp"

#include <cmath>
#include <fstream>
#include <iomanip>
#include <optional>
#include <ostream>
#include <sstream>
#include <string>
#include <vector>

#include <nlohmann/json.hpp>

#include "db.hpp"
#include "engine.hpp"
#include "fallback.hpp"

namespace zf {
namespace {

constexpr const char* kGracz = "PLAYER";

// Wszystko, co silnik wyprodukował z ostatniego zdarzenia. Czyszczone przed każdym
// kolejnym zdarzeniem, żeby oczekiwanie mówiło o konkretnej przyczynie, a nie o sumie
// całego pliku (inaczej „ceny x1.3" przechodziłoby, bo kiedyś tam były x1.3).
struct Bufory {
    std::vector<RadioOut> radia;
    std::vector<SpawnOut> spawny;
    std::vector<ContractOut> kontrakty;
    std::vector<ReputationOut> reputacje;
    std::vector<PriceOut> ceny;
    std::vector<std::pair<std::string, std::int64_t>> standdowny;
    std::vector<RansomDemandOut> okupy;

    void wyczysc() {
        radia.clear();
        spawny.clear();
        kontrakty.clear();
        reputacje.clear();
        ceny.clear();
        standdowny.clear();
        okupy.clear();
    }
};

std::string liczba(double v) {
    std::ostringstream os;
    os << std::fixed << std::setprecision(4) << v;
    std::string s = os.str();
    // Ucinamy zbędne zera, żeby komunikat czytał się jak liczba, a nie jak wydruk maszyny.
    while (s.size() > 1 && s.back() == '0') {
        s.pop_back();
    }
    if (!s.empty() && s.back() == '.') {
        s.pop_back();
    }
    return s;
}

// Zwięzły opis tego, co NAPRAWDĘ poszło — bez tego każdy FAIL kończyłby się
// ręcznym przeglądaniem logu.
std::string opis_cen(const std::vector<PriceOut>& ceny) {
    if (ceny.empty()) {
        return "(nic)";
    }
    std::string s;
    for (const PriceOut& p : ceny) {
        s += (s.empty() ? "" : ", ") + p.faction + " x" + liczba(p.modifier) +
             (p.embargo ? " EMBARGO" : "");
    }
    return s;
}

std::string opis_kontraktow(const std::vector<ContractOut>& kontrakty) {
    if (kontrakty.empty()) {
        return "(nic)";
    }
    std::string s;
    for (const ContractOut& c : kontrakty) {
        s += (s.empty() ? "" : ", ") + c.faction + " " + c.kind + " za " +
             std::to_string(c.reward) + " kr";
    }
    return s;
}

class Kontrola {
public:
    Kontrola(std::ostream& out, WynikScenariusza& wynik) : out_(out), wynik_(wynik) {}

    void ok(const std::string& co) {
        ++wynik_.sprawdzen;
        out_ << "    ok   " << co << "\n";
    }

    void fail(const std::string& co, const std::string& bylo) {
        ++wynik_.sprawdzen;
        ++wynik_.bledow;
        out_ << "    FAIL " << co << " | było: " << bylo << "\n";
    }

    // Skrót na najczęstszy kształt asercji: warunek + opis + co zastano.
    void sprawdz(bool warunek, const std::string& co, const std::string& bylo) {
        if (warunek) {
            ok(co);
        } else {
            fail(co, bylo);
        }
    }

private:
    std::ostream& out_;
    WynikScenariusza& wynik_;
};

// Pola opcjonalne: brak pola = nie sprawdzamy tego wymiaru.
bool ma(const nlohmann::json& j, const char* klucz) {
    return j.contains(klucz) && !j[klucz].is_null();
}

bool w_zakresie(const nlohmann::json& j, double wartosc) {
    if (ma(j, "wartosc") && std::fabs(wartosc - j["wartosc"].get<double>()) >
                                 (ma(j, "tol") ? j["tol"].get<double>() : 0.001)) {
        return false;
    }
    if (ma(j, "min") && wartosc < j["min"].get<double>()) {
        return false;
    }
    if (ma(j, "max") && wartosc > j["max"].get<double>()) {
        return false;
    }
    return true;
}

std::string zakres_opis(const nlohmann::json& j) {
    std::string s;
    if (ma(j, "wartosc")) {
        s += "= " + liczba(j["wartosc"].get<double>());
    }
    if (ma(j, "min")) {
        s += (s.empty() ? "" : " i ") + std::string(">= ") + liczba(j["min"].get<double>());
    }
    if (ma(j, "max")) {
        s += (s.empty() ? "" : " i ") + std::string("<= ") + liczba(j["max"].get<double>());
    }
    return s.empty() ? "(bez warunku)" : s;
}

// Bufory nie są const: oczekiwanie "okup_kredytowy" samo wywołuje de-eskalację (tak jak
// main.cpp po odpowiedzi modelu), więc powstały przy tym stand_down musi trafić do bufora,
// inaczej następna linia scenariusza go nie zobaczy.
void sprawdz_oczekiwanie(const nlohmann::json& linia, Bufory& buf, Db& db, Engine& engine,
                         const Config& cfg, std::int64_t now_ms, Kontrola& k) {
    const std::string rodzaj = linia.value("oczekuj", std::string{});
    const std::string frakcja = linia.value("frakcja", std::string{});

    if (rodzaj == "relacja") {
        const double v = db.get_relation(frakcja, kGracz).value;
        k.sprawdz(w_zakresie(linia, v), "relacja " + frakcja + " " + zakres_opis(linia),
                  liczba(v));
        return;
    }

    if (rodzaj == "sufit_relacji") {
        const double cap = db.get_relation(frakcja, kGracz).cap;
        k.sprawdz(w_zakresie(linia, cap), "sufit relacji " + frakcja + " " + zakres_opis(linia),
                  liczba(cap));
        return;
    }

    if (rodzaj == "ceny") {
        const PriceOut* znaleziony = nullptr;
        for (const PriceOut& p : buf.ceny) {
            if (p.faction == frakcja) {
                znaleziony = &p;
            }
        }
        if (znaleziony == nullptr) {
            k.fail("cennik " + frakcja + " miał się zmienić", opis_cen(buf.ceny));
            return;
        }
        bool dobrze = true;
        std::string co = "cennik " + frakcja;
        if (ma(linia, "mnoznik")) {
            const double tol = ma(linia, "tol") ? linia["tol"].get<double>() : 0.001;
            dobrze = dobrze && std::fabs(znaleziony->modifier - linia["mnoznik"].get<double>()) <= tol;
            co += " x" + liczba(linia["mnoznik"].get<double>());
        }
        if (ma(linia, "embargo")) {
            dobrze = dobrze && znaleziony->embargo == linia["embargo"].get<bool>();
            co += linia["embargo"].get<bool>() ? " + EMBARGO" : " bez embarga";
        }
        k.sprawdz(dobrze, co, opis_cen(buf.ceny));
        return;
    }

    if (rodzaj == "brak_cen") {
        bool jest = false;
        for (const PriceOut& p : buf.ceny) {
            jest = jest || p.faction == frakcja;
        }
        k.sprawdz(!jest, "cennik " + frakcja + " NIE miał się zmienić", opis_cen(buf.ceny));
        return;
    }

    if (rodzaj == "reputacja") {
        const std::string wobec = linia.value("wobec", std::string{});
        const ReputationOut* znaleziony = nullptr;
        for (const ReputationOut& r : buf.reputacje) {
            if (r.faction == frakcja && r.other == wobec) {
                znaleziony = &r;
            }
        }
        if (znaleziony == nullptr) {
            std::string bylo;
            for (const ReputationOut& r : buf.reputacje) {
                bylo += (bylo.empty() ? "" : ", ") + r.faction +
                        (r.other.empty() ? "->gracz" : "->" + r.other) + " " +
                        std::to_string(r.vanilla);
            }
            k.fail("reputacja " + frakcja + (wobec.empty() ? "->gracz" : "->" + wobec) +
                       " miała polecieć do gry",
                   bylo.empty() ? "(nic)" : bylo);
            return;
        }
        k.sprawdz(w_zakresie(linia, znaleziony->vanilla),
                  "reputacja " + frakcja + (wobec.empty() ? "->gracz" : "->" + wobec) + " " +
                      zakres_opis(linia),
                  std::to_string(znaleziony->vanilla));
        return;
    }

    if (rodzaj == "kontrakt") {
        const ContractOut* znaleziony = nullptr;
        for (const ContractOut& c : buf.kontrakty) {
            if (c.faction == frakcja &&
                (!ma(linia, "typ") || c.kind == linia["typ"].get<std::string>())) {
                znaleziony = &c;
            }
        }
        if (znaleziony == nullptr) {
            k.fail("zlecenie " + frakcja +
                       (ma(linia, "typ") ? " (" + linia["typ"].get<std::string>() + ")" : ""),
                   opis_kontraktow(buf.kontrakty));
            return;
        }
        bool dobrze = true;
        if (ma(linia, "min_nagroda")) {
            dobrze = dobrze && znaleziony->reward >= linia["min_nagroda"].get<std::int64_t>();
        }
        if (ma(linia, "max_nagroda")) {
            dobrze = dobrze && znaleziony->reward <= linia["max_nagroda"].get<std::int64_t>();
        }
        if (ma(linia, "cel")) {
            dobrze = dobrze && znaleziony->target_faction == linia["cel"].get<std::string>();
        }
        k.sprawdz(dobrze, "zlecenie " + frakcja + " " + znaleziony->kind,
                  opis_kontraktow(buf.kontrakty));
        return;
    }

    if (rodzaj == "brak_kontraktu") {
        bool jest = false;
        for (const ContractOut& c : buf.kontrakty) {
            jest = jest || c.faction == frakcja;
        }
        k.sprawdz(!jest, "frakcja " + frakcja + " NIE wystawia zlecenia",
                  opis_kontraktow(buf.kontrakty));
        return;
    }

    if (rodzaj == "spawn" || rodzaj == "brak_spawnu") {
        const SpawnOut* znaleziony = nullptr;
        std::string bylo;
        for (const SpawnOut& s : buf.spawny) {
            bylo += (bylo.empty() ? "" : ", ") + s.faction + " " + s.kind;
            if (s.faction == frakcja &&
                (!ma(linia, "kind") || s.kind == linia["kind"].get<std::string>())) {
                znaleziony = &s;
            }
        }
        if (bylo.empty()) {
            bylo = "(nic)";
        }
        if (rodzaj == "brak_spawnu") {
            k.sprawdz(znaleziony == nullptr, "brak spawnu dla " + frakcja, bylo);
        } else {
            k.sprawdz(znaleziony != nullptr,
                      "spawn " + frakcja +
                          (ma(linia, "kind") ? " kind=" + linia["kind"].get<std::string>() : ""),
                      bylo);
        }
        return;
    }

    if (rodzaj == "stand_down") {
        const std::pair<std::string, std::int64_t>* znaleziony = nullptr;
        std::string bylo;
        for (const auto& sd : buf.standdowny) {
            bylo += (bylo.empty() ? "" : ", ") + sd.first + " okup " + std::to_string(sd.second);
            if (sd.first == frakcja) {
                znaleziony = &sd;
            }
        }
        if (znaleziony == nullptr) {
            k.fail("stand_down dla " + frakcja, bylo.empty() ? "(nic)" : bylo);
            return;
        }
        const bool dobrze = !ma(linia, "okup") ||
                            znaleziony->second == linia["okup"].get<std::int64_t>();
        k.sprawdz(dobrze,
                  "stand_down " + frakcja +
                      (ma(linia, "okup") ? " z okupem " + std::to_string(linia["okup"].get<std::int64_t>())
                                         : ""),
                  bylo);
        return;
    }

    if (rodzaj == "brak_stand_down") {
        bool jest = false;
        for (const auto& sd : buf.standdowny) {
            jest = jest || sd.first == frakcja;
        }
        k.sprawdz(!jest, "brak stand_down dla " + frakcja, jest ? "był" : "(nic)");
        return;
    }

    if (rodzaj == "okup_zadanie") {
        const RansomDemandOut* znaleziony = nullptr;
        std::string bylo;
        for (const RansomDemandOut& r : buf.okupy) {
            bylo += (bylo.empty() ? "" : ", ") + r.faction + " " + std::to_string(r.amount) + "x " +
                    r.item;
            if (r.faction == frakcja) {
                znaleziony = &r;
            }
        }
        if (znaleziony == nullptr) {
            k.fail("żądanie trybutu od " + frakcja, bylo.empty() ? "(nic)" : bylo);
            return;
        }
        bool dobrze = true;
        if (ma(linia, "ilosc_min")) {
            dobrze = dobrze && znaleziony->amount >= linia["ilosc_min"].get<std::int64_t>();
        }
        if (ma(linia, "ilosc_max")) {
            dobrze = dobrze && znaleziony->amount <= linia["ilosc_max"].get<std::int64_t>();
        }
        if (ma(linia, "deadline_s")) {
            dobrze = dobrze && znaleziony->deadline_s == linia["deadline_s"].get<int>();
        }
        k.sprawdz(dobrze, "żądanie trybutu " + frakcja, bylo);
        return;
    }

    if (rodzaj == "brak_okupu") {
        bool jest = false;
        for (const RansomDemandOut& r : buf.okupy) {
            jest = jest || r.faction == frakcja;
        }
        k.sprawdz(!jest, "frakcja " + frakcja + " NIE żąda trybutu", jest ? "żądała" : "(nic)");
        return;
    }

    if (rodzaj == "prog_okupu") {
        const std::int64_t prog = engine.cash_ransom_threshold(frakcja, cfg);
        k.sprawdz(w_zakresie(linia, static_cast<double>(prog)),
                  "próg okupu kredytowego " + frakcja + " " + zakres_opis(linia),
                  std::to_string(prog));
        return;
    }

    // Ścieżka, którą w grze uruchamia odpowiedź modelu: kwota z wiadomości gracza +
    // próg + saldo => decyzja. Testujemy TĘ SAMĄ funkcję, której używa main.cpp.
    if (rodzaj == "okup_kredytowy") {
        const std::string wiadomosc = linia.value("wiadomosc", std::string{});
        const std::int64_t oferta = parse_ransom_amount(wiadomosc);
        const std::int64_t prog = engine.cash_ransom_threshold(frakcja, cfg);
        const OcenaOkupu ocena = ocen_oferte_okupu(oferta, prog, engine.player_balance());
        const char* nazwa = ocena == OcenaOkupu::Wiazaca      ? "pokoj"
                            : ocena == OcenaOkupu::BezPokrycia ? "pusta_obietnica"
                            : ocena == OcenaOkupu::ZaMalo      ? "za_malo"
                                                                : "brak";
        const std::string oczekiwany = linia.value("wynik", std::string{"pokoj"});
        k.sprawdz(oczekiwany == nazwa,
                  "okup kredytowy " + frakcja + " (oferta " + std::to_string(oferta) + ", próg " +
                      std::to_string(prog) + ", saldo " + std::to_string(engine.player_balance()) +
                      ") => " + oczekiwany,
                  nazwa);
        // Jak w main.cpp: oferta wiążąca kończy rajd niezależnie od decyzji modelu.
        if (ocena == OcenaOkupu::Wiazaca) {
            engine.apply_deescalation(frakcja, cfg, now_ms, oferta);
            for (auto& sd : engine.take_standdowns()) {
                buf.standdowny.push_back(std::move(sd));
            }
        }
        return;
    }

    if (rodzaj == "radio") {
        const std::string szukany = linia.value("zawiera", std::string{});
        bool jest = false;
        std::string bylo;
        for (const RadioOut& r : buf.radia) {
            bylo += (bylo.empty() ? "" : " | ") + r.faction + ": " + r.text;
            if (r.faction == frakcja &&
                (szukany.empty() || r.text.find(szukany) != std::string::npos ||
                 r.context.find(szukany) != std::string::npos)) {
                jest = true;
            }
        }
        k.sprawdz(jest, "radio od " + frakcja + (szukany.empty() ? "" : " ze wzmianką \"" + szukany + "\""),
                  bylo.empty() ? "(cisza)" : bylo);
        return;
    }

    k.fail("nieznany rodzaj oczekiwania \"" + rodzaj + "\"",
           "popraw scenariusz albo dopisz obsługę w scenariusz.cpp");
}

} // namespace

std::int64_t parse_ransom_amount(const std::string& text) {
    for (std::size_t i = 0; i < text.size();) {
        if (text[i] >= '0' && text[i] <= '9') {
            std::size_t j = i;
            while (j < text.size() && text[j] >= '0' && text[j] <= '9') {
                ++j;
            }
            if (j - i >= 2 && j - i <= 12) { // ignoruj pojedyncze cyfry i absurdy (overflow)
                std::int64_t val = 0;
                for (std::size_t k = i; k < j; ++k) {
                    val = val * 10 + (text[k] - '0');
                }
                return val;
            }
            i = j;
        } else {
            ++i;
        }
    }
    return 0;
}

OcenaOkupu ocen_oferte_okupu(std::int64_t oferta, std::int64_t prog, std::int64_t saldo) {
    if (prog <= 0 || saldo < 0) {
        // Bramka wyłączona albo saldo nieznane — nie kupujemy obietnic, których nie da
        // się sprawdzić (mod dosyła saldo w chat_message; -1 = nie wie).
        return OcenaOkupu::Brak;
    }
    if (oferta < prog) {
        return OcenaOkupu::ZaMalo;
    }
    return saldo >= oferta ? OcenaOkupu::Wiazaca : OcenaOkupu::BezPokrycia;
}

WynikScenariusza uruchom_scenariusz(const std::string& sciezka, const Config& cfg,
                                    const std::string& fallback_path, std::ostream& out) {
    WynikScenariusza wynik;
    std::ifstream in(sciezka, std::ios::binary);
    if (!in) {
        out << "[brain] nie można otworzyć scenariusza: " << sciezka << "\n";
        return wynik;
    }
    wynik.plik_wczytany = true;

    Db db(":memory:");
    Fallback fallback(fallback_path);
    std::optional<Engine> engine;
    engine.emplace(db, fallback, /*rng_seed=*/1337);

    Kontrola kontrola(out, wynik);
    Bufory buf;
    std::string linia;
    std::int64_t ts = 0;
    int numer = 0;

    while (std::getline(in, linia)) {
        ++numer;
        if (linia.empty() || linia[0] == '#') {
            continue; // pusta linia i komentarz `#` — wygoda przy pisaniu scenariuszy
        }
        nlohmann::json j;
        try {
            j = nlohmann::json::parse(linia);
        } catch (const nlohmann::json::exception& e) {
            out << "  linia " << numer << ": pominięta (zły JSON) — " << e.what() << "\n";
            continue;
        }

        if (j.contains("opis")) {
            out << "\n== " << j["opis"].get<std::string>() << "\n";
            continue;
        }

        if (j.value("restart", false)) {
            // Nowy Engine na tej samej bazie: co siedziało w SQLite, ma przeżyć; co
            // trzymaliśmy w RAM (cooldown radia, ostatnio wysłane ceny) — nie.
            engine.emplace(db, fallback, /*rng_seed=*/1337);
            out << "  [restart brainu — baza zostaje]\n";
            continue;
        }

        if (j.contains("oczekuj")) {
            sprawdz_oczekiwanie(j, buf, db, *engine, cfg, ts, kontrola);
            continue;
        }

        // Zwykłe zdarzenie z gry.
        Event ev;
        ev.type = j.value("type", std::string{});
        ev.data = j.value("data", nlohmann::json::object());
        ts = j.value("ts", ts);

        buf.wyczysc();
        for (RadioOut& r : engine->on_event(ev, cfg, ts)) {
            buf.radia.push_back(std::move(r));
        }
        for (RadioOut& r : engine->tick(cfg, ts)) {
            buf.radia.push_back(std::move(r));
        }
        buf.spawny = engine->take_spawns();
        buf.kontrakty = engine->take_contracts();
        buf.reputacje = engine->take_reputations();
        buf.ceny = engine->take_prices();
        buf.standdowny = engine->take_standdowns();
        buf.okupy = engine->take_ransom_demands();

        out << "  -> " << ev.type;
        if (!buf.ceny.empty()) {
            out << " | ceny: " << opis_cen(buf.ceny);
        }
        if (!buf.kontrakty.empty()) {
            out << " | zlecenia: " << opis_kontraktow(buf.kontrakty);
        }
        out << "\n";
    }

    out << "\n[brain] scenariusz " << sciezka << ": " << wynik.sprawdzen << " sprawdzeń, "
        << wynik.bledow << " błędów. Relacje: " << engine->relations_report() << "\n";
    return wynik;
}

} // namespace zf
