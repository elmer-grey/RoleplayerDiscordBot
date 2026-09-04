namespace RPBot.VtM;

/// <summary>
/// 30 способностей V20 (Таланты + Навыки + Знания), все стартуют с 0.
/// </summary>
/// <remarks>
/// <para>VtM V20 (стр. 81): все способности стартуют с 0 пунктов.
/// На Шаге 3 максимум 3, далее поднимаются свободными пунктами (Шаг 5).
/// Имена свойств совпадают с русскими именами из
/// <see cref="VampireAbilitiesCatalog"/> (с пробелами в CamelCase),
/// чтобы резолвер мог доставать значение по имени через switch.</para>
/// </remarks>
public class VampireAbilities
{
    // ── Таланты (10) ───────────────────────────────────────────────────
    public int Атлетика { get; set; }
    public int Бдительность { get; set; }
    public int Драка { get; set; }
    public int Запугивание { get; set; }
    public int Красноречие { get; set; }
    public int Лидерство { get; set; }
    public int УличноеЧутьё { get; set; }
    public int Хитрость { get; set; }
    public int ШестоеЧувство { get; set; }
    public int Эмпатия { get; set; }

    // ── Навыки (10) ────────────────────────────────────────────────────
    public int Вождение { get; set; }
    public int Воровство { get; set; }
    public int Выживание { get; set; }
    public int Исполнение { get; set; }
    public int ОбращениеСЖивотными { get; set; }
    public int Ремесло { get; set; }
    public int Скрытность { get; set; }
    public int Стрельба { get; set; }
    public int Фехтование { get; set; }
    public int Этикет { get; set; }

    // ── Знания (10) ────────────────────────────────────────────────────
    public int ГуманитарныеНауки { get; set; }
    public int ЕстественныеНауки { get; set; }
    public int Информатика { get; set; }
    public int Медицина { get; set; }
    public int Оккультизм { get; set; }
    public int Политика { get; set; }
    public int Расследование { get; set; }
    public int Финансы { get; set; }
    public int Электроника { get; set; }
    public int Юриспруденция { get; set; }
}
