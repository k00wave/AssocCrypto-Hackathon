using System;
using System.IO;
using System.Text;
using System.Linq;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Diagnostics;
using BitVectorLib;

class Program
{
    static void Main(string[] args)
    {
        Console.WriteLine("Атака на ассоциативный механизм: восстановление ключа и сообщения");
        Console.WriteLine();

        // --------------------------------------------------------------------
        // 1. Определение путей к файлам
        //
        // Программа должна работать:
        //  - из среды разработки (Kali / исходный код),
        //  - из собранного EXE под Windows, где рядом лежит папка data.
        // Поэтому используем две стратегии поиска:
        //  1) data рядом с исполняемым файлом;
        //  2) data в корне решения (на несколько уровней выше).
        // --------------------------------------------------------------------
        string baseDir = AppContext.BaseDirectory;

        // Вариант для релизной сборки / Windows: папка data рядом с .exe
        string dataDirLocal = Path.Combine(baseDir, "data");
        // Вариант для разработки: папка data на уровне решения (Kali)
        string rootDirDev = Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", ".."));
        string dataDirDev = Path.Combine(rootDirDev, "data");

        // Выбор существующей директории данных
        string dataDir = Directory.Exists(dataDirLocal) ? dataDirLocal : dataDirDev;

        // Конкретные файлы
        string bookPath = Path.Combine(dataDir, "Selyankin_Kogda-truba-zovet.txt");
        string stegoPath = Path.Combine(dataDir, "stego_data_orig.bin");

        // Результаты атаки
        string recoveredKeyPath = Path.Combine(dataDir, "stego_key_recovered.bin");
        string recoveredMsgPath = Path.Combine(dataDir, "stego_message_recovered.txt");

        Console.WriteLine($"[Инфо] Путь к книге:      {bookPath}");
        Console.WriteLine($"[Инфо] Путь к стего-файлу: {stegoPath}");
        Console.WriteLine();

        // --------------------------------------------------------------------
        // 2. Проверка наличия входных файлов
        //
        // Важно: для удобства запуска под Windows добавлена пауза перед выходом,
        // чтобы окно консоли не закрывалось мгновенно при ошибке.
        // --------------------------------------------------------------------
        if (!File.Exists(bookPath))
        {
            Console.WriteLine("[Ошибка] Файл книги не найден:");
            Console.WriteLine(bookPath);
            Console.WriteLine();
            Console.WriteLine("Нажмите Enter для выхода...");
            Console.ReadLine();
            return;
        }

        if (!File.Exists(stegoPath))
        {
            Console.WriteLine("[Ошибка] Стего-файл не найден:");
            Console.WriteLine(stegoPath);
            Console.WriteLine();
            Console.WriteLine("Нажмите Enter для выхода...");
            Console.ReadLine();
            return;
        }

        // --------------------------------------------------------------------
        // 3. Читаем текст книги (открытый источник для поиска фрагмента)
        // --------------------------------------------------------------------
        string bookText = File.ReadAllText(bookPath, Encoding.UTF8);
        Console.WriteLine($"[Инфо] Длина книги (символов): {bookText.Length}");
        Console.WriteLine();

        // --------------------------------------------------------------------
        // 4. Формируем эталонные векторы (BitVector) для цифр 0–9
        //
        // Эти эталоны описаны в методичке и в демонстрационной программе.
        // Каждый эталон — битовый шаблон длиной etalonBits, по которому
        // строится ассоциативное соответствие с контейнерами.
        // --------------------------------------------------------------------
        string[] etalonStrings = GetDefaultEtalons10();
        int etalonBits = etalonStrings[0].Length;

        BitVector[] etalons = etalonStrings
            .Select(s => new BitVector(s))
            .ToArray();

        int containerByteLen = (etalonBits + 7) >> 3; // длина контейнера в байтах

        Console.WriteLine($"[Инфо] Количество эталонов: {etalons.Length}, длина каждого = {etalonBits} бит");
        Console.WriteLine();

        // Кэшируем байтовое представление эталонов для ускорения
        byte[][] etalonBytes = etalons
            .Select(e => e.ToByteArray())
            .ToArray();

        // --------------------------------------------------------------------
        // 5. Читаем стего-файл и оцениваем его структуру
        //
        // Стего-файл состоит из последовательности контейнеров одинаковой длины.
        // Каждый контейнер кодирует одну цифру (0–9) в виде ассоциативного
        // совпадения "контейнер ↔ эталон & маска".
        // --------------------------------------------------------------------
        byte[] stegoData = File.ReadAllBytes(stegoPath);
        PrintStegoInfo(stegoData, containerByteLen);

        int totalBytes = stegoData.Length;
        int containerCount = totalBytes / containerByteLen; // число контейнеров
        int digitsCount = containerCount;                   // цифр столько же, сколько контейнеров
        int hiddenBytes = digitsCount / 3;                  // каждые 3 цифры кодируют 1 байт (000–255)

        Console.WriteLine();
        Console.WriteLine($"[Инфо] Оценка скрытых данных: {hiddenBytes} байт полезного сообщения");
        Console.WriteLine();

        // --------------------------------------------------------------------
        // 6. Разбиваем стего-данные на BitVector-контейнеры
        //
        // Одновременно заранее кэшируем их байтовое представление для ускорения.
        // --------------------------------------------------------------------
        List<BitVector> containers = SplitStegoToContainers(stegoData, etalonBits);
        Console.WriteLine($"[Инфо] Контейнеров загружено: {containers.Count}");

        if (containers.Count != digitsCount)
            Console.WriteLine("[Предупреждение] containers.Count != digitsCount (возможное несовпадение размеров)");

        byte[][] containerBytes = containers
            .Select(c => c.ToByteArray())
            .ToArray();

        // --------------------------------------------------------------------
        // 7. Параллельный перебор фрагментов книги
        //
        // Идея:
        //   - длина скрытого сообщения известна (hiddenBytes),
        //   - предполагаем, что скрыт фрагмент книги длиной fragmentChars символов,
        //     который в UTF-8 даёт ровно hiddenBytes байт;
        //   - для каждого такого фрагмента строим последовательность цифр,
        //     восстанавливаем ключ и проверяем согласованность со стего-файлом.
        //
        // Перебор делается в Parallel.For, чтобы ускорить обработку за счёт
        // всех ядер процессора.
        // --------------------------------------------------------------------
        int fragmentChars = 1000;
        int maxStart = bookText.Length - fragmentChars;

        Console.WriteLine("[Инфо] Запуск параллельного перебора фрагментов книги...");
        Console.WriteLine($"       Длина фрагмента (символов): {fragmentChars}");
        Console.WriteLine($"       Длина сообщения (байт):     {hiddenBytes}");
        Console.WriteLine($"       Возможные стартовые позиции: 0..{maxStart}");
        Console.WriteLine();

        // Запускаем таймер для оценки времени работы атаки
        var sw = Stopwatch.StartNew();

        // Общие переменные результата атаки
        bool found = false;
        int bestStartIndex = -1;
        byte[] bestFragBytes = null;
        string bestFragText = null;
        BitVector[] bestKey = null;

        // Счётчик кандидатов с подходящей длиной в байтах ( т.е. bytes.Length == hiddenBytes )
        int candidateCount = 0;

        // Объект блокировки для доступа к общим переменным из параллельных потоков
        object lockObj = new object();

        // ------------------- ПАРАЛЛЕЛЬНЫЙ ПЕРЕБОР ---------------------------
        Parallel.For(0, maxStart + 1, (i, state) =>
        {
            // Если другой поток уже нашёл полностью согласованный кандидат — выходим
            if (Volatile.Read(ref found))
            {
                state.Stop();
                return;
            }

            // Выбираем фрагмент книги длиной fragmentChars символов
            string chunk = bookText.Substring(i, fragmentChars);
            byte[] fragBytes = Encoding.UTF8.GetBytes(chunk);

            // Нас интересуют только фрагменты, которые в UTF-8 дают ровно hiddenBytes байт.
            if (fragBytes.Length != hiddenBytes)
                return;

            // Увеличиваем счётчик подходящих по длине кандидатов
            Interlocked.Increment(ref candidateCount);

            // Строим "цифровое" представление байтов:
            // каждый байт (0–255) → три десятичные цифры [d2 d1 d0].
            int[] digits = BuildDigitsFromFragmentBytes(fragBytes);

            // Восстанавливаем ключ (маски) по всем контейнерам и предположенным цифрам.
            BitVector[] candidateKey = RecoverKeyFromDigitsAndContainers_Bytes(
                containerBytes,
                digits,
                etalonBytes,
                etalonBits);

            // Проверяем, что этот ключ СОВМЕСТИМ со всеми контейнерами
            // (то есть, если расшифровывать контейнеры этим ключом, то
            // мы получаем тот же набор цифр digits).
            bool ok = CheckCandidateAgainstStego_Bytes(
                containerBytes,
                digits,
                etalonBytes,
                candidateKey,
                containerByteLen);

            if (ok)
            {
                // Фиксируем результат только один раз (первый найденный согласованный кандидат).
                lock (lockObj)
                {
                    if (!found)
                    {
                        found = true;
                        bestStartIndex = i;
                        bestFragBytes = fragBytes;
                        bestFragText = chunk;
                        bestKey = candidateKey;
                        state.Stop(); // остановить дальнейший перебор
                    }
                }
            }
        });
        // ---------------- КОНЕЦ ПАРАЛЛЕЛЬНОГО ПЕРЕБОРА ----------------------

        sw.Stop();

        Console.WriteLine();
        Console.WriteLine($"[Инфо] Кандидатов с подходящей длиной (байт): {candidateCount}");
        Console.WriteLine($"[Инфо] Общее время атаки: {sw.Elapsed}");
        Console.WriteLine();

        if (!found || bestKey == null)
        {
            Console.WriteLine("[Результат] Последовательный и параллельный перебор не нашли полностью согласованный кандидат.");
            Console.WriteLine("Нажмите Enter для выхода...");
            Console.ReadLine();
            return;
        }

        // --------------------------------------------------------------------
        // 8. Вывод итогового результата: найденный фрагмент и характеристики ключа
        // --------------------------------------------------------------------
        Console.WriteLine("==============================================");
        Console.WriteLine("[Результат атаки] Найден согласованный кандидат!");
        Console.WriteLine($"  Стартовый индекс фрагмента в книге: {bestStartIndex}");
        Console.WriteLine();
        Console.WriteLine("  Статистика по маскам (ключу):");
        PrintKeyStats(bestKey, etalonBits);
        Console.WriteLine("==============================================");
        Console.WriteLine();

        // --------------------------------------------------------------------
        // 9. Сохраняем найденный ключ и скрытое сообщение в файлы
        // --------------------------------------------------------------------
        SaveKeyToFile(recoveredKeyPath, bestKey, etalonBits);
        Console.WriteLine($"[Файл] Восстановленный ключ сохранён в: {recoveredKeyPath}");

        File.WriteAllText(recoveredMsgPath, bestFragText, Encoding.UTF8);
        Console.WriteLine($"[Файл] Восстановленное скрытое сообщение сохранено в: {recoveredMsgPath}");
        Console.WriteLine();

        Console.WriteLine("=== Скрытое сообщения ===");
        Console.WriteLine(bestFragText);
        Console.WriteLine();

        // --------------------------------------------------------------------
        // 10. Дополнительная проверка:
        //
        // Декодируем стего-файл напрямую с помощью восстановленного ключа
        // и сравниваем результат с найденным фрагментом книги.
        //
        // Это даёт сильное криптоаналитическое обоснование корректности ключа:
        // - контейнеры → цифры → байты → текст,
        // - результат совпадает с фрагментом книги.
        // --------------------------------------------------------------------
        Console.WriteLine("[Проверка] Прямая дешифровка стего-файла с использованием восстановленного ключа...");

        byte[] decryptedBytes = DecryptFromStegoWithKey_Bytes(
            containerBytes,
            containerByteLen,
            etalonBits,
            etalonBytes,
            bestKey);

        bool same = decryptedBytes != null && bestFragBytes != null &&
                    decryptedBytes.SequenceEqual(bestFragBytes);

        Console.WriteLine($"[Проверка] Длина дешифрованных данных (байт): {decryptedBytes.Length}");
        Console.WriteLine($"[Проверка] Совпадает с найденным фрагментом книги: {same}");
        Console.WriteLine();

        string decryptedText = Encoding.UTF8.GetString(decryptedBytes);
        Console.WriteLine("=== Дешифрованный текст ===");
        Console.WriteLine(decryptedText);

        Console.WriteLine();
        Console.WriteLine("Нажмите Enter для выхода...");
        Console.ReadLine();
    }

