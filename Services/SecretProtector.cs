using System.Security.Cryptography;

namespace MarkCheck.Services;

/// <summary>
/// Шифрование секретных настроек (URL вебхука Битрикс24 с токеном) перед сохранением в БД.
/// Алгоритм перенесён из WbPackStation без изменений — в объединённой платформе хранилище секретов будет общим.
/// AES-GCM 256 бит из стандартной библиотеки .NET, без привязки к Windows/DPAPI.
/// привязки к Windows/DPAPI — проект целится в net8.0, не net8.0-windows).
///
/// Ключ шифрования генерируется один раз и хранится ЛОКАЛЬНО НА МАШИНЕ (файл data/secret.key,
/// 32 случайных байта), НЕ в git и НЕ в самой БД рядом с зашифрованными данными — иначе шифрование
/// было бы бессмысленным. Файл создаётся автоматически при первом обращении, если ещё не существует.
///
/// Формат хранения зашифрованного значения (одна строка, Base64):
///   base64(nonce(12 байт) + ciphertext + tag(16 байт))
/// </summary>
public class SecretProtector
{
    private const int KeySizeBytes = 32; // 256 бит
    private const int NonceSizeBytes = 12; // рекомендованный размер nonce для AES-GCM
    private const int TagSizeBytes = 16;

    private readonly byte[] _key;

    public SecretProtector(string dataDir)
    {
        Directory.CreateDirectory(dataDir);
        var keyPath = Path.Combine(dataDir, "secret.key");

        if (File.Exists(keyPath))
        {
            _key = File.ReadAllBytes(keyPath);
            if (_key.Length != KeySizeBytes)
                throw new InvalidOperationException($"Файл ключа шифрования '{keyPath}' повреждён (неверная длина ключа).");
        }
        else
        {
            _key = new byte[KeySizeBytes];
            RandomNumberGenerator.Fill(_key);
            File.WriteAllBytes(keyPath, _key);
        }
    }

    /// <summary>Шифрует открытый текст, возвращает Base64-строку для хранения в БД.</summary>
    public string Encrypt(string plaintext)
    {
        var plainBytes = System.Text.Encoding.UTF8.GetBytes(plaintext ?? "");
        var nonce = new byte[NonceSizeBytes];
        RandomNumberGenerator.Fill(nonce);
        var ciphertext = new byte[plainBytes.Length];
        var tag = new byte[TagSizeBytes];

        using (var aes = new AesGcm(_key, TagSizeBytes))
        {
            aes.Encrypt(nonce, plainBytes, ciphertext, tag);
        }

        var result = new byte[NonceSizeBytes + ciphertext.Length + TagSizeBytes];
        Buffer.BlockCopy(nonce, 0, result, 0, NonceSizeBytes);
        Buffer.BlockCopy(ciphertext, 0, result, NonceSizeBytes, ciphertext.Length);
        Buffer.BlockCopy(tag, 0, result, NonceSizeBytes + ciphertext.Length, TagSizeBytes);

        return Convert.ToBase64String(result);
    }

    /// <summary>Расшифровывает значение, полученное из Encrypt(). Бросает исключение при повреждённых данных.</summary>
    public string Decrypt(string ciphertextBase64)
    {
        var data = Convert.FromBase64String(ciphertextBase64);
        if (data.Length < NonceSizeBytes + TagSizeBytes)
            throw new InvalidOperationException("Зашифрованное значение повреждено (недостаточная длина).");

        var nonce = new byte[NonceSizeBytes];
        var tag = new byte[TagSizeBytes];
        var ciphertextLength = data.Length - NonceSizeBytes - TagSizeBytes;
        var ciphertext = new byte[ciphertextLength];

        Buffer.BlockCopy(data, 0, nonce, 0, NonceSizeBytes);
        Buffer.BlockCopy(data, NonceSizeBytes, ciphertext, 0, ciphertextLength);
        Buffer.BlockCopy(data, NonceSizeBytes + ciphertextLength, tag, 0, TagSizeBytes);

        var plainBytes = new byte[ciphertextLength];
        using (var aes = new AesGcm(_key, TagSizeBytes))
        {
            aes.Decrypt(nonce, ciphertext, tag, plainBytes);
        }

        return System.Text.Encoding.UTF8.GetString(plainBytes);
    }
}
