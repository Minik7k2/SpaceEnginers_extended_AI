#!/usr/bin/env python3
"""Walidator plików SBC moda SE_ZyweFrakcje.

PO CO TO JEST. Gra nie mówi, że grupa spawnu wskazuje na nieistniejące zachowanie —
po prostu nic się nie spawnuje albo statek dryfuje. Każda taka literówka kosztowała
dotąd pełne wczytanie świata i sesję zgadywania. Ten skrypt czyta te same pliki, co
gra, i sprawdza to, czego gra nie sprawdzi za nas: czy wszystkie referencje między
SpawnGroups.sbc, RivalAiBehaviors.sbc, ZF_Manipulations.sbc i ZF_Boty.sbc trafiają
w coś, co naprawdę istnieje, oraz czy trzymają się reguł MES spisanych w CLAUDE.md.

CZEGO NIE SPRAWDZA. Nie wie, czy vanillowy prefab (C33_Military_Enforcer itd.)
istnieje w grze ani czy `[BotType:Police_Bot]` to prawidłowa nazwa w AiEnabled —
tego nie da się ustalić bez plików gry. Zamiast zgadywać, trzyma jawną tabelę
ZNANE_PREFABY: prefab spoza niej to błąd z prośbą o dopisanie, więc założenie
przestaje być niewidoczne i wchodzi do przeglądu kodu.

Użycie: python3 tools/waliduj_sbc.py [--korzen <katalog moda>]
Kod wyjścia: 0 = czysto, 1 = błędy.
"""

import argparse
import os
import re
import sys
import xml.etree.ElementTree as ET

# Vanillowe prefaby używane przez mod. Rozmiar i obecność bloku zdalnego sterowania
# spisane 2026-08-01 przy doborze flot (przeskanowane po xsi:type bloków) — to jedyne
# źródło, jakie mamy bez plików gry. Gdy dokładasz prefab do SpawnGroups.sbc albo do
# Stations.cs, dopisz go TUTAJ; walidator wymusza to celowo.
ZNANE_PREFABY = {
    # nazwa: (rozmiar siatki, czy ma blok zdalnego sterowania)
    "DS_Assault_Support": ("Small", True),
    "DS_AutomatedGunShip": ("Small", True),
    "DS_PirateBeaconDrone": ("Small", True),
    "DS_Pirate_Scavenger": ("Small", True),
    "DS_Pirate_Scimitar": ("Small", True),
    "DS_Pirate_ShakedownDrone": ("Small", True),
    "C33_Military_Enforcer": ("Large", False),
    "C22_Trade_Merchant": ("Large", False),
    "C40_Pirate_Vulture": ("Large", False),
    "C42_Pirate_SalvageCarrier": ("Large", False),
    "C12_Mining_Armed_Tender": ("Large", False),
    "C10_Mining_Carriage": ("Large", False),
    # Stacje stawiane przez StationSpawner (Stations.cs).
    "GE_LogisticsFacility": ("Large", False),
    "RE19_PirateDepot": ("Large", False),
    "RE05_StagingStation": ("Large", False),
}

NASZE_TAGI = ("HEL", "KRW", "WGR")

# Wartości dozwolone przez wiki MES ("Behaviors: Getting Started").
ZACHOWANIA_MES = {
    "CargoShip", "Escort", "Fighter", "HorseFighter", "Horsefly",
    "Hunter", "Nautical", "Passive", "Patrol", "Strike",
}

TAG_RE = re.compile(r"\[([A-Za-z0-9_]+):([^\]]*)\]")


class Wynik:
    """Zbiera błędy i ostrzeżenia, żeby raport był kompletny za jednym przebiegiem."""

    def __init__(self):
        self.bledy = []
        self.ostrzezenia = []

    def blad(self, plik, tekst):
        self.bledy.append("BŁĄD       {}: {}".format(plik, tekst))

    def ostrzez(self, plik, tekst):
        self.ostrzezenia.append("OSTRZEŻENIE {}: {}".format(plik, tekst))

    def raport(self):
        for w in self.ostrzezenia:
            print(w)
        for b in self.bledy:
            print(b)
        print("")
        print("Wynik: {} błąd(ów), {} ostrzeżeń.".format(len(self.bledy), len(self.ostrzezenia)))
        return 1 if self.bledy else 0


def tagi_z_opisu(opis):
    """[Klucz:wartość] z <Description> → {klucz: [wartości]} (klucz może się powtarzać)."""
    out = {}
    for klucz, wartosc in TAG_RE.findall(opis or ""):
        out.setdefault(klucz, []).append(wartosc.strip())
    return out