    // ========================================================================
    // БЛОК ВСПОМОГАТЕЛЬНЫХ ФУНКЦИЙ
    // ========================================================================

    // ------------------------------------------------------------------------
    // ЭТАЛОНЫ (шаблоны) для цифр 0–9.
    //
    // Это фиксированные битовые строки длиной 78 бит, приведённые в методичке.
    // Они определяют, как "цифра" (0..9) отображается на ассоциативный вектор
    // через маски.
    // ------------------------------------------------------------------------
    static string[] GetDefaultEtalons10() =>
        new[]
        {
            new string("111111111111111111111111111111111111111111111111111111000000000000000000000000".Reverse().ToArray()),
            new string("000000000100000000000000000111111111111111111100000000000000000000000011111111".Reverse().ToArray()),
            new string("100000000000000000111111111111111111100000000111111111111111110000000000000000".Reverse().ToArray()),
            new string("100000000100000000111111111100000000100000000000000000111111111111111111111111".Reverse().ToArray()),
            new string("000000000111111111100000000111111111111111111100000000000000001111111100000000".Reverse().ToArray()),
            new string("100000000111111111111111111100000000111111111111111111000000001111111100000000".Reverse().ToArray()),
            new string("111111111100000000000000000100000000111111111111111111000000001111111111111111".Reverse().ToArray()),
            new string("111111111100000000111111111100000000000000000000000000000000000000000011111111".Reverse().ToArray()),
            new string("111111111111111111111111111111111111111111111111111111000000001111111100000000".Reverse().ToArray()),
            new string("100000000111111111111111111111111111100000000000000000111111111111111100000000".Reverse().ToArray()),
        };

