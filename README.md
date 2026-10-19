# GPhotos Sync (Windows) – Dokumentace a Architektura

Automatická synchronizace fotek a videí z **Google Photos** do složky ve Windows s podporou **obousměrného mazání** (přesunutí/smazání fotky na PC ji přesune do koše i v Google Photos).

## Hlavní funkce
1. **Automatický běh na pozadí (System Tray):** Spouští se po startu Windows a tiše kontroluje nové fotky a videa v Google Photos.
2. **Originální kvalita a zachování EXIF metadat:** Stahuje původní soubory přes nativní jádro Microsoft Edge WebView2 a nastavuje souborům ve Windows reálné datum vyfocení podle EXIF.
3. **Jedna přehledná složka (jako OneDrive):** Všechny nové fotky a videa padají do jedné zvolené složky.
4. **Obousměrná synchronizace (Mazání / Přesunutí):**
   - **Zkopírování fotky (`Ctrl+C` -> `Ctrl+V`):** Fotka zůstává ve složce i v Google Photos.
   - **Přesunutí (`Ctrl+X` / přetažení myší jinam) nebo Smazání (`Delete`):** Fotka ze sledované složky zmizí -> aplikace ji automaticky přesune do **Koše v Google Photos** (kde zůstává ještě 60 dní pro případ omylu).
   - **Přejmenování souboru ve složce:** Aplikace rozpozná přejmenování a fotku v Google Photos nesmaže.
5. **Volba počáteční synchronizace při instalaci / prvním spuštění:**
   - Pouze nové fotky od této chvíle
   - Fotky za posledních X dní
   - Fotky od konkrétního data
   - Stáhnout celou historii
