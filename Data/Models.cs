namespace MarkCheck.Data;

/// <summary>Номенклатура: связка кода 1С и GTIN.</summary>
public class Product
{
    public long Id { get; set; }
    public string Code1C { get; set; }
    public string Name { get; set; }
    public string Gtin { get; set; }
    /// <summary>Требуется ли маркировка (для товаров без [М] проверку в ЧЗ можно не делать).</summary>
    public bool Marked { get; set; } = true;
}

/// <summary>Проверяемый расходный ордер.</summary>
public class Order
{
    public long Id { get; set; }
    public string OrderNumber { get; set; }
    public string FileName { get; set; }
    public string Recipient { get; set; }
    public string Warehouse { get; set; }
    public string CreatedAt { get; set; }
    public string ClosedAt { get; set; }
    /// <summary>active | closed</summary>
    public string Status { get; set; }
    public string DocumentJson { get; set; }
}

/// <summary>Строка расходного ордера.</summary>
public class OrderLine
{
    public long Id { get; set; }
    public long OrderId { get; set; }
    public int LineNo { get; set; }
    public string Code1C { get; set; }
    public string Name { get; set; }
    public double Qty { get; set; }
    public string Unit { get; set; }
    public bool RequiresMarking { get; set; }
    /// <summary>GTIN конкретной характеристики товара (заполняется при загрузке ордера, если в справочнике
    /// для этого Code1C однозначно известен GTIN, или вручную через окно сопоставления). Основной
    /// ключ сопоставления скана со строкой ордера — точнее, чем сравнение по тексту наименования.
    public string Gtin { get; set; }
    // Расчётное поле (не хранится)
    public int ScannedQty { get; set; }
}

/// <summary>Отсканированный код маркировки.</summary>
public class ScannedItem
{
    public long Id { get; set; }
    public long OrderId { get; set; }
    public long? OrderLineId { get; set; }
    public long? ProductId { get; set; }
    public string DataMatrix { get; set; }
    public string Cis { get; set; }
    public string Gtin { get; set; }
    public string Serial { get; set; }
    /// <summary>ok | pending | error | setaside</summary>
    public string ValidationStatus { get; set; }
    public string ValidationMessage { get; set; }
    public string ProductName { get; set; }
    public string Code1C { get; set; }
    public bool IsMapped { get; set; }
    public bool SetAside { get; set; }
    public string CreatedAt { get; set; }
    public string RawResponse { get; set; }
}

/// <summary>Сохранённый отчёт по закрытому ордеру (генерируется один раз при закрытии, чтобы кнопки скачивания работали повторно).</summary>
public class OrderReport
{
    public long OrderId { get; set; }
    /// <summary>Только коды маркировки — для вставки в 1С.</summary>
    public string SimpleCsv { get; set; }
    /// <summary>Сводная + все коды со статусами — подробный отчёт.</summary>
    public string FullCsv { get; set; }
    public string CreatedAt { get; set; }
}

/// <summary>
/// Строка журнала ордеров с агрегатами для статистики.
/// Важно: запрос журнала должен быть типизированным (`Query&lt;JournalRow&gt;`).
/// Динамические строки Dapper (`DapperRow`) не имеют публичных свойств,
/// поэтому System.Text.Json сериализует их как пустые объекты `{}` — в браузере все поля
/// становятся `undefined` (статистика = NaN, ссылки с `undefined` вместо Id → HTTP 404).
/// </summary>
public class JournalRow
{
    public long Id { get; set; }
    /// <summary>Номер расходного ордера из 1С.</summary>
    public string OrderNumber { get; set; }
    public string FileName { get; set; }
    public string Recipient { get; set; }
    public string CreatedAt { get; set; }
    public string ClosedAt { get; set; }
    /// <summary>active | closed</summary>
    public string Status { get; set; }
    /// <summary>Количество строк товара в ордере.</summary>
    public int Lines { get; set; }
    /// <summary>Плановое количество единиц по всем строкам.</summary>
    public decimal PlanQty { get; set; }
    /// <summary>Успешно проверено кодов.</summary>
    public int OkQty { get; set; }
    /// <summary>Отложено / с ошибками.</summary>
    public int BadQty { get; set; }
}

/// <summary>Результат обработки сканирования, отправляемый в браузер.</summary>
public class ScanResult
{
    public string Status { get; set; }          // ok | pending | error | need_mapping | duplicate
    public string Message { get; set; }
    public long? ScanId { get; set; }
    public long? OrderId { get; set; }
    public long? OrderLineId { get; set; }
    public string Gtin { get; set; }
    public string Serial { get; set; }
    public string DataMatrix { get; set; }
    public string ProductName { get; set; }
    public string Code1C { get; set; }
    public bool NeedSetAside { get; set; }
}