    // ------------------------------------------------------------------------
    // Печать общей информации о стего-файле:
    // - общий размер в байтах;
    // - количество контейнеров;
    // - количество "цифр" (каждый контейнер → одна цифра);
    // - оценка числа скрытых байтов.
    // ------------------------------------------------------------------------
    static void PrintStegoInfo(byte[] stegoData, int containerByteLen)
    {
        int total = stegoData.Length;
        int containers = total / containerByteLen;
        int digits = containers;
        int hidden = digits / 3;

        Console.WriteLine("=== Информация о стего-файле ===");
        Console.WriteLine($"Общий размер (байт):     {total}");
        Console.WriteLine($"Число контейнеров:        {containers}");
        Console.WriteLine($"Число десятичных цифр:    {digits}");
        Console.WriteLine($"Оценка скрытых байтов:    {hidden}");
        Console.WriteLine("================================");
    }

    // ------------------------------------------------------------------------
    // Разбиение стего-данных на список BitVector-контейнеров.
    // Каждый контейнер имеет длину etalonBits (по битам).
    // ------------------------------------------------------------------------
    static List<BitVector> SplitStegoToContainers(byte[] stegoData, int etalonBits)
    {
        int len = (etalonBits + 7) >> 3; // длина одного контейнера в байтах
        int count = stegoData.Length / len;

        var list = new List<BitVector>(count);
        for (int i = 0; i < count; i++)
        {
            byte[] chunk = new byte[len];
            Buffer.BlockCopy(stegoData, i * len, chunk, 0, len);
            list.Add(new BitVector(chunk, etalonBits));
        }

        return list;
    }

