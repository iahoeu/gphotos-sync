# GPhotos Sync (Windows) – Dokumentace a Architektura

Automatická synchronizace fotek a videí z **Google Photos** do složky ve Windows s podporou **obousměrného mazání** (přesunutí/smazání fotky na PC ji přesune do koše i v Google Photos) a automatickými aktualizacemi z GitHub Releases (<https://github.com/iahoeu/gphotos-sync>).

## Instalace
Stáhněte nejnovější instalátor `GPhotosSync_Setup.exe` z <https://github.com/iahoeu/gphotos-sync/releases/latest> a spusťte ho.

## Hlavní funkce
1. **Automatický běh na pozadí (System Tray):** Spouští se po startu Windows a tiše kontroluje nové fotky a videa v Google Photos.
   - **Barva ikony u hodin:** zelená = vše v pořádku, modrá = aplikace čeká na vaše rozhodnutí (hromadné mazání, potvrzení účtu, přihlášení), červená = chyba (podrobnosti v menu ikony a v protokolu).
2. **Originální kvalita a zachování EXIF metadat:** Stahuje původní soubory přes nativní jádro Microsoft Edge WebView2 a nastavuje souborům ve Windows reálné datum vyfocení podle EXIF.
3. **Jedna přehledná složka (jako OneDrive):** Všechny nové fotky a videa padají do jedné zvolené složky.
4. **Obousměrná synchronizace (Mazání / Přesunutí / Podsložky):**
   - **Zkopírování fotky (`Ctrl+C` -> `Ctrl+V`):** Fotka zůstává ve složce i v Google Photos.
   - **Přesunutí do podsložky nebo přejmenování uvnitř sledované složky:** Aplikace sleduje celou složku včetně všech jejích podsložek (podle SHA-256 otisku celého souboru a velikosti). Přesunutí do podsložky nebo přejmenování souboru uvnitř sledované složky pouze aktualizuje umístění v evidenci a fotku v Google Photos **nesmaže**.
   - **Přesunutí mimo sledovanou složku (`Ctrl+X` / přetažení na jiné místo mimo strom sledované složky) nebo Smazání (`Delete`):** Fotka ze sledované složky i jejích podsložek zmizí -> aplikace ji automaticky přesune do **Koše v Google Photos** (kde zůstává ještě 60 dní pro případ omylu). Při větším počtu odebraných fotek najednou zasáhne bezpečnostní pojistka hromadného mazání a vyžádá si potvrzení u ikony v liště. Do rozhodnutí čekají i ostatní mazání. Volba **Zrušit** fotky v Google Photos ponechá a stáhne je zpět do složky.
   - **Přejmenování a zároveň úprava fotky (např. uložení v editoru pod jiným názvem):** Fotka se pozná jen podle otisku obsahu, upravený soubor ho má jiný. Pokud původní soubor zmizí, aplikace ho bere jako smazaný a fotku přesune do koše v Google Photos. Upravená kopie na disku zůstane.
   - **Nejistota = nemazat:** Když nejde ověřit, že soubor opravdu zmizel (soubor bez otisku ze starší verze, zamčený soubor, chyba prohledání složky, neověřený účet Google), aplikace v Google Photos nic nesmaže a zapíše upozornění do protokolu.
   - **Obnovení z koše v Google Photos:** Fotku, kterou aplikace přesunula do koše, můžete na webu obnovit. Aplikace ji pak sama znovu stáhne do složky, ale **nejdříve 5 minut po jejím smazání** (pojistka proti tomu, aby se hned po smazání znovu stáhla fotka, kterou stránka ještě chvíli ukazuje). Synchronizace spuštěná dřív obnovenou fotku přeskočí, stáhne ji až první synchronizace po uplynutí 5 minut.
   - **Neověřené smazání se neopakuje:** Pokud aplikace na koš klikne, ale výsledek nedokáže ověřit, mazání už znovu nezkouší (mohlo by to smazat fotku, kterou jste mezitím obnovili). Zobrazí upozornění a do protokolu zapíše diagnostiku – v takovém případě zkontrolujte koš v Google Photos ručně.
   - **Složka fotek** nesmí ležet v datové složce aplikace (`%LocalAppData%\GPhotosSync`) ani ji obsahovat.
5. **Volba počáteční synchronizace při instalaci / prvním spuštění:**
   - Pouze nové fotky od této chvíle
   - Fotky za posledních X dní
   - Fotky od konkrétního data
   - Stáhnout celou historii
   - Výchozí stav režimu „jen nové“ se zaznamenává postupně přes více cyklů; fotky ze dne instalace a novější se mezitím stahují.
   - **Omezení hloubky:** časová osa se prochází posouváním webu od začátku. U velmi velké knihovny nemusí kontrola dosáhnout nejstarší historie (výchozí stav, „celá historie“, týdenní úplná kontrola). Aplikace to pak hlásí u ikony v liště a v protokolu jako „Neúplná kontrola knihovny“.
6. **Automatická kontrola a tichá instalace aktualizací:**
   - Ve výchozím stavu aplikace kontroluje nejnovější vydání na GitHubu přes `https://api.github.com/repos/iahoeu/gphotos-sync/releases/latest` a umí instalátor sama stáhnout a provést tichou aktualizaci (`--silent-update`).
   - V Nastavení lze aktualizace přesměrovat na vlastní server Gitea/Forgejo (Server URL + vlastník/repozitář). Aplikace pak používá `/api/v1/repos/{vlastník}/{repozitář}/releases/latest` na zadaném serveru.

## Licence
Projekt je šířen pod licencí MIT, viz [LICENSE](LICENSE).
