# Hollylark

[English](README.md) · [Historia zmian — po angielsku](CHANGELOG.md) · [Współpraca — po angielsku](CONTRIBUTING.md)

Nieoficjalna aplikacja Windows do sterowania odbiornikiem **Hollyland LARK A1 USB-C**. Hollylark zapewnia natywne kontrolki WPF, obsługę klawiatury i dostępne etykiety dla NVDA. Jest projektem niezależnym od Hollyland i nie ma poparcia producenta.

**Wersja źródeł 0.10.1** · Windows x64 · .NET Desktop Runtime 10 x64

## Pobieranie i uruchamianie

Najnowsza opublikowana paczka portable to **v0.9.1**. Ten przewodnik opisuje **źródła 0.10.1**; aby korzystać ze skrótów globalnych, zachowywania niestałych ustawień i bezpośrednich ogłoszeń Prism przed publikacją odpowiedniej paczki, [zbuduj program ze źródeł](#kompilacja).

1. Pobierz wersję portable w ZIP z [najnowszego wydania na GitHubie](https://github.com/Pates2004/Hollylark/releases/latest).
2. Osobno zainstaluj **Microsoft .NET Desktop Runtime 10 dla Windows x64**. Hollylark jest aplikacją framework-dependent: nie zawiera środowiska .NET. Natywny host GUI Windows wyświetla standardową propozycję pobrania .NET, gdy brakuje wymaganego środowiska.
3. Rozpakuj cały ZIP do zapisywalnego folderu. Zachowaj poniższe pliki razem; nie uruchamiaj EXE wewnątrz archiwum:

   ```text
   Hollylark.exe
   prism.dll
   langs/
     pl.lng
     en.lng
   ```

4. Uruchom `Hollylark.exe`, wybierz opcje pierwszego uruchomienia i naciśnij **Kontynuuj**. **Anuluj** kończy program bez zapisywania i otwierania głównego okna.

Plik `prism.dll` jest dołączany do buildów 0.10.1; starsze opublikowane paczki go nie zawierają.

Domyślnym językiem jest polski. Angielski można wybrać już w kreatorze pierwszego uruchomienia. Później wybierz **Program → Ustawienia → Wygląd → Język**, zapisz przez **OK**, całkowicie zakończ program i uruchom go ponownie. Wymagane są oba pliki językowe, niezależnie od wybranego języka.

Hollylark pozwala na jedną działającą instancję dla użytkownika/sesji Windows. Ponowne uruchomienie przywraca istniejące główne okno, kreator pierwszego uruchomienia albo otwarte okno pomocnicze, również po zminimalizowaniu lub ukryciu programu w zasobniku. Nie jest to restart aplikacji i nie wprowadza zapisanej zmiany języka.

## Funkcje

- Automatyczne wykrywanie odbiorników USB, odczyty stanu i wersji firmware oraz poziomów baterii połączonych nadajników.
- Edytowalne globalne skróty z przechwytywaniem, włączaniem/wyłączaniem i przywracaniem domyślnych.
- Opcjonalne powiadomienia czytnika ekranu w tle o potwierdzonych zmianach zewnętrznych.
- Domyślnie włączone zachowywanie niestałych wspólnych parametrów po zaobserwowanym powrocie mikrofonów.
- Sześć poziomów wzmocnienia, redukcja szumu, EQ, pogłos, mono/stereo, diody LED i automatyczne wyłączenie po 15 minutach bez sparowania.
- Wyciszanie/włączanie dźwięku pojedynczych mikrofonów według ich indeksów oraz cel **Wszystkie mikrofony** do działań grupowych.
- Domyślne ręczne **Zastosuj**; opcjonalne automatyczne stosowanie nowych wyborów i lokalne profile dźwięku.
- Opcjonalne uruchamianie z Windows i obsługa zasobnika z jawnymi poleceniami odczytu, wzmocnienia i wyciszania.
- Jasny/ciemny wygląd, regulowana wielkość tekstu i prezentacja odczytów oraz opcjonalne podpowiedzi wizualne i dla czytnika ekranu, zapamiętywane między uruchomieniami.
- Opcjonalne mierniki wejść Windows: RMS, szczyty próbek i zapamiętywane wskazania przesterowania. Samodzielnie wybierz wejście i rozpocznij pomiar; Hollylark nie rozpoczyna automatycznie przechwytywania, nie zapisuje nagrań ani nie wysyła dźwięku przez sieć.
- Lokalna diagnostyka i odczyt numerów seryjnych/MAC na żądanie.

## Odbiorniki i sterowanie mikrofonami

Podłącz **odbiornik USB-C** przewodem lub adapterem obsługującym transmisję danych i włącz nadajniki. Etui ładujące nie jest odbiornikiem. Jedyny wykryty odbiornik jest wybierany i odczytywany automatycznie; jeśli jest ich kilka, wybierz jeden strzałkami na liście odbiorników. Wykrywanie przy starcie i pierwszy odczyt zastający już aktywne mikrofony nie stosują profili ani nie zapisują ustawień. Włączone zachowywanie niestałych ustawień może przywracać parametry po zaobserwowanym powrocie mikrofonów, zgodnie z opisem poniżej.

Na liście mikrofonów wybierz pojedynczy mikrofon albo **Wszystkie mikrofony**. Interfejs obsługuje indeksy 1–4, gdy odbiornik zgłasza odpowiadający im układ; nie zmienia to firmware dla dwóch nadajników w firmware dla czterech.

| Aktywne mikrofony | Cel dla parametrów wspólnych | Wyciszanie/włączanie dźwięku |
| --- | --- | --- |
| Dokładnie jeden | Wybierz ten pojedynczy mikrofon | Ten mikrofon |
| Co najmniej dwa | Wybierz **Wszystkie mikrofony** | Pojedynczy mikrofon albo grupa |

W znanym protokole wzmocnienie, redukcja szumu, EQ, pogłos i inne parametry urządzenia są **wspólnymi ustawieniami odbiornika**. Wybranie pojedynczego mikrofonu nie zapewnia niezależnego wzmocnienia ani EQ. Gdy aktywny jest tylko jeden mikrofon, edycja parametrów pod jego pozycją nadal zmienia ustawienia całego odbiornika; wpływ na nadajniki dołączające później nie został ustalony. Profile podlegają tym samym ograniczeniom wyboru celu.

Domyślnie wybór wartości tworzy szkic; dopiero **Zastosuj** wysyła zmianę. Automatyczne stosowanie jest opcjonalnym ustawieniem, domyślnie wyłączonym. Zmiany wymagają potwierdzenia polecenia i zgodnego odczytu z urządzenia. Zmiana odbiornika, wybranego mikrofonu/grupy lub składu aktywnych mikrofonów odrzuca niewysłane szkice i oczekujące zmiany automatyczne. **F5** również odrzuca szkice przed ponownym odczytem.

Grupowe wyciszanie/włączanie dźwięku wysyła polecenia kolejno do indeksów aktywnych mikrofonów, sprawdza potwierdzenia i wymaga końcowego odczytu potwierdzającego stan całej grupy. Grupa mieszana zostaje wyciszona; w grupie całkowicie wyciszonej dźwięk zostaje włączony. Operacja nie jest atomowa: błąd, rozłączenie lub anulowanie mogą pozostawić część mikrofonów ze zmienionym stanem. Ostrzeżenie o częściowym wykonaniu oznacza konieczność ponownego odczytu stanu; nie oznacza cofnięcia wcześniejszych zapisów.

## Globalne skróty i automatyczne przywracanie

Otwórz **Ustawienia → Skróty klawiszowe**, wybierz akcję strzałkami, a następnie Tabem przejdź do edytowalnego pola skrótu i przycisku **Przechwyć skrót**. Przechwytywanie rozpoczyna się wyłącznie po naciśnięciu tego przycisku; Escape je anuluje bez zamykania Ustawień. Wyczyść pole, aby usunąć przypisanie. Nieprawidłowe, powtórzone i zajęte kombinacje są zgłaszane przed zapisem.

Globalne skróty są **domyślnie wyłączone**. Ich wyłączanie nie usuwa przypisań. Przywrócenie domyślnych tej kategorii odtwarza kombinacje i ponownie je wyłącza. Domyślne kombinacje używają Ctrl+Alt+Shift: F1 pokazuje Hollylark, F5 odczytuje stan, F2 przełącza wyciszenie wybranego mikrofonu/grupy, F3 — wszystkich mikrofonów, strzałki góra/dół zmieniają wzmocnienie, F6 redukcję szumu, F7 korektor EQ, F8 pogłos, F9 diody LED, a F10 kanały mono/stereo. Automatyczne wyłączenie i rozpoznawanie mikrofonów początkowo nie mają przypisań. Domyślne nie używają liter, aby nie zabierać znaków wpisywanych przez AltGr. Polecenia sprzętowe używają świeżego odczytu i ograniczeń mikrofon/grupa, bez przenoszenia fokusu. Globalny odczyt odmawia przy niezastosowanych wyborach; zwykłe F5 nadal jawnie je odrzuca. Skróty są wstrzymane w Ustawieniach i zwalniane przy zakończeniu aplikacji. Program musi pozostać uruchomiony.

Istniejąca opcja **Ogłaszaj zmiany stanu urządzenia za pomocą czytnika ekranu** przekazuje potwierdzone zmiany z telefonu i przycisków mikrofonu bezpośrednio uruchomionemu czytnikowi przez **Prism**, za pomocą bindingu .NET **Prismatoid**. Nie wymaga aktywnego okna Hollylarka: działa również po zminimalizowaniu lub ukryciu w zasobniku. Plik `prism.dll` musi pozostać obok EXE. Jeśli działa kilka obsługiwanych czytników, pierwszeństwo ma NVDA; osobne głosy SAPI/OneCore nie są wybierane. Bezpośrednie komunikaty nie żądają przerwania bieżącej mowy czytnika, nie przenoszą fokusu, nie powtarzają niezmienionych wartości ani wahań baterii. Udane bezpośrednie przekazanie wyklucza dodatkowe wysłanie tego samego powiadomienia UI Automation. Jeśli czytnik jest niedostępny lub przekazanie zawiedzie, pozostaje UI Automation; wtedy odczyt zależy od polityki czytnika. Czytnik musi działać w tej samej sesji Windows i na tym samym pulpicie. Testy automatyczne nie potwierdzają mowy rzeczywiście usłyszanej przez człowieka.

**Zachowuj niestałe ustawienia mikrofonów** jest domyślnie włączone. Potwierdzone wzmocnienie, kanały, redukcja szumu, EQ, pogłos, diody, automatyczne wyłączenie i obsługiwane rozpoznawanie są pamiętane osobno dla ścieżki USB odbiornika w `data/retained-settings.json` obok EXE. Zmiany zewnętrzne podczas ciągłego połączenia stają się nową zapamiętaną konfiguracją. Po wykrytym rozłączeniu i powrocie program przywraca różniące się wspólne parametry ze świeżą kontrolą grupy, konfliktów i odczytu. Dotyczy to całej aktywnej grupy, niezależnie od aktualnie pokazanego pojedynczego mikrofonu. Wyciszenie nie jest przywracane i program nigdy sam nie odcisza mikrofonów. Jeśli pierwszy odczyt sprzętu zastanie aktywne mikrofony, przyjmuje ich rzeczywisty stan bez wysyłania starej konfiguracji; jeśli najpierw zastanie je rozłączone, zapamiętane ustawienia mogą wrócić po ich połączeniu. Inna ścieżka USB ma własną konfigurację. Cyklu całkowicie pomiędzy odczytami lub przy zamkniętym Hollylarku nie da się wiarygodnie rozpoznać; protokół nie podaje przyczyny zmiany wartości. Błąd lub zmiana podczas przywracania kończy próbę bez pętli. Program nie odcisza mikrofonów ani nie omija wymaganej liczby TX, aby wymusić niedostępną zmianę redukcji szumu lub rozpoznawania. Niezastosowane wybory albo otwarte Ustawienia blokują rozpoczęcie przywracania. F5 i odczyt identyfikatorów pozostają tylko odczytem; odroczone przywrócenie może nastąpić przy późniejszym odczycie w tle. Wyłącz zachowywanie, aby zapobiec takim automatycznym zapisom.

## Klawiatura i NVDA

| Klawisz | Działanie |
| --- | --- |
| Tab / Shift+Tab | Przechodzenie między kontrolkami |
| Strzałki | Nawigacja po natywnych listach, w tym wybór odbiornika i celu sterowania mikrofonami |
| Ctrl+Tab / Ctrl+Shift+Tab | Zmiana zakładki |
| Enter / Spacja | Uruchomienie przycisku |
| Alt | Otwarcie menu |
| F5 | Odczyt lub ponowienie odczytu odbiornika; odrzucenie oczekujących wyborów urządzenia |
| Alt+F4 | Zamknięcie albo ukrycie w zasobniku zgodnie z ustawieniami |
| Win+B | Przejście do obszaru powiadomień Windows przy włączonej obsłudze zasobnika |

Escape zamyka otwarte menu/listy rozwijane, pozostawia główne okno i kreator otwarte oraz zamyka zwykłe okna pomocnicze. Opuszczenie Ustawień z niezapisanymi zmianami wywołuje pytanie o zapis: **Tak** zapisuje, **Nie** odrzuca. Błędy zapisu pozostawiają okno ustawień otwarte.

Odczyty mają dostępne nazwy i prezentację umożliwiającą ustawienie fokusu. Opcjonalne podpowiedzi, również dla czytnika ekranu, można wyłączyć i ponownie włączyć w Ustawieniach bez usuwania niezbędnych nazw, błędów ani ostrzeżeń bezpieczeństwa. Automatyczne kontrole UI Automation nie potwierdzają rzeczywistej jakości mowy NVDA; próby z czytnikiem, klawiaturą, zasobnikiem, wysokim kontrastem i różnymi DPI pozostają częścią ręcznej weryfikacji.

## Dane lokalne i aktualizacje

Preferencje i profile dźwięku są zapisywane w `data/settings.json`, a domyślne raporty diagnostyczne w `reports`, zawsze obok **rzeczywistego EXE**, niezależnie od katalogu roboczego terminala. Hollylark nie przechodzi po cichu do AppData. Używaj zapisywalnego folderu portable. Nieprawidłowe lub nieobsługiwane ustawienia powodują błąd zamiast cichego zastąpienia. Program nie wysyła telemetrii.

Przed aktualizacją całkowicie zakończ Hollylark. Zachowaj folder `data` i potrzebne raporty, a pliki programu oraz oba katalogi językowe zastąp razem zawartością nowego ZIP. Jeśli uruchamianie z Windows jest włączone, wyłącz je przed przeniesieniem folderu portable i włącz ponownie z nowego miejsca.

Diagnostyka może zawierać ścieżki urządzeń USB, wersje firmware, numery seryjne, adresy MAC i surowe raporty. Przejrzyj ją przed publicznym udostępnieniem.

## Znane ograniczenia

- Zgodność z najnowszą globalną wersją HollyAudio z Google Play **nie jest potwierdzona**.
- Instalacja firmware, sprawdzanie dostępności aktualizacji, parowanie nadajników oraz bateria/stan etui ładującego nie są zaimplementowane. Strona firmware zawiera informacje o wersjach i wskazówki, nie aktualizator.
- Nie ma niezależnego wzmocnienia/EQ dla mikrofonów, obsługi Bluetooth ani odbiornika Lightning, ani zwykłego ustawienia przełączającego firmware dla dwóch i czterech nadajników.
- Wyciszanie wymaga połączonego nadajnika i potwierdzonego firmware odbiornika co najmniej 1.0.1.3. Pozostałe kontrolki zależą od zgłaszanego stanu i możliwości urządzenia.
- Mierniki dźwięku Windows mierzą jawnie wybrane wejście Windows, nie poziomy poszczególnych nadajników przez protokół sterowania USB ani skalibrowaną głośność akustyczną.
- Zgodny odczyt nie potwierdza efektu dźwiękowego, fizycznego świecenia diod, wyłączenia po upływie czasu ani pełnej zgodności sprzętowej. Udana kompilacja nie jest certyfikacją dostępności ani sprzętu.

## Kompilacja

Użyj Windows x64, **.NET 10 SDK** i PowerShell. W katalogu głównym źródeł wykonaj:

```powershell
.\build.ps1
```

Skrypt publikuje bieżące wydanie x64 framework-dependent w lokalnym `installfiles`, zachowuje `Hollylark.exe` razem z folderem `langs` i istniejącymi `data` oraz `reports`. Pozostawia natywny host GUI; nie używa publikacji self-contained ani Native AOT.

Artefakty kompilacji trafiają do `D:\work\Workspace\<nazwa-katalogu-źródeł>`. Zastąpione buildy/wydania są przenoszone bez usuwania do `D:\work\Trash\<nazwa-katalogu-źródeł>`; poprzedni build pozostaje w `Snapshots` tylko przy udokumentowanej bieżącej potrzebie diagnostyki lub odtwarzania danych. Odpowiednik w `C:\work` jest używany wyłącznie wtedy, gdy dysk D: nie istnieje. Brak uprawnień lub miejsca na D: nie powoduje przełączenia na C:.

## Walidacja

Wersja 0.10.1 przeszła 36 374 kontroli pełnej regresji na osobnym pulpicie Windows. Osobny przebieg 347 kontroli Prism obejmuje symulowane błędy i czas życia zasobów natywnych, pojedynczą ścieżkę ogłaszania oraz potwierdzone komunikaty PL/EN przy aktywnym, nieaktywnym, zminimalizowanym i ukrytym oknie bez przenoszenia fokusu. Osiem prób natywnych załadowało ustaloną bibliotekę x64 i zainicjalizowało działający backend NVDA bez wysyłania mowy ani anulowania; przeszło również 12 kontroli przywracania okien WPF i dziewięć prób natywnego zasobnika Explorer. Zakresy częściowo się pokrywają i nie potwierdzają mowy usłyszanej przez człowieka, fizycznego wywoływania skrótów z innej aplikacji ani skutków ustawień na mikrofonach — te próby wymagają użytkownika/sprzętu.

Kontrole konsolowe uruchomisz poleceniami:

```powershell
dotnet build .\LarkControl.slnx -c Release
dotnet run --project .\tests\Hollylark.Validation\Hollylark.Validation.csproj -c Release --no-build -- --core-only
dotnet run --project .\tests\Hollylark.Validation\Hollylark.Validation.csproj -c Release --no-build -- --single-instance-only
```

Walidator jest programem konsolowym, nie zestawem uruchamianym przez `dotnet test`. Kontrole rdzenia używają symulowanych danych urządzenia; kontrole jednej instancji używają odizolowanych nazw i własnych procesów potomnych bez GUI ani sprzętu. Nie sprawdzają przywracania rzeczywistych okien ani mowy NVDA.

Opcjonalną walidację WPF/UI Automation na osobnym pulpicie uruchom po zbudowaniu rozwiązania:

```powershell
$validationLog = 'Workspace\Validation\public-' + (Get-Date -Format 'yyyyMMdd-HHmmss-fff') + '.log'
.\tools\RunValidationDesktop.ps1 -Arguments @('--without-native-shell') -LogPath $validationLog
```

Względna ścieżka logu wskazuje centralny Workspace danego katalogu źródeł. Ten tryb pomija próby natywnych menu/zasobnika oraz przywracania okien na pulpicie użytkownika i używa symulowanego USB/audio. Uruchamiaj go z odpowiadających mu źródeł; część kontroli odczytuje pliki kodu. Tryby sprzętowe nie są częścią tych poleceń.

Kontrole przywracania okien uruchom na zwykłym, interaktywnym pulpicie:

```powershell
dotnet run --project .\tests\Hollylark.Validation\Hollylark.Validation.csproj -c Release --no-build -- --single-instance-ui-only
```

Te kontrole korzystają z okien WPF, symulowanego odbiornika i zasobnika w pamięci. Rzeczywista mowa NVDA, dźwięk ze sprzętu i obsługa natywnego zasobnika wymagają osobnych prób.

## Informacje o zależnościach

Hollylark używa HidSharp, NAudio i Prism przez Prismatoid. Zachowaj informacje o autorach i teksty ich licencji; zobacz [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt) oraz licencje bibliotek dostępne w oknie Pomocy aplikacji.

## Licencja

Hollylark jest udostępniany na licencji **GNU General Public License w wersji 3.0 (GPL-3.0-only)**. Pełny tekst znajduje się w [LICENSE](LICENSE). Zależności zachowują własne licencje. Źródła odpowiadające każdemu wydaniu portable są dostępne pod jego tagiem Git.