    // ------------------------------------------------------------------------
    // Построение массива "цифр" из байтов фрагмента открытого текста.
    //
    // Каждый байт b (0..255) представляется как три десятичные цифры:
    //   d2 = b / 100
    //   d1 = (b / 10) % 10
    //   d0 = b % 10
    //
    // Итоговый массив digits содержит длину 3 * bytes.Length.
    // ------------------------------------------------------------------------
    static int[] BuildDigitsFromFragmentBytes(byte[] bytes)
    {
        int count = bytes.Length;
        int[] digits = new int[count * 3];

        for (int i = 0; i < count; i++)
        {
            int v = bytes[i];

            digits[i * 3] = v / 100;
            digits[i * 3 + 1] = (v / 10) % 10;
            digits[i * 3 + 2] = v % 10;
        }

        return digits;
    }

    // ------------------------------------------------------------------------
    // Восстановление ключа (масок) по:
    // - массиву контейнеров (в байтах),
    // - предполагаемому набору цифр (digits),
    // - эталонам (в байтах),
    // - длине эталона (количеству бит).
    //
    // Идея:
    //   Для каждой цифры d и каждого бита j:
    //     - предполагаем, что в маске key[d][j] может быть 1;
    //     - затем, проходя по всем контейнерам, относящимся к этой цифре,
    //       отбрасываем те позиции j, где (sBit != eBit).
    //   В конце оставшиеся позиции, не противоречащие наблюдённым контейнерам,
    //   считаем единицами в маске.
    // ------------------------------------------------------------------------
    static BitVector[] RecoverKeyFromDigitsAndContainers_Bytes(
        byte[][] containerBytes,
        int[] digits,
        byte[][] etalonBytes,
        int etalonBits)
    {
        int containerCount = digits.Length;
        int etalonCount = etalonBytes.Length;
        int byteLen = (etalonBits + 7) >> 3;

        // canBeOne[d, j] = true означает, что бит маски для цифры d в позиции j
        // ещё может быть равен 1 (не противоречит контейнерам).
        bool[,] canBeOne = new bool[etalonCount, etalonBits];

        for (int d = 0; d < etalonCount; d++)
            for (int j = 0; j < etalonBits; j++)
                canBeOne[d, j] = true;

        // Проходим по всем контейнерам и соответствующим цифрам
        for (int k = 0; k < containerCount; k++)
        {
            int d = digits[k];
            if (d < 0 || d >= etalonCount)
                continue;

            byte[] sBytes = containerBytes[k];
            byte[] eBytes = etalonBytes[d];

            int maxBits = Math.Min(etalonBits,
                Math.Min(sBytes.Length, eBytes.Length) * 8);

            for (int j = 0; j < maxBits; j++)
            {
                if (!canBeOne[d, j]) continue;

                int bi = j >> 3;
                int bt = j & 7;

                bool sj = (sBytes[bi] & (1 << bt)) != 0;
                bool ej = (eBytes[bi] & (1 << bt)) != 0;

                // Если контейнер и эталон в этой позиции всегда различаются,
                // то бит маски не может быть 1 (иначе ассоциативное совпадение
                // было бы невозможно).
                if (sj != ej)
                    canBeOne[d, j] = false;
            }
        }

        // По итогам canBeOne формируем битовые маски (BitVector) для каждой цифры
        BitVector[] key = new BitVector[etalonCount];

        for (int d = 0; d < etalonCount; d++)
        {
            byte[] kb = new byte[byteLen];

            for (int j = 0; j < etalonBits; j++)
            {
                if (!canBeOne[d, j]) continue;
                int bi = j >> 3;
                int bt = j & 7;
                kb[bi] |= (byte)(1 << bt);
            }

            key[d] = new BitVector(kb, etalonBits);
        }

        return key;
    }

