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
    proces = subprocess.run(
        [sys.executable, WALIDATOR, "--korzen", korzen_danych],
        capture_output=True, text=True)
    return proces.returncode, proces.stdout + proces.stderr


def podmien(sciezka, szukaj, zamien, ile=1):
    with open(sciezka, encoding="utf-8") as f:
        tresc = f.read()
    if szukaj not in tresc:
        raise AssertionError("nie znalazłem w {}: {}".format(sciezka, szukaj))
    with open(sciezka, "w", encoding="utf-8") as f:
        f.write(tresc.replace(szukaj, zamien, ile))


# (opis, funkcja psująca dane, fragment oczekiwanego komunikatu)
USTERKI = [
    ("zachowanie wskazuje na nieistniejący profil",
     lambda d: podmien(os.path.join(d, "SpawnGroups.sbc"),
                       "<Behaviour>ZF_Fighter_KRW</Behaviour>",
                       "<Behaviour>ZF_Fighter_Krw</Behaviour>"),
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
     lambda d: podmien(os.path.join(d, "SpawnGroups.sbc"),
                       "[ManipulationGroups:ZF_ManipulacjaGrupa_Pilot]",
                       "[ManipulationGroups:ZF_ManipulacjaGrupa_Pilotow]"),
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
     lambda d: podmien(os.path.join(d, "SpawnGroups.sbc"),
                       'SubtypeId="C33_Military_Enforcer"',
                       'SubtypeId="DS_Pirate_ShakedownDrone"'),
     "wstawia blok dla siatki Large"),

    ("duży kadłub bez pilota i bez manipulacji (konwój dryfuje)",
     lambda d: podmien(os.path.join(d, "SpawnGroups.sbc"),
                       "[ManipulationGroups:ZF_ManipulacjaGrupa_Pilot]", "", ile=99),
     "będzie dryfował"),

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
