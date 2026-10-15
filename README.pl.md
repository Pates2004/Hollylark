# Hollylark

[English](README.md) · [Historia zmian — po angielsku](CHANGELOG.md) · [Współpraca — po angielsku](CONTRIBUTING.md)

Nieoficjalna aplikacja Windows do sterowania odbiornikiem **Hollyland LARK A1 USB-C**. Hollylark zapewnia natywne kontrolki WPF, obsługę klawiatury i dostępne etykiety dla NVDA. Jest projektem niezależnym od Hollyland i nie ma poparcia producenta.

**Wersja 0.9.1** · Windows x64 · .NET Desktop Runtime 10 x64

## Pobieranie i uruchamianie

1. Pobierz wersję portable w ZIP z [najnowszego wydania na GitHubie](https://github.com/Pates2004/Hollylark/releases/latest). Pierwsze publiczne wydanie to **v0.9.1**.
2. Osobno zainstaluj **Microsoft .NET Desktop Runtime 10 dla Windows x64**. Hollylark jest aplikacją framework-dependent: nie zawiera środowiska .NET. Natywny host GUI Windows wyświetla standardową propozycję pobrania .NET, gdy brakuje wymaganego środowiska.
3. Rozpakuj cały ZIP do zapisywalnego folderu. Zachowaj poniższe pliki razem; nie uruchamiaj EXE wewnątrz archiwum:

   ```text
   Hollylark.exe
   langs/
     pl.lng
     en.lng
   ```

4. Uruchom `Hollylark.exe`, wybierz opcje pierwszego uruchomienia i naciśnij **Kontynuuj**. **Anuluj** kończy program bez zapisywania i otwierania głównego okna.

Domyślnym językiem jest polski. Angielski można wybrać już w kreatorze pierwszego uruchomienia. Później wybierz **Program → Ustawienia → Wygląd → Język**, zapisz przez **OK**, całkowicie zakończ program i uruchom go ponownie. Wymagane są oba pliki językowe, niezależnie od wybranego języka.

Hollylark pozwala na jedną działającą instancję dla użytkownika/sesji Windows. Ponowne uruchomienie przywraca istniejące główne okno, kreator pierwszego uruchomienia albo otwarte okno pomocnicze, również po zminimalizowaniu lub ukryciu programu w zasobniku. Nie jest to restart aplikacji i nie wprowadza zapisanej zmiany języka.

## Funkcje

- Automatyczne wykrywanie odbiorników USB, odczyty stanu i wersji firmware oraz poziomów baterii połączonych nadajników.
- Sześć poziomów wzmocnienia, redukcja szumu, EQ, pogłos, mono/stereo, diody LED i automatyczne wyłączenie po 15 minutach bez sparowania.
- Wyciszanie/włączanie dźwięku pojedynczych mikrofonów według ich indeksów oraz cel **Wszystkie mikrofony** do działań grupowych.
- Domyślne ręczne **Zastosuj**; opcjonalne automatyczne stosowanie nowych wyborów i lokalne profile dźwięku.
- Opcjonalne uruchamianie z Windows i obsługa zasobnika z jawnymi poleceniami odczytu, wzmocnienia i wyciszania.
- Jasny/ciemny wygląd, regulowana wielkość tekstu i prezentacja odczytów oraz opcjonalne podpowiedzi wizualne i dla czytnika ekranu, zapamiętywane między uruchomieniami.
- Opcjonalne mierniki wejść Windows: RMS, szczyty próbek i zapamiętywane wskazania przesterowania. Samodzielnie wybierz wejście i rozpocznij pomiar; Hollylark nie rozpoczyna automatycznie przechwytywania, nie zapisuje nagrań ani nie wysyła dźwięku przez sieć.
- Lokalna diagnostyka i odczyt numerów seryjnych/MAC na żądanie.

## Odbiorniki i sterowanie mikrofonami

Podłącz **odbiornik USB-C** przewodem lub adapterem obsługującym transmisję danych i włącz nadajniki. Etui ładujące nie jest odbiornikiem. Jedyny wykryty odbiornik jest wybierany i odczytywany automatycznie; jeśli jest ich kilka, wybierz jeden strzałkami na liście odbiorników. Wykrywanie i automatyczne odczyty stanu nie stosują profili ani nie zapisują ustawień dźwięku.

Na liście mikrofonów wybierz pojedynczy mikrofon albo **Wszystkie mikrofony**. Interfejs obsługuje indeksy 1–4, gdy odbiornik zgłasza odpowiadający im układ; nie zmienia to firmware dla dwóch nadajników w firmware dla czterech.

| Aktywne mikrofony | Cel dla parametrów wspólnych | Wyciszanie/włączanie dźwięku |
| --- | --- | --- |
| Dokładnie jeden | Wybierz ten pojedynczy mikrofon | Ten mikrofon |
| Co najmniej dwa | Wybierz **Wszystkie mikrofony** | Pojedynczy mikrofon albo grupa |

W znanym protokole wzmocnienie, redukcja szumu, EQ, pogłos i inne parametry urządzenia są **wspólnymi ustawieniami odbiornika**. Wybranie pojedynczego mikrofonu nie zapewnia niezależnego wzmocnienia ani EQ. Gdy aktywny jest tylko jeden mikrofon, edycja parametrów pod jego pozycją nadal zmienia ustawienia całego odbiornika; wpływ na nadajniki dołączające później nie został ustalony. Profile podlegają tym samym ograniczeniom wyboru celu.

Domyślnie wybór wartości tworzy szkic; dopiero **Zastosuj** wysyła zmianę. Automatyczne stosowanie jest opcjonalnym ustawieniem, domyślnie wyłączonym. Zmiany wymagają potwierdzenia polecenia i zgodnego odczytu z urządzenia. Zmiana odbiornika, wybranego mikrofonu/grupy lub składu aktywnych mikrofonów odrzuca niewysłane szkice i oczekujące zmiany automatyczne. **F5** również odrzuca szkice przed ponownym odczytem.

Grupowe wyciszanie/włączanie dźwięku wysyła polecenia kolejno do indeksów aktywnych mikrofonów, sprawdza potwierdzenia i wymaga końcowego odczytu potwierdzającego stan całej grupy. Grupa mieszana zostaje wyciszona; w grupie całkowicie wyciszonej dźwięk zostaje włączony. Operacja nie jest atomowa: błąd, rozłączenie lub anulowanie mogą pozostawić część mikrofonów ze zmienionym stanem. Ostrzeżenie o częściowym wykonaniu oznacza konieczność ponownego odczytu stanu; nie oznacza cofnięcia wcześniejszych zapisów.

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

Artefakty kompilacji trafiają do `D:\work\Workspace\<nazwa-katalogu-źródeł>`, a poprzednie buildy/wydania są przenoszone do `D:\work\Snapshots\<nazwa-katalogu-źródeł>`. Odpowiednik w `C:\work` jest używany wyłącznie wtedy, gdy dysk D: nie istnieje. Brak uprawnień lub miejsca na D: nie powoduje przełączenia na C:.

## Walidacja

Wersja 0.9.1 przeszła 34 663 kontrole pełnej regresji na osobnym pulpicie Windows. Osobno przeszły 24 kontrole współpracy procesów i 12 kontroli przywracania okien WPF. Zakresy częściowo się pokrywają. Rzeczywista mowa NVDA i fizyczne skutki ustawień mikrofonów wymagają prób ze sprzętem/użytkownikiem.

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

Hollylark używa HidSharp i NAudio. Zachowaj informacje o autorach i teksty ich licencji; zobacz [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt) oraz licencje bibliotek dostępne w oknie Pomocy aplikacji.

## Licencja

Hollylark jest udostępniany na licencji **GNU General Public License w wersji 3.0 (GPL-3.0-only)**. Pełny tekst znajduje się w [LICENSE](LICENSE). Zależności zachowują własne licencje. Źródła odpowiadające każdemu wydaniu portable są dostępne pod jego tagiem Git.