    // ------------------------------------------------------------------------
    // Проверка кандидата (ключа) на согласованность со стего-файлом.
    //
    // Проверяем:
    //   Для каждого контейнера k:
    //     - с помощью DiscloseDigit_Bytes восстанавливаем цифру через эталоны и ключ;
    //     - сравниваем с ожидаемой digits[k].
    // Если все совпадают — ключ согласован.
    // ------------------------------------------------------------------------
    static bool CheckCandidateAgainstStego_Bytes(
        byte[][] containerBytes,
        int[] digits,
        byte[][] etalonBytes,
        BitVector[] key,
        int containerByteLen)
    {
        // Кэшируем байты масок (ключа)
        byte[][] keyBytes = key
            .Select(kv => kv.ToByteArray())
            .ToArray();

        for (int k = 0; k < digits.Length; k++)
        {
            int expected = digits[k];
            int actual = DiscloseDigit_Bytes(
                containerBytes[k],
                etalonBytes,
                keyBytes,
                containerByteLen);

            if (actual != expected)
                return false;
        }

        return true;
    }

    // ------------------------------------------------------------------------
    // "Раскрытие" одной цифры по контейнеру, эталонам и маскам.
    //
    // Для каждой цифры d:
    //   - вычисляем (container & mask[d]) и (etalon[d] & mask[d]);
    //   - если все биты совпадают, считаем, что контейнер соответствует цифре d.
    //
    // Если ни одна цифра не подошла, возвращается -1.
    // ------------------------------------------------------------------------
    static int DiscloseDigit_Bytes(
        byte[] sBytes,
        byte[][] etalonBytes,
        byte[][] keyBytes,
        int containerByteLen)
    {
        for (int d = 0; d < etalonBytes.Length; d++)
        {
            byte[] eBytes = etalonBytes[d];
            byte[] kBytesD = keyBytes[d];

            bool match = true;
            for (int j = 0; j < containerByteLen; j++)
            {
                byte s = sBytes[j];
                byte e = j < eBytes.Length ? eBytes[j] : (byte)0;
                byte m = j < kBytesD.Length ? kBytesD[j] : (byte)0;

                byte sMasked = (byte)(s & m);
                byte eMasked = (byte)(e & m);

                if (sMasked != eMasked)
                {
                    match = false;
                    break;
                }
            }

            if (match)
                return d;
        }

        return -1;
    }