def pierwszy(tagi, klucz, domyslnie=None):
    wartosci = tagi.get(klucz)
    return wartosci[0] if wartosci else domyslnie


def prawda(tagi, klucz):
    return (pierwszy(tagi, klucz, "false") or "").lower() == "true"


def wczytaj_sbc(korzen, wynik):
    """Parsuje wszystkie .sbc. Zwraca (grupy, komponenty, prefaby_wlasne)."""
    grupy, komponenty, prefaby = {}, {}, {}

    for katalog, _, pliki in os.walk(korzen):
        for nazwa in sorted(pliki):
            if not nazwa.endswith(".sbc"):
                continue
            sciezka = os.path.join(katalog, nazwa)
            wzgledna = os.path.relpath(sciezka, os.path.dirname(korzen.rstrip(os.sep)))
            try:
                root = ET.parse(sciezka).getroot()
            except ET.ParseError as e:
                wynik.blad(wzgledna, "nie parsuje się jako XML — {}".format(e))
                continue

            for grupa in root.iter("SpawnGroup"):
                subtype = (grupa.findtext("Id/SubtypeId") or "").strip()
                if not subtype:
                    wynik.blad(wzgledna, "SpawnGroup bez <SubtypeId>")
                    continue
                if subtype in grupy:
                    wynik.blad(wzgledna, "zdublowany SpawnGroup {}".format(subtype))
                grupy[subtype] = {
                    "plik": wzgledna,
                    "tagi": tagi_z_opisu(grupa.findtext("Description")),
                    "prefaby": [
                        {
                            "subtype": (p.get("SubtypeId") or "").strip(),
                            "zachowanie": (p.findtext("Behaviour") or "").strip(),
                        }
                        for p in grupa.iter("Prefab")
                    ],
                }

            for komp in root.iter("EntityComponent"):
                subtype = (komp.findtext("Id/SubtypeId") or "").strip()
                if not subtype:
                    continue
                opis = komp.findtext("Description") or ""
                if subtype in komponenty:
                    wynik.blad(wzgledna, "zdublowany EntityComponent {}".format(subtype))
                komponenty[subtype] = {
                    "plik": wzgledna,
                    "tagi": tagi_z_opisu(opis),
                    # Nagłówek w nawiasach kwadratowych bez dwukropka mówi, CZYM jest profil
                    # ([RivalAI Behavior], [MES Bot Spawn]...). Po nim rozpoznajemy rodzaj.
                    "rodzaj": _rodzaj_profilu(opis),
                }

            for pref in root.iter("Prefab"):
                ident = pref.find("Id")
                if ident is None:
                    continue  # <Prefab SubtypeId=...> wewnątrz SpawnGroup, nie definicja
                subtype = (ident.get("Subtype") or "").strip()
                if not subtype:
                    continue
                rozmiary = {(g.findtext("GridSizeEnum") or "").strip()
                            for g in pref.iter("CubeGrid")}
                prefaby[subtype] = {
                    "plik": wzgledna,
                    "rozmiar": "Large" if "Large" in rozmiary else "Small",
                }

    return grupy, komponenty, prefaby


def _rodzaj_profilu(opis):
    for naglowek, rodzaj in (
        ("[RivalAI Behavior]", "zachowanie"),
        ("[RivalAI Action]", "akcja"),
        ("[RivalAI Trigger]", "trigger"),
        ("[MES Bot Spawn]", "bot"),
        ("[MES Manipulation Group]", "grupa-manipulacji"),
        ("[MES Manipulation]", "manipulacja"),
    ):
        if naglowek in opis:
            return rodzaj
    return "?"


