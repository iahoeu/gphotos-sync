# GPhotos Sync (Windows) – Dokumentace a Architektura

Automatická synchronizace fotek a videí z **Google Photos** do složky ve Windows s podporou **obousměrného mazání** (přesunutí/smazání fotky na PC ji přesune do koše i v Google Photos) a automatickými aktualizacemi z Gitea serveru (`https://git.iaho.eu/iaho/gphotos-sync`).

## Hlavní funkce
1. **Automatický běh na pozadí (System Tray):** Spouští se po startu Windows a tiše kontroluje nové fotky a videa v Google Photos.
2. **Originální kvalita a zachování EXIF metadat:** Stahuje původní soubory přes nativní jádro Microsoft Edge WebView2 a nastavuje souborům ve Windows reálné datum vyfocení podle EXIF.
3. **Jedna přehledná složka (jako OneDrive):** Všechny nové fotky a videa padají do jedné zvolené složky.
4. **Obousměrná synchronizace (Mazání / Přesunutí / Podsložky):**
   - **Zkopírování fotky (`Ctrl+C` -> `Ctrl+V`):** Fotka zůstává ve složce i v Google Photos.
   - **Přesunutí do podsložky nebo přejmenování uvnitř sledované složky:** Aplikace sleduje celou složku včetně všech jejích podsložek (podle SHA-256 otisku celého souboru a velikosti). Přesunutí do podsložky nebo přejmenování souboru uvnitř sledované složky pouze aktualizuje umístění v evidenci a fotku v Google Photos **nesmaže**.
   - **Přesunutí mimo sledovanou složku (`Ctrl+X` / přetažení na jiné místo mimo strom sledované složky) nebo Smazání (`Delete`):** Fotka ze sledované složky i jejích podsložek zmizí -> aplikace ji automaticky přesune do **Koše v Google Photos** (kde zůstává ještě 60 dní pro případ omylu). Při větším počtu odebraných fotek najednou zasáhne bezpečnostní pojistka hromadného mazání a vyžádá si potvrzení u ikony v liště.
5. **Volba počáteční synchronizace při instalaci / prvním spuštění:**
   - Pouze nové fotky od této chvíle
   - Fotky za posledních X dní
   - Fotky od konkrétního data
   - Stáhnout celou historii
6. **Automatická kontrola a tichá instalace aktualizací z Gitea (`git.iaho.eu`):**
   - Aplikace má ve výchozím stavu přednastavený server `https://git.iaho.eu` (repozitář `iaho/gphotos-sync`).
   - Kontroluje nejnovější vydání přes REST API (`/api/v1/repos/iaho/gphotos-sync/releases/latest`) a umí instalátor sama stáhnout a provést tichou aktualizaci (`--silent-update`).

## Napojení na Gitea a vydávání nových verzí
- **Web / API repozitáře:** `https://git.iaho.eu/iaho/gphotos-sync`
- **SSH Git remote:** `git@iaho.eu:iaho/gphotos-sync.git`
- **Nejnovější instalátor ke stažení:** `https://git.iaho.eu/iaho/gphotos-sync/releases/latest`

### Vydání nové verze jedním příkazem
Ve složce `C:\Antigravity\photos` spusťte:
```powershell
.\publish_to_gitea.ps1 -Version "1.0.1" -ReleaseNotes "Popis změn v nové verzi"
```
Skript automaticky:
1. Aktualizuje verzi `<Version>` v projektech.
2. Zkompiluje aplikaci a zabalí ji do `GPhotosSync_Setup.exe`.
3. Provede `git commit`, vytvoří tag `v1.0.1` a odešle vše přes SSH do Gitea.
4. Vytvoří Release na `https://git.iaho.eu/iaho/gphotos-sync/releases` a nahraje k němu `GPhotosSync_Setup.exe`.