    // ------------------------------------------------------------------------
    // Подсчёт количества единиц в каждой маске (для печати статистики).
    //
    // Показывает "разреженность" или "плотность" маски:
    //   Mask[i]: ones=.../etalonBits
    // ------------------------------------------------------------------------
    static void PrintKeyStats(BitVector[] key, int etalonBits)
    {
        for (int i = 0; i < key.Length; i++)
        {
            byte[] b = key[i].ToByteArray();
            int ones = 0;
            int maxBits = Math.Min(etalonBits, b.Length * 8);

            for (int j = 0; j < maxBits; j++)
            {
                int bi = j >> 3;
                int bt = j & 7;
                if ((b[bi] & (1 << bt)) != 0)
                    ones++;
            }

            Console.WriteLine($"Mask[{i}]: количество единиц = {ones}/{etalonBits}");
        }
    }

    // ------------------------------------------------------------------------
    // Прямая дешифровка стего-файла с использованием уже восстановленного ключа.
    //
    // Шаги:
    //  1) Для каждого контейнера восстанавливаем цифру d (0..9).
    //  2) Накопленные цифры группируем по 3 → восстанавливаем байты.
    //  3) Байты интерпретируются как UTF-8 текст.
    // ------------------------------------------------------------------------
    static byte[] DecryptFromStegoWithKey_Bytes(
        byte[][] containerBytes,
        int containerByteLen,
        int etalonBits,
        byte[][] etalonBytes,
        BitVector[] key)
    {
        int C = containerBytes.Length;
        var digits = new List<int>(C);

        byte[][] keyBytes = key
            .Select(kv => kv.ToByteArray())
            .ToArray();

        for (int k = 0; k < C; k++)
        {
            int d = DiscloseDigit_Bytes(
                containerBytes[k],
                etalonBytes,
                keyBytes,
                containerByteLen);

            if (d < 0)
                break;

            digits.Add(d);
        }

        return DigitsToBytes(digits);
    }

    // ------------------------------------------------------------------------
    // Преобразование списка цифр (длина кратна 3) обратно в байтовый массив.
    //
    // Каждые три цифры [d2, d1, d0] превращаются в:
    //   val = d2*100 + d1*10 + d0;
    // Если val > 255, для безопасности ставим 0.
    // ------------------------------------------------------------------------
    static byte[] DigitsToBytes(List<int> digits)
    {
        int N = digits.Count / 3;
        byte[] result = new byte[N];

        for (int i = 0; i < N; i++)
        {
            int d2 = digits[i * 3];
            int d1 = digits[i * 3 + 1];
            int d0 = digits[i * 3 + 2];

            int val = d2 * 100 + d1 * 10 + d0;
            if (val > 255) val = 0;
            result[i] = (byte)val;
        }

        return result;
    }

    // ------------------------------------------------------------------------
    // Сохранение ключа в бинарный файл в формате, совместимом с демонстрационной
    // программой (AssocStegoDemo / AssocStego).
    //
    // Формат:
    //   MAGIC (4 байта)
    //   count (4 байта)  - количество масок
    //   etalonBits (4 байта)
    //   для каждой маски:
    //       length (4 байта) - длина массива байт
    //       bytes[length]    - содержимое
    // ------------------------------------------------------------------------
    static void SaveKeyToFile(string path, BitVector[] key, int etalonBits)
    {
        const int MAGIC = 0x41534B59; // 'A','S','K','Y'

        using var fs = File.Create(path);
        using var bw = new BinaryWriter(fs);

        bw.Write(MAGIC);
        bw.Write(key.Length);
        bw.Write(etalonBits);

        foreach (var kv in key)
        {
            byte[] b = kv.ToByteArray();
            bw.Write(b.Length);
            bw.Write(b);
        }
    }
}