def sprawdz_referencje(grupy, komponenty, wynik):
    """Każda nazwa wskazana tagiem musi istnieć — to najczęstsza cicha awaria."""
    # (tag w opisie, oczekiwany rodzaj profilu)
    powiazania = [
        ("ManipulationGroups", "grupa-manipulacji"),
        ("ManipulationProfiles", "manipulacja"),
        ("Triggers", "zachowanie->trigger"),
        ("Actions", "trigger->akcja"),
        ("BotSpawnProfileNames", "akcja->bot"),
    ]
    oczekiwany_rodzaj = {
        "ManipulationGroups": "grupa-manipulacji",
        "ManipulationProfiles": "manipulacja",
        "Triggers": "trigger",
        "Actions": "akcja",
        "BotSpawnProfileNames": "bot",
    }

    for zrodlo in (grupy, komponenty):
        for nazwa, dane in sorted(zrodlo.items()):
            for tag, _ in powiazania:
                for wartosc in dane["tagi"].get(tag, []):
                    # MES przyjmuje listę po przecinku ORAZ powtórzony tag.
                    for cel in [c.strip() for c in wartosc.split(",") if c.strip()]:
                        if cel not in komponenty:
                            wynik.blad(dane["plik"],
                                       "{}: [{}:{}] wskazuje na nieistniejący profil"
                                       .format(nazwa, tag, cel))
                            continue
                        rodzaj = komponenty[cel]["rodzaj"]
                        chciany = oczekiwany_rodzaj[tag]
                        if rodzaj != chciany:
                            wynik.blad(dane["plik"],
                                       "{}: [{}:{}] wskazuje na profil rodzaju \"{}\", "
                                       "a powinien na \"{}\"".format(nazwa, tag, cel, rodzaj, chciany))

    # <Behaviour> prefaba → profil zachowania RivalAI.
    for nazwa, grupa in sorted(grupy.items()):
        for prefab in grupa["prefaby"]:
            zachowanie = prefab["zachowanie"]
            if not zachowanie:
                continue
            if zachowanie not in komponenty:
                wynik.blad(grupa["plik"],
                           "{}: prefab {} wskazuje <Behaviour>{}</Behaviour>, a takiego "
                           "profilu nie ma".format(nazwa, prefab["subtype"], zachowanie))
            elif komponenty[zachowanie]["rodzaj"] != "zachowanie":
                wynik.blad(grupa["plik"],
                           "{}: <Behaviour>{}</Behaviour> nie jest profilem [RivalAI Behavior]"
                           .format(nazwa, zachowanie))

    # BehaviorName musi być jedną z wartości, które RivalAI zna.
    for nazwa, komp in sorted(komponenty.items()):
        if komp["rodzaj"] != "zachowanie":
            continue
        behavior = pierwszy(komp["tagi"], "BehaviorName")
        if behavior is None:
            wynik.blad(komp["plik"], "{}: profil zachowania bez [BehaviorName]".format(nazwa))
        elif behavior not in ZACHOWANIA_MES:
            wynik.blad(komp["plik"],
                       "{}: [BehaviorName:{}] spoza listy MES ({})"
                       .format(nazwa, behavior, ", ".join(sorted(ZACHOWANIA_MES))))


def sprawdz_grupy(grupy, prefaby_wlasne, wynik):
    """Reguły MES i pułapki spisane w CLAUDE.md — to one kosztowały sesje w grze."""
    for nazwa, grupa in sorted(grupy.items()):
        plik = grupa["plik"]
        tagi = grupa["tagi"]

        if not grupa["prefaby"]:
            wynik.blad(plik, "{}: grupa bez żadnego <Prefab>".format(nazwa))

        # Bez tego tagu MESApi.CustomSpawnRequest odrzuca grupę (komentarz w SpawnGroups.sbc).
        if not prawda(tagi, "RivalAiSpawn"):
            wynik.blad(plik, "{}: brak [RivalAiSpawn:true] — CustomSpawnRequest odrzuci grupę"
                       .format(nazwa))

        rozmiary = set()
        for prefab in grupa["prefaby"]:
            subtype = prefab["subtype"]
            if not subtype:
                wynik.blad(plik, "{}: <Prefab> bez SubtypeId".format(nazwa))
                continue
            if subtype in prefaby_wlasne:
                rozmiary.add(prefaby_wlasne[subtype]["rozmiar"])
            elif subtype in ZNANE_PREFABY:
                rozmiary.add(ZNANE_PREFABY[subtype][0])
            else:
                wynik.blad(plik,
                           "{}: prefab {} nie jest ani nasz, ani w tabeli ZNANE_PREFABY — "
                           "dopisz go do tools/waliduj_sbc.py razem z rozmiarem siatki"
                           .format(nazwa, subtype))

        # PUŁAPKA z CLAUDE.md: grupa z manipulacją pilota musi być JEDNOROZMIAROWA,
        # bo mała siatka dostałaby blok nie na swój rozmiar.
        for grupa_manip in tagi.get("ManipulationGroups", []):
            wymagany = _rozmiar_wymagany_przez_manipulacje(grupa_manip, grupy, wynik)
            if wymagany is None:
                continue
            if len(rozmiary) > 1:
                wynik.blad(plik,
                           "{}: grupa z [ManipulationGroups:{}] miesza siatki {} — manipulacja "
                           "wstawia blok jednego rozmiaru, więc grupa musi być jednorozmiarowa"
                           .format(nazwa, grupa_manip, "/".join(sorted(rozmiary))))
            elif rozmiary and wymagany not in rozmiary:
                wynik.blad(plik,
                           "{}: [ManipulationGroups:{}] wstawia blok dla siatki {}, a grupa "
                           "ma siatki {}".format(nazwa, grupa_manip, wymagany,
                                                 "/".join(sorted(rozmiary))))

        # Sedno awarii „konwoje dryfują": RivalAI poprowadzi grid tylko z blokiem
        # zdalnego sterowania, a [RivalAiReplaceRemoteControl] go PODMIENIA, nie dodaje.
        if prawda(tagi, "UseRivalAi") and not tagi.get("ManipulationGroups"):
            for prefab in grupa["prefaby"]:
                dane = ZNANE_PREFABY.get(prefab["subtype"])
                if dane and not dane[1]:
                    wynik.blad(plik,
                               "{}: prefab {} nie ma bloku zdalnego sterowania, a grupa liczy na "
                               "[RivalAiReplaceRemoteControl] — dodaj [ManipulationGroups:...] "
                               "z blokiem pilota, inaczej statek będzie dryfował"
                               .format(nazwa, prefab["subtype"]))


