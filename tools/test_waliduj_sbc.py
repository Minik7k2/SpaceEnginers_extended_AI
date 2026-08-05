#!/usr/bin/env python3
"""Kontrtest walidatora SBC: psuje dane na kopii i sprawdza, że KAŻDY test krzyczy.

Bez tego walidator mógłby po cichu przestać cokolwiek sprawdzać (dokładnie tak, jak
testy C++ przechodziły „zielono" w Release, dopóki nie wymusiliśmy -UNDEBUG) —
a wtedy zielone CI znaczyłoby tylko tyle, że skrypt się nie wywrócił.

Użycie: python3 tools/test_waliduj_sbc.py
"""

import os
import re
import shutil
import subprocess
import sys
import tempfile

KORZEN = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
WALIDATOR = os.path.join(KORZEN, "tools", "waliduj_sbc.py")


def uruchom(korzen_danych):
    # encoding= JAWNIE (2026-08-04): bez tego `text=True` dekoduje po locale rodzica
    # (na polskim Windowsie cp1250), a walidator pisze UTF-8, gdy w środowisku stoi
    # PYTHONIOENCODING — kontrtest wywalał się wtedy na UnicodeDecodeError zamiast
    # cokolwiek sprawdzić. errors="replace", bo krzaki w komunikacie są mniej groźne
    # niż wywrócony test.
    proces = subprocess.run(
        [sys.executable, WALIDATOR, "--korzen", korzen_danych],
        capture_output=True, text=True, encoding="utf-8", errors="replace")
    return proces.returncode, proces.stdout + proces.stderr


def podmien(sciezka, szukaj, zamien, ile=1):
    with open(sciezka, encoding="utf-8") as f:
        tresc = f.read()
    if szukaj not in tresc:
        raise AssertionError("nie znalazłem w {}: {}".format(sciezka, szukaj))
    with open(sciezka, "w", encoding="utf-8") as f:
        f.write(tresc.replace(szukaj, zamien, ile))


def _dodaj_manipulacje(sciezka, nazwa_grupy):
    """Wstrzykuje [ManipulationGroups:<nazwa_grupy>] do PIERWSZEJ grupy z [UseRivalAi:true].

    Od naprawy z 2026-08-02 (dekompilacja MES: [ReplaceArmorBlocksWithModules] strukturalnie
    nie potrafi wstawić RemoteControl) żadna prawdziwa grupa w SpawnGroups.sbc już nie używa
    [ManipulationGroups] — pilota dokłada TestSpawner.EnsurePilot w C#. Walidacja odwołań
    [ManipulationGroups:...] i spójności rozmiarów siatek zostaje w kodzie (przyda się, gdy
    ktoś użyje manipulacji do czegoś innego niż pilot), ale bez wstrzyknięcia syntetycznego
    tagu nie ma już jak jej przetestować na prawdziwych danych.
    """
    # UWAGA: samo "[UseRivalAi:true]" trafia NAJPIERW w komentarz nagłówkowy pliku (ten sam
    # efekt jak przy [RivalAiSpawn:true] — patrz test niżej), więc kotwiczymy o dwie linie
    # razem, których komentarz nie zawiera (tam są w jednej linii, oddzielone " + ").
    kotwica = "[UseRivalAi:true]\n        [RivalAiReplaceRemoteControl:true]"
    podmien(sciezka, kotwica, kotwica + "\n        [ManipulationGroups:{}]".format(nazwa_grupy),
            ile=1)


