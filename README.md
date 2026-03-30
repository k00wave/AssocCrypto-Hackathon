# AssocCrypto-Hackathon
Участник II Онлайн-хакатона по параллельному программированию на языке C# приурочен к 80-летию Победы в Великой Отечественной войне и посвящен выявлению секретного ключа ассоциативного механизма защиты данных с применением технологий параллельного программирования на языке C# 

https://github.com/CSharpCooking/CSharpCooking.github.io/discussions/11#discussioncomment-15059421 

Решение атаки на "ассоциативный механизм защиты данных" от команды №1 | Хорочкин Артём Владиславович | Хаертдинов Тимур  Низамович | Вазыхов Марсель Айдарович | Казанский национальный исследовательский технический университет им. А.Н. Туполева – КАИ
Задача:
По известному контейнеру (stego-файл) и открытой книге (book) восстановить секретный ключ (набор масок BitVector для каждой цифры 0–9).
Восстановить скрытое сообщение, которое было зашифровано с помощью эталонов и масок.
Подход:
Книга рассматривается как источник потенциальных открытых текстов.
Перебираются фрагменты книги фиксированной длины (1000 символов),для каждого фрагмента считаются "десятичные цифры" его байтов (0–255 → 3 десятичные цифры).
Сопоставляя полученные цифры с битами контейнеров, восстанавливаем маски (ключ).
Проверяем, что этот ключ полностью согласован со всеми контейнерами.
При успехе считаем, что найден реальный скрытый фрагмент и ключ.
Дополнительно выполняем прямую дешифровку стего с восстановленным ключом и сверяем результат с найденным фрагментом книги.
Структура проекта:
README.md - текущий файл
Исходный код.cs — ОСНОВНАЯ программа атаки (код программы)
Характеристики(VirtualBox+Kali Linux).txt
Характеристики(Windows 11).txt
Восстановленный секретный ключ.bin
Восстановленный секретный ключ - HEX.txt - восстановленный секретный ключ в виде hex байтов
Видео-демонстрация работы программ (VirtualBox+KaliLinux и Windows 11).mp4
Временные метрики.txt
Презентация.pptx
AssocHack/  - исходный проект реализации на VirtualBox + KaliLinux
│
├── BitVector/
│   └── src/BitVectorLib/
│       ├── BitVector.cs      — собственная библиотека для битовых векторов
│       └── BitVectorLib.csproj
│
├── doc/
│   ├── Searching-for-Security-Vulnerabilities.cs  
│   └── Searching-for-Security-Vulnerabilities.xlsx  - частотный анализ  
│  
├── data/
│   ├── Selyankin_Kogda-truba-zovet.txt    — книга (открытый текст)
│   ├── stego_data_orig.bin                — стего-файл (вход для атаки)
│   ├── stego_key_recovered.bin            — *восстановленный ключ при сборке будет лежать здесь
│   └── stego_message_recovered.txt        — *восстановленное сообщение при сборке будет лежать здесь
│
│
├── src/
│   ├── AssocStegoDemo/
│   │   ├── Program.cs         — демо кода
│   │   └── AssocStegoDemo.csproj
│   │
│   └──── AssocStegoAttack/
│       ├── Program.cs         — ОСНОВНАЯ программа атаки (код программы)
│       └── AssocStegoAttack.csproj
│
└── AssocStegoSolution.sln   — решение Visual Studio / dotnet
release_win/  - исходный проект реализации на Windows11
│
├── data/
│   ├── Selyankin_Kogda-truba-zovet.txt    — книга (открытый текст)
│   ├── stego_data_orig.bin                — стего-файл (вход для атаки)
│   ├── stego_key_recovered.bin            — *восстановленный ключ при сборке будет лежать здесь
│   └── stego_message_recovered.txt        — *восстановленное сообщение при сборке будет лежать здесь
│
├── AssocStegoAttack.exe
│
└── Program.cs  — ОСНОВНАЯ программа атаки (код программы)
Реализация проекта:
Для Linux (.NET v8.0)
sudo apt install dotnet-sdk-8.0                                    - установка .NET SDK
dotnet build AssocStegoSolution.sln                                - сборка решения
dotnet run --project src/AssocStegoAttack/AssocStegoAttack.csproj  - запуск атаки
Для Windows (.NET v8.0.100)
Для Windows систем собрано решение в каталоге realise_win, консольное приложение .exe, запустить.
Результат запуска проекта появятся:
файл stego_key_recovered.bin      - сохраняется в каталоге data
файл stego_message_recovered.txt  - cохраняется в каталоге data
вывод на экран:
найденный фрагмент книги,
восстановленное сообщение,
проверка корректности,
статистика масок,
время выполнения.
Команды сборки и реализации проекта:
Собираем проект и запускаем на Linux/ KaliLinux
dotnet build AssocStegoSolution.sln
dotnet run --project src/AssocStegoAttack/AssocStegoAttack.csproj
Собираем и создаем .exe для запуска на Windows
dotnet publish src/AssocStegoAttack/AssocStegoAttack.csproj   
-c Release \  
-r win-x64 \  
--self-contained true   
/p:PublishSingleFile=true   
/p:IncludeNativeLibrariesForSelfExtract=true   
/p:DebugType=None   
-o release_win
Подготавливаем папку release_win/data и копируем туда необходимые входные файлы программы
mkdir release_win/data
cp data/Selyankin_Kogda-truba-zovet.txt release_win/data/
cp data/stego_data_orig.bin release_win/data/