def _rozmiar_wymagany_przez_manipulacje(nazwa_grupy, grupy, wynik):
    """Large/Small wynikające z [ModulesForArmorReplacement] w profilach tej grupy."""
    del grupy  # profile manipulacji są w komponentach, przekazywanych przez domknięcie niżej
    return _ROZMIARY_MANIPULACJI.get(nazwa_grupy)


# Uzupełniane raz, po wczytaniu komponentów (patrz main) — trzymamy to osobno, żeby
# sprawdzanie grup nie musiało co chwilę przechodzić po profilach manipulacji.
_ROZMIARY_MANIPULACJI = {}


def zbierz_rozmiary_manipulacji(komponenty):
    for nazwa, komp in komponenty.items():
        if komp["rodzaj"] != "grupa-manipulacji":
            continue
        rozmiar = None
        for wartosc in komp["tagi"].get("ManipulationProfiles", []):
            for profil in [p.strip() for p in wartosc.split(",") if p.strip()]:
                dane = komponenty.get(profil)
                if not dane:
                    continue
                for modul in dane["tagi"].get("ModulesForArmorReplacement", []):
                    if modul.endswith("Large"):
                        rozmiar = "Large"
                    elif modul.endswith("Small"):
                        rozmiar = "Small"
        if rozmiar:
            _ROZMIARY_MANIPULACJI[nazwa] = rozmiar


