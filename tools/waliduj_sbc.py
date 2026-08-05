#!/usr/bin/env python3
"""Walidator plików SBC moda SE_ZyweFrakcje.

PO CO TO JEST. Gra nie mówi, że grupa spawnu wskazuje na nieistniejące zachowanie —
po prostu nic się nie spawnuje albo statek dryfuje. Każda taka literówka kosztowała
dotąd pełne wczytanie świata i sesję zgadywania. Ten skrypt czyta te same pliki, co
gra, i sprawdza to, czego gra nie sprawdzi za nas: czy wszystkie referencje między
SpawnGroups.sbc, RivalAiBehaviors.sbc, ZF_Manipulations.sbc i ZF_Boty.sbc trafiają
w coś, co naprawdę istnieje, oraz czy trzymają się reguł MES spisanych w CLAUDE.md.

CZEGO NIE SPRAWDZA. Nie wie, czy vanillowy prefab (C33_Military_Enforcer itd.)
istnieje w grze — tego nie da się ustalić bez plików gry. Zamiast zgadywać, trzyma
jawną tabelę ZNANE_PREFABY: prefab spoza niej to błąd z prośbą o dopisanie, więc
założenie przestaje być niewidoczne i wchodzi do przeglądu kodu. Tak samo działa
ZNANE_POSTACIE dla [BotType] — 2026-08-02 okazało się, że wpisane tam z opisu na
Workshopie „Police_Bot" i „Space_Skeleton" NIE ISTNIEJĄ, a AiEnabled przerywa wtedy
spawn po cichu (ostrzeżenie idzie tylko do jego własnego logu).

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

# Postacie, które [BotType] może wskazać. AiEnabled buduje AllowedBotSubtypes z
# MyDefinitionManager.Static.Characters (`charDef.Name ?? SubtypeId`), więc lista musi
# odpowiadać definicjom postaci W GRZE — spisane 2026-08-02 z Characters.sbc
# i Characters_Npc.sbc. Wartość True = Skeleton Humanoid (bot chodzi i używa narzędzi);
# pająki i wilk zostawione świadomie, ale oznaczone, bo do załogi się nie nadają.
ZNANE_POSTACIE = {
    "Default_Astronaut": True,
    "Default_Astronaut_Female": True,
    "NPC_Astronaut": True,
    "NPC_Astronaut_Female": True,
    "Space_spider": False,
    "Space_spider_black": False,
    "Space_spider_brown": False,
    "Space_spider_green": False,
    "Space_Wolf": False,
}

# Suma enumów BotRoleFriendly/BotRoleEnemy/BotRoleNeutral z AiEnabled (BotFactory.cs).
# AiEnabled porównuje przez ToUpperInvariant(), więc i my porównujemy bez wielkości liter.
ROLE_AIENABLED = {
    "REPAIR", "SCAVENGER", "COMBAT", "CREW",
    "ZOMBIE", "SOLDIER", "BRUISER", "GRINDER", "GHOST", "CREATURE",
    "NOMAD", "ENFORCER", "PATRON",
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

    # [BotType] / [BotBehavior] w profilach botów. Gra ich NIE waliduje: AiEnabled przerywa
    # spawn dla nieznanego podtypu i pisze o tym wyłącznie do własnego logu, więc objawem
    # jest pusty pokład bez śladu w logu SE (tak przepadły „Police_Bot" i „Space_Skeleton").
    for nazwa, dane in sorted(komponenty.items()):
        if dane["rodzaj"] != "bot":
            continue
        for wartosc in dane["tagi"].get("BotType", []):
            postac = wartosc.strip()
            if postac not in ZNANE_POSTACIE:
                wynik.blad(dane["plik"],
                           "{}: [BotType:{}] nie jest postacią znaną w grze — AiEnabled "
                           "przerwie spawn po cichu. Dozwolone: {} (albo dopisz nową do "
                           "ZNANE_POSTACIE w tools/waliduj_sbc.py)"
                           .format(nazwa, postac, ", ".join(sorted(ZNANE_POSTACIE))))
            elif not ZNANE_POSTACIE[postac]:
                wynik.ostrzez(dane["plik"],
                              "{}: [BotType:{}] to postać nie-humanoidalna — do załogi "
                              "stacji/statku raczej się nie nadaje".format(nazwa, postac))
        for wartosc in dane["tagi"].get("BotBehavior", []):
            rola = wartosc.strip()
            if rola.upper() not in ROLE_AIENABLED:
                wynik.blad(dane["plik"],
                           "{}: [BotBehavior:{}] nie jest rolą AiEnabled — spawn zostanie "
                           "odrzucony. Dozwolone: {}"
                           .format(nazwa, rola, ", ".join(sorted(ROLE_AIENABLED))))

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
        # [ManipulationGroups] NIE JEST rozwiązaniem (dekompilacja MES 2026-08-02,
        # patrz ZF_Manipulations.sbc): ArmorModuleReplacement wstawia tylko bloki z
        # zamkniętej listy modułów, RemoteControl na niej nie ma. Dla DUŻYCH siatek pilota
        # dokłada TestSpawner.EnsurePilot na żywej siatce po spawnie (dowolny duży kadłub
        # bez zdalnego sterowania, niezależnie od tagów w tym pliku) — więc to już nie jest
        # błąd danych. Ostrzegamy tylko dla MAŁYCH siatek: EnsurePilot celowo je pomija
        # (małe drony mają własne zdalne sterowanie w prefabie), więc brak RC na małej
        # siatce naprawdę zostawia statek bez pilota.
        if prawda(tagi, "UseRivalAi"):
            for prefab in grupa["prefaby"]:
                dane = ZNANE_PREFABY.get(prefab["subtype"])
                if dane and dane[0] == "Small" and not dane[1]:
                    wynik.blad(plik,
                               "{}: prefab {} (mała siatka) nie ma bloku zdalnego sterowania — "
                               "TestSpawner.EnsurePilot obsługuje tylko DUŻE siatki, więc ten "
                               "statek będzie dryfował. Dodaj RC do prefabu albo rozszerz "
                               "EnsurePilot o mały wariant (RivalAIRemoteControlSmall)"
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

    # EnsurePilot musi ODTWORZYĆ profil, który grupa wskazuje w <Behaviour>.
    # Duże kadłuby nie mają bloku zdalnego sterowania, więc MES nie ma gdzie zapisać
    # zachowania i całą treść CustomData pisze nasz TestSpawner.EnsurePilot. Jeśli grupa
    # mówi ZF_Fighter_HEL (wariant z załogą), a kod wpisuje goły ZF_Fighter, to statek
    # lata i strzela, ale jest PUSTY — i nic tego nie zgłasza, bo obie nazwy istnieją.
    # Dokładnie ten błąd popełniono 2026-08-02 przy naprawie pilota.
    for nazwa_grupy in sorted(grupy):
        for prefab in grupy[nazwa_grupy]["prefaby"]:
            zachowanie = prefab.get("zachowanie")
            dane_prefabu = ZNANE_PREFABY.get(prefab["subtype"])
            # Dotyczy wyłącznie kadłubów, którym pilota dokłada kod (duże, bez własnego RC).
            if not zachowanie or not dane_prefabu or dane_prefabu[0] != "Large" or dane_prefabu[1]:
                continue
            m = re.match(r"^ZF_Fighter_({})$".format("|".join(NASZE_TAGI)), zachowanie)
            if not m:
                continue
            trigger = "ZF_Trigger_Zaloga_" + m.group(1)
            # Kod może wpisać nazwę wprost ALBO skleić ją z tagiem w czasie działania
            # (\"ZF_Trigger_Zaloga_\" + tag) — obie formy są poprawne.
            #
            # POPRAWKA 2026-08-04: drugi warunek brzmiał `"ZF_Trigger_Zaloga_" not in zrodlo`
            # i był ZAWSZE fałszywy, gdy kod sklejał nazwę — czyli reguła wykrywała wyłącznie
            # całkowity zanik prefiksu. Wpisanie na sztywno JEDNEGO tagu dla wszystkich frakcji
            # (np. zawsze ZF_Trigger_Zaloga_HEL) przechodziło na zielono, a to najbardziej
            # prawdopodobna pomyłka przy tej nazwie. Teraz sklejanie uznajemy tylko wtedy, gdy
            # kod naprawdę dokleja ZMIENNĄ, a nie stały tag.
            # Uwaga na kształt literału: prefiks stoi na KOŃCU dłuższego napisu
            # ("\n[Triggers:ZF_Trigger_Zaloga_"), więc nie wolno wymagać cudzysłowu przed nim.
            sklejane = re.search(
                r'ZF_Trigger_Zaloga_"\s*\+\s*(?!")', zrodlo) is not None
            if trigger not in zrodlo and not sklejane:
                wynik.blad("TestSpawner.cs",
                           "grupa {} używa zachowania {} (z załogą), ale EnsurePilot nie wpisuje "
                           "\"{}\" — statek powstanie bez załogi. Kod musi odtworzyć profil "
                           "z ZF_Boty.sbc, bo MES nie ma go gdzie zapisać"
                           .format(nazwa_grupy, zachowanie, trigger))

    # Crew.cs stawia boty PROGRAMOWO (stacje nie przechodzą przez profile MES), więc ta
    # sama pomyłka co w ZF_Boty.sbc może siedzieć w tablicach C#. Sprawdzamy je tak samo —
    # dokładnie tu żył „Police_Bot", którego nie ma w grze.
    plik_zalogi = os.path.join(korzen_skryptow, "Crew.cs")
    try:
        with open(plik_zalogi, encoding="utf-8") as f:
            zrodlo_zalogi = f.read()
    except OSError:
        zrodlo_zalogi = None
    if zrodlo_zalogi is not None:
        m = re.search(r"string\[\]\s+BotType\s*=\s*\{([^}]*)\}", zrodlo_zalogi, re.S)
        if not m:
            wynik.ostrzez("Crew.cs",
                          "nie znalazłem tablicy BotType — walidator wymaga aktualizacji")
        else:
            for postac in re.findall(r'"([^"]+)"', m.group(1)):
                if postac not in ZNANE_POSTACIE:
                    wynik.blad("Crew.cs",
                               "BotType \"{}\" nie jest postacią znaną w grze — AiEnabled "
                               "przerwie spawn po cichu (patrz ZNANE_POSTACIE)".format(postac))
        m = re.search(r"string\[\]\s+Role\s*=\s*\{([^}]*)\}", zrodlo_zalogi, re.S)
        if m:
            for rola in re.findall(r'"([^"]+)"', m.group(1)):
                if rola.upper() not in ROLE_AIENABLED:
                    wynik.blad("Crew.cs",
                               "Role \"{}\" nie jest rolą AiEnabled — spawn zostanie odrzucony"
                               .format(rola))

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
