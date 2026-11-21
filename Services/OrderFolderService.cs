using ClosedXML.Excel;
using MarkCheck.Data;

namespace MarkCheck.Services;

/// <summary>Печатная форма в общей папке.</summary>
public class OrderFileInfo
{
    /// <summary>Путь относительно общей папки — идентификатор файла для API.</summary>
    public string Path { get; set; }
    public string Name { get; set; }
    public string Folder { get; set; }
    public string Modified { get; set; }
    public bool Collected { get; set; }
}

/// <summary>
/// Работа с общей сетевой папкой печатных форм «Расходный ордер на товары».
/// Поиск — по всем подпапкам. Собранный файл остаётся на месте и получает префикс «СОБРАН - »,
/// рядом сохраняется «КИЗ - <имя>.xlsx» с кодами маркировки в одну колонку.
/// </summary>
public class OrderFolderService
{
    public const string CollectedPrefix = "СОБРАН - ";
    public const string KizPrefix = "КИЗ - ";
    private readonly Db _db;
    public OrderFolderService(Db db) { _db = db; }

    public string Root => (_db.GetSetting("orders_root", "") ?? "").Trim();

    public List<OrderFileInfo> List(bool includeCollected, out string error)
    {
        error = null;
        var root = Root;
        if (root.Length == 0) { error = "Не задана общая папка печатных форм (Администрирование)"; return new(); }
        if (!Directory.Exists(root)) { error = $"Папка недоступна: {root}"; return new(); }
        var opts = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, MatchCasing = MatchCasing.CaseInsensitive };
        var list = new List<OrderFileInfo>();
        foreach (var f in Directory.EnumerateFiles(root, "*.xlsx", opts))
        {
            var name = System.IO.Path.GetFileName(f);
            if (name.StartsWith("~$") || name.StartsWith(KizPrefix, StringComparison.OrdinalIgnoreCase)) continue;
            bool collected = name.StartsWith(CollectedPrefix, StringComparison.OrdinalIgnoreCase);
            if (collected && !includeCollected) continue;
            var rel = System.IO.Path.GetRelativePath(root, f);
            list.Add(new OrderFileInfo
            {
                Path = rel, Name = name, Collected = collected,
                Folder = System.IO.Path.GetDirectoryName(rel) ?? "",
                Modified = File.GetLastWriteTime(f).ToString("yyyy-MM-dd HH:mm")
            });
            if (list.Count >= 5000) break;
        }
        return list.OrderByDescending(x => x.Modified).ToList();
    }

    /// <summary>Абсолютный путь по относительному; защита от выхода за пределы общей папки.</summary>
    public string FullPath(string relative)
    {
        var root = System.IO.Path.GetFullPath(Root);
        var full = System.IO.Path.GetFullPath(System.IO.Path.Combine(root, relative ?? ""));
        var rootWithSep = root.EndsWith(System.IO.Path.DirectorySeparatorChar) ? root : root + System.IO.Path.DirectorySeparatorChar;
        if (!full.StartsWith(rootWithSep, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Файл вне общей папки");
        return full;
    }

    /// <summary>Читает файл в память, не блокируя его (файл может быть открыт в Excel у другого пользователя).</summary>
    public MemoryStream Read(string relative)
    {
        var ms = new MemoryStream();
        using (var fs = new FileStream(FullPath(relative), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            fs.CopyTo(ms);
        ms.Position = 0;
        return ms;
    }

    /// <summary>Сохраняет «КИЗ - имя.xlsx» рядом с исходником. Возвращает имя созданного файла.</summary>
    public string WriteKiz(string relative, IEnumerable<string> codes)
    {
        var full = FullPath(relative);
        var dir = System.IO.Path.GetDirectoryName(full);
        var baseName = StripPrefix(System.IO.Path.GetFileName(full));
        var kiz = System.IO.Path.Combine(dir, KizPrefix + baseName);
        using var wb = new XLWorkbook();
        var ws = wb.AddWorksheet("КИЗ");
        int r = 1;
        foreach (var code in codes)
        {
            var cell = ws.Cell(r++, 1);
            cell.SetValue(code);
            cell.Style.NumberFormat.Format = "@"; // текст: Excel не превратит код в число
        }
        ws.Column(1).AdjustToContents();
        wb.SaveAs(kiz);
        return System.IO.Path.GetFileName(kiz);
    }

    /// <summary>Добавляет префикс «СОБРАН - » к имени исходного файла. Возвращает новый относительный путь.</summary>
    public string MarkCollected(string relative)
    {
        var full = FullPath(relative);
        var name = System.IO.Path.GetFileName(full);
        if (name.StartsWith(CollectedPrefix, StringComparison.OrdinalIgnoreCase)) return relative;
        var dir = System.IO.Path.GetDirectoryName(full);
        var target = System.IO.Path.Combine(dir, CollectedPrefix + name);
        for (int i = 2; File.Exists(target); i++)
            target = System.IO.Path.Combine(dir, CollectedPrefix + System.IO.Path.GetFileNameWithoutExtension(name) + $" ({i})" + System.IO.Path.GetExtension(name));
        File.Move(full, target);
        return System.IO.Path.GetRelativePath(System.IO.Path.GetFullPath(Root), target);
    }

    private static string StripPrefix(string name) =>
        name.StartsWith(CollectedPrefix, StringComparison.OrdinalIgnoreCase) ? name[CollectedPrefix.Length..] : name;
}