def sprawdz_kod(korzen_skryptow, grupy, prefaby_wlasne, wynik):
    """Mod odwołuje się do danych po nazwach z kodu — te też muszą trafiać."""
    plik_spawnera = os.path.join(korzen_skryptow, "TestSpawner.cs")
    plik_stacji = os.path.join(korzen_skryptow, "Stations.cs")

    # TestSpawner.GroupForKind: baseName + "_" + TAG dla naszych frakcji, sam baseName dla obcych.
    try:
        with open(plik_spawnera, encoding="utf-8") as f:
            zrodlo = f.read()
    except OSError as e:
        wynik.blad("TestSpawner.cs", "nie mogę odczytać ({})".format(e))
        return

    bazy = re.findall(r'baseName = "(ZF_\w+)"', zrodlo)
    if not bazy:
        wynik.ostrzez("TestSpawner.cs",
                      "nie znalazłem nazw grup w GroupForKind — walidator wymaga aktualizacji")
    for baza in sorted(set(bazy)):
        oczekiwane = [baza] + ["{}_{}".format(baza, tag) for tag in NASZE_TAGI]
        for nazwa in oczekiwane:
            if nazwa not in grupy:
                wynik.blad("mod/Data/SpawnGroups.sbc",
                           "TestSpawner.GroupForKind może zażądać grupy {}, a jej nie ma"
                           .format(nazwa))

    # Prefaby rekwizytów i skrzynki zrzutu: const string ...Prefab = "...".
    for plik in sorted(os.listdir(korzen_skryptow)):
        if not plik.endswith(".cs"):
            continue
        with open(os.path.join(korzen_skryptow, plik), encoding="utf-8") as f:
            tresc = f.read()
        for nazwa in re.findall(r'const string \w*Prefab\w* = "([^"]+)"', tresc):
            if nazwa not in prefaby_wlasne and nazwa not in ZNANE_PREFABY:
                wynik.blad(plik,
                           "kod spawnuje prefab \"{}\", którego nie ma ani w mod/Data/Prefabs, "
                           "ani w tabeli ZNANE_PREFABY".format(nazwa))

    # StationSpawner: tablica Prefabs z gotowcami vanilli.
    try:
        with open(plik_stacji, encoding="utf-8") as f:
            zrodlo_stacji = f.read()
    except OSError as e:
        wynik.blad("Stations.cs", "nie mogę odczytać ({})".format(e))
        return

    blok = re.search(r"string\[\] Prefabs\s*=\s*\{(.*?)\};", zrodlo_stacji, re.S)
    if not blok:
        wynik.ostrzez("Stations.cs", "nie znalazłem tablicy Prefabs — walidator wymaga aktualizacji")
        return
    nazwy = re.findall(r'"([^"]+)"', blok.group(1))
    tagi_stacji = re.search(r"string\[\] Tags\s*=\s*\{(.*?)\};", zrodlo_stacji, re.S)
    liczba_tagow = len(re.findall(r'"([^"]+)"', tagi_stacji.group(1))) if tagi_stacji else 0
    if liczba_tagow and len(nazwy) != liczba_tagow:
        wynik.blad("Stations.cs",
                   "Tags ma {} pozycji, a Prefabs {} — tablice są indeksowane wspólnie"
                   .format(liczba_tagow, len(nazwy)))
    for nazwa in nazwy:
        if nazwa not in ZNANE_PREFABY and nazwa not in prefaby_wlasne:
            wynik.blad("Stations.cs",
                       "stacja z prefabu \"{}\" spoza tabeli ZNANE_PREFABY — dopisz go do "
                       "tools/waliduj_sbc.py".format(nazwa))
        elif nazwa in ZNANE_PREFABY and ZNANE_PREFABY[nazwa][0] != "Large":
            wynik.blad("Stations.cs",
                       "stacja z prefabu \"{}\" nie jest dużą siatką — bloki ekonomiczne "
                       "dokładamy jako duże".format(nazwa))


def sprawdz_frakcje(korzen, grupy, wynik):
    """Nasze trzy frakcje muszą istnieć w Factions.sbc i mieć komplet flot."""
    sciezka = os.path.join(korzen, "Factions.sbc")
    try:
        root = ET.parse(sciezka).getroot()
    except (ET.ParseError, OSError) as e:
        wynik.blad("mod/Data/Factions.sbc", "nie mogę wczytać ({})".format(e))
        return
    # Tag jest ATRYBUTEM <Faction Tag="HEL" ...>, nie elementem.
    tagi = {(f.get("Tag") or f.findtext("Tag") or "").strip() for f in root.iter("Faction")}
    for tag in NASZE_TAGI:
        if tag not in tagi:
            wynik.blad("mod/Data/Factions.sbc", "brak frakcji o tagu {}".format(tag))
        for rodzaj in ("ZF_Patrol", "ZF_Raid", "ZF_Convoy"):
            nazwa = "{}_{}".format(rodzaj, tag)
            if nazwa not in grupy:
                wynik.ostrzez("mod/Data/SpawnGroups.sbc",
                              "frakcja {} nie ma własnej grupy {} — poleci flotą ogólną"
                              .format(tag, nazwa))


def main():
    parser = argparse.ArgumentParser(description="Walidator plików SBC moda SE_ZyweFrakcje")
    parser.add_argument("--korzen", default="mod/Data",
                        help="katalog z danymi moda (domyślnie mod/Data)")
    args = parser.parse_args()

    if not os.path.isdir(args.korzen):
        print("Nie ma katalogu {} — uruchom z korzenia repozytorium.".format(args.korzen))
        return 1

    wynik = Wynik()
    grupy, komponenty, prefaby = wczytaj_sbc(args.korzen, wynik)
    zbierz_rozmiary_manipulacji(komponenty)

    print("Wczytano: {} grup spawnu, {} profili, {} własnych prefabów."
          .format(len(grupy), len(komponenty), len(prefaby)))

    sprawdz_referencje(grupy, komponenty, wynik)
    sprawdz_grupy(grupy, prefaby, wynik)
    sprawdz_frakcje(args.korzen, grupy, wynik)
    sprawdz_kod(os.path.join(args.korzen, "Scripts", "ZyweFrakcje"), grupy, prefaby, wynik)

    return wynik.raport()


if __name__ == "__main__":
    sys.exit(main())