# (opis, funkcja psująca dane, fragment oczekiwanego komunikatu)
USTERKI = [
    ("zachowanie wskazuje na nieistniejący profil",
     lambda d: podmien(os.path.join(d, "SpawnGroups.sbc"),
                       "<Behaviour>ZF_Fighter</Behaviour>",
                       "<Behaviour>ZF_Fighte</Behaviour>", ile=1),
     "takiego profilu nie ma"),

    ("trigger załogi wskazuje na nieistniejącą akcję",
     lambda d: podmien(os.path.join(d, "ZF_Boty.sbc"),
                       "[Actions:ZF_Akcja_Zaloga_HEL]",
                       "[Actions:ZF_Akcja_Zaloga_Helion]"),
     "wskazuje na nieistniejący profil"),

    ("akcja wskazuje na nieistniejący profil bota",
     lambda d: podmien(os.path.join(d, "ZF_Boty.sbc"),
                       "[BotSpawnProfileNames:ZF_Bot_WGR_Zaloga]",
                       "[BotSpawnProfileNames:ZF_Bot_WGR_Zaloge]"),
     "wskazuje na nieistniejący profil"),

    ("grupa manipulacji wskazuje w próżnię",
     lambda d: _dodaj_manipulacje(os.path.join(d, "SpawnGroups.sbc"),
                                  "ZF_ManipulacjaGrupa_Pilotow"),
     "wskazuje na nieistniejący profil"),

    # ile=99, bo pierwsze wystąpienie siedzi w komentarzu nagłówkowym pliku (parser XML
    # komentarze pomija) — podmiana tylko tam nie zepsułaby żadnej grupy.
    ("brak [RivalAiSpawn:true] — MES odrzuci grupę",
     lambda d: podmien(os.path.join(d, "SpawnGroups.sbc"),
                       "[RivalAiSpawn:true]", "[RivalAiSpawn:false]", ile=99),
     "CustomSpawnRequest odrzuci grupę"),

    ("BehaviorName spoza listy MES",
     lambda d: podmien(os.path.join(d, "RivalAiBehaviors.sbc"),
                       "[BehaviorName:CargoShip]", "[BehaviorName:Konwoj]"),
     "spoza listy MES"),

    ("nieznany prefab w grupie spawnu",
     lambda d: podmien(os.path.join(d, "SpawnGroups.sbc"),
                       'SubtypeId="C40_Pirate_Vulture"', 'SubtypeId="C40_Pirate_Vultur"'),
     "ZNANE_PREFABY"),

    ("mała siatka w grupie z manipulacją dla dużej",
     lambda d: (_dodaj_manipulacje(os.path.join(d, "SpawnGroups.sbc"),
                                   "ZF_ManipulacjaGrupa_Pilot"),
                podmien(os.path.join(d, "SpawnGroups.sbc"),
                       'SubtypeId="C33_Military_Enforcer"',
                       'SubtypeId="DS_Pirate_ShakedownDrone"')),
     "wstawia blok dla siatki Large"),

    # BRAK testu dla „mała siatka bez zdalnego sterowania": wszystkie prefaby Small w
    # ZNANE_PREFABY mają dziś RC=True (żaden znany mały kadłub w naszych danych go nie
    # brakuje), a tabela to prawdziwy spis z gry — nie wolno wpisać fikcyjnego wpisu tylko
    # pod test. Ścieżka w kodzie (waliduj_sbc.py, sekcja "Sedno awarii") zadziała, gdy
    # taki prefab się pojawi; do tego czasu jest niepokryta uczciwie, zamiast fałszywie.

    # Regresja z 2026-08-02: rajd wskazuje ZF_Fighter_<TAG> (wariant Z ZAŁOGĄ), ale kod
    # wpisuje do CustomData goły Fighter — statek lata i strzela, tylko jest pusty w środku.
    # Obie nazwy istnieją, więc sprawdzanie samych referencji tego nie łapie.
    ("EnsurePilot gubi trigger załogi z ZF_Fighter_TAG",
     lambda d: podmien(os.path.join(d, "Scripts", "ZyweFrakcje", "TestSpawner.cs"),
                       "ZF_Trigger_Zaloga_", "ZF_Trigger_Nieistniejacy_", ile=99),
     "statek powstanie bez załogi"),

    # Regresja z 2026-08-02: [BotType] wzięty z opisu na Workshopie wskazywał postać,
    # której NIE MA w grze. AiEnabled przerywał wtedy spawn, pisząc tylko do własnego logu —
    # w grze objawem był pusty pokład i cisza w logu SE.
    ("[BotType] wskazuje postać, której nie ma w grze",
     lambda d: podmien(os.path.join(d, "ZF_Boty.sbc"),
                       "[BotType:Default_Astronaut]", "[BotType:Police_Bot]", ile=99),
     "nie jest postacią znaną w grze"),

    ("[BotBehavior] spoza ról AiEnabled",
     lambda d: podmien(os.path.join(d, "ZF_Boty.sbc"),
                       "[BotBehavior:Grinder]", "[BotBehavior:Szlifierz]"),
     "nie jest rolą AiEnabled"),

    ("Crew.cs: BotType spoza postaci znanych w grze",
     lambda d: podmien(os.path.join(d, "Scripts", "ZyweFrakcje", "Crew.cs"),
                       '"Default_Astronaut", "Default_Astronaut", "Default_Astronaut"',
                       '"Police_Bot", "Police_Bot", "Police_Bot"'),
     "nie jest postacią znaną w grze"),

    # Ta sama pomyłka co w [BotBehavior], tylko po stronie C# — reguła istniała od
    # 2026-08-02, ale nie miała kontrtestu, więc nikt nie wiedział, czy działa.
    ("Crew.cs: Role spoza ról AiEnabled",
     lambda d: podmien(os.path.join(d, "Scripts", "ZyweFrakcje", "Crew.cs"),
                       '"Soldier", "Soldier", "Soldier"',
                       '"Zolnierz", "Zolnierz", "Zolnierz"'),
     "nie jest rolą AiEnabled"),

    # Najbardziej prawdopodobna pomyłka przy nazwie triggera: nie zanik prefiksu, tylko
    # zabetonowanie JEDNEGO tagu dla wszystkich frakcji. Do 2026-08-04 przechodziło to na
    # zielono, bo reguła zadowalała się samym prefiksem gdziekolwiek w pliku.
    ("EnsurePilot wpisuje trigger załogi na sztywno dla jednego tagu",
     lambda d: podmien(os.path.join(d, "Scripts", "ZyweFrakcje", "TestSpawner.cs"),
                       r'"\n[Triggers:ZF_Trigger_Zaloga_" + tag + "]"',
                       r'"\n[Triggers:ZF_Trigger_Zaloga_HEL]"'),
     "statek powstanie bez załogi"),

    ("brakuje grupy, o którą poprosi TestSpawner",
     lambda d: podmien(os.path.join(d, "SpawnGroups.sbc"),
                       "<SubtypeId>ZF_Raid_WGR</SubtypeId>",
                       "<SubtypeId>ZF_Rajd_WGR</SubtypeId>"),
     "GroupForKind może zażądać grupy ZF_Raid_WGR"),

    ("kod spawnuje nieistniejący prefab",
     lambda d: podmien(os.path.join(d, "Scripts", "ZyweFrakcje", "RansomManager.cs"),
                       'CratePrefab = "ZF_DropCrate"', 'CratePrefab = "ZF_DropKrate"'),
     "ani w tabeli ZNANE_PREFABY"),

    ("Stations.cs: tablice Tags i Prefabs rozjechane",
     lambda d: podmien(os.path.join(d, "Scripts", "ZyweFrakcje", "Stations.cs"),
                       '"RE05_StagingStation",  // WGR', "// WGR bez prefabu"),
     "indeksowane wspólnie"),

    ("brak frakcji w Factions.sbc",
     lambda d: podmien(os.path.join(d, "Factions.sbc"), 'Tag="WGR"', 'Tag="GOR"'),
     "brak frakcji o tagu WGR"),

    ("uszkodzony XML",
     lambda d: podmien(os.path.join(d, "ZF_Manipulations.sbc"),
                       "</EntityComponents>", "</EntityComponent>"),
     "nie parsuje się jako XML"),
]


def main():
    zrodlo = os.path.join(KORZEN, "mod", "Data")

    kod, wyjscie = uruchom(zrodlo)
    if kod != 0:
        print("Walidator zgłasza błędy na CZYSTYCH danych — najpierw napraw mod:\n" + wyjscie)
        return 1
    print("OK   dane w repo przechodzą walidację")

    bledy = 0
    for opis, psuj, oczekiwane in USTERKI:
        with tempfile.TemporaryDirectory() as tmp:
            kopia = os.path.join(tmp, "Data")
            shutil.copytree(zrodlo, kopia)
            psuj(kopia)
            kod, wyjscie = uruchom(kopia)
            if kod == 0:
                print("FAIL {} — walidator NIE zauważył usterki".format(opis))
                bledy += 1
            elif oczekiwane not in wyjscie:
                print("FAIL {} — zgłoszony błąd nie pasuje (brak \"{}\"):\n{}"
                      .format(opis, oczekiwane, wyjscie))
                bledy += 1
            else:
                print("OK   {}".format(opis))

    print("")
    print("Kontrtest: {} z {} usterek wykrytych.".format(len(USTERKI) - bledy, len(USTERKI)))
    return 1 if bledy else 0


if __name__ == "__main__":
    sys.exit(main())
